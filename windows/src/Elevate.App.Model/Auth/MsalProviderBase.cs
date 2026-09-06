using System.Security.Claims;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Microsoft.Identity.Client;

namespace Elevate.App.Auth;

/// <summary>
/// What the own-app and first-party providers share: one MSAL public client, the on-disk cache,
/// the interactive gate, identity mapping and error mapping.
/// </summary>
public abstract class MsalProviderBase : ITokenProvider
{
    private readonly TokenCache _cache;
    private readonly InteractiveGate _gate;
    private readonly Lazy<Task> _registered;

    protected MsalProviderBase(IPublicClientApplication app, TokenCache cache, InteractiveGate gate)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(gate);
        App = app;
        _cache = cache;
        _gate = gate;
        _registered = new Lazy<Task>(() => cache.RegisterAsync(app));
    }

    protected IPublicClientApplication App { get; }

    /// <summary>The sign-in method stamped on identities this provider returns.</summary>
    protected abstract SignInMethod Method { get; }

    /// <summary>Scopes requested at first sign-in.</summary>
    protected abstract IReadOnlyList<string> SignInScopes { get; }

    /// <summary>The scopes actually sent for a resource read; first-party clients use <c>.default</c>.</summary>
    protected abstract IReadOnlyList<string> Requested(IReadOnlyList<string> scopes);

    public async Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct)
    {
        if (method != Method)
        {
            throw new PimException(PimErrorKind.Unexpected, $"This provider signs in with the {Method.DisplayName} only");
        }

        await EnsureCacheAsync().ConfigureAwait(false);
        var result = await _gate.RunAsync(() => Interactive(SignInScopes, account: null, tenantId: null, claims: null, Prompt.SelectAccount, ct), ct)
            .ConfigureAwait(false);
        return IdentityFrom(result.Account, result.ClaimsPrincipal);
    }

    public async Task SignOutAsync(Identity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await EnsureCacheAsync().ConfigureAwait(false);
        if (await FindAccountAsync(identity).ConfigureAwait(false) is { } account)
        {
            await Run(() => App.RemoveAsync(account)).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct)
    {
        await EnsureCacheAsync().ConfigureAwait(false);
        var accounts = await Run(() => App.GetAccountsAsync()).ConfigureAwait(false);
        return [.. accounts.Select(a => IdentityFrom(a, null))];
    }

    public async Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await EnsureCacheAsync().ConfigureAwait(false);
        var account = await FindAccountAsync(identity).ConfigureAwait(false)
            ?? throw new PimException(PimErrorKind.InteractionRequired);
        var result = await Run(() => App.AcquireTokenSilent(Requested(scopes), account)
            .WithTenantId(tenantId)
            .ExecuteAsync(ct)).ConfigureAwait(false);
        return result.AccessToken;
    }

    public async Task<string> AcquireInteractivelyAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await EnsureCacheAsync().ConfigureAwait(false);
        var account = await FindAccountAsync(identity).ConfigureAwait(false);
        var result = await _gate.RunAsync(
            () => Interactive(Requested(scopes), account, tenantId, claims, account is null ? Prompt.SelectAccount : Prompt.NoPrompt, ct), ct)
            .ConfigureAwait(false);
        return result.AccessToken;
    }

    protected Task EnsureCacheAsync() => _registered.Value;

    protected async Task<IAccount?> FindAccountAsync(Identity identity)
    {
        try
        {
            return await App.GetAccountAsync(identity.Id).ConfigureAwait(false);
        }
        catch (MsalException)
        {
            return null;
        }
    }

    /// <summary>Hook for the interactive builder: the broker or the system browser.</summary>
    protected abstract AcquireTokenInteractiveParameterBuilder Configure(AcquireTokenInteractiveParameterBuilder builder);

    private Task<AuthenticationResult> Interactive(IReadOnlyList<string> scopes, IAccount? account, string? tenantId, string? claims, Prompt prompt, CancellationToken ct)
    {
        return Run(() =>
        {
            var builder = App.AcquireTokenInteractive(scopes).WithPrompt(prompt);
            if (account is not null)
            {
                builder = builder.WithAccount(account);
            }

            if (tenantId is not null)
            {
                builder = builder.WithTenantId(tenantId);
            }

            if (!string.IsNullOrEmpty(claims))
            {
                builder = builder.WithClaims(claims);
            }

            return Configure(builder).ExecuteAsync(ct);
        });
    }

    /// <summary>Runs an MSAL call, translating its failures into <see cref="PimException"/>s.</summary>
    protected static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not PimException)
        {
            throw Map(e);
        }
    }

    protected static async Task Run(Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not PimException)
        {
            throw Map(e);
        }
    }

    protected Identity IdentityFrom(IAccount account, ClaimsPrincipal? claims)
    {
        var name = claims?.FindFirst("name")?.Value;
        var tenant = claims?.FindFirst("tid")?.Value;
        return new Identity(
            account.HomeAccountId?.Identifier ?? account.Username ?? Guid.NewGuid().ToString(),
            account.Username ?? "unknown",
            name ?? account.Username ?? "unknown",
            account.HomeAccountId?.TenantId ?? tenant ?? string.Empty,
            Method);
    }

    /// <summary>The Swift <c>MSALTokenProvider.map</c>, for MSAL.NET's exception types.</summary>
    public static PimException Map(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        switch (error)
        {
            case MsalUiRequiredException:
                return new PimException(PimErrorKind.InteractionRequired);
            case MsalClientException client when client.ErrorCode == MsalError.AuthenticationCanceledError:
                return new PimException(PimErrorKind.Network, "Sign-in cancelled");
            case MsalServiceException service:
            {
                var text = service.Message ?? string.Empty;
                if (text.Contains("AADSTS65001", StringComparison.Ordinal)
                    || text.Contains("AADSTS65004", StringComparison.Ordinal)
                    || text.Contains("AADSTS90094", StringComparison.Ordinal)
                    || text.Contains("consent_required", StringComparison.Ordinal))
                {
                    return new PimException(PimErrorKind.ConsentRequired);
                }

                return new PimException(PimErrorKind.Network, FirstLine(text));
            }

            case MsalException msal:
                return new PimException(PimErrorKind.Network, FirstLine(msal.Message));
            default:
                return new PimException(PimErrorKind.Network, FirstLine(error.Message));
        }
    }

    /// <summary>MSAL messages carry paragraphs of guidance; the first line is the one that names the failure.</summary>
    private static string FirstLine(string text)
    {
        var line = (text ?? string.Empty).Split('\n', 2)[0].Trim();
        return line.Length == 0 ? "Unknown MSAL failure" : line;
    }
}
