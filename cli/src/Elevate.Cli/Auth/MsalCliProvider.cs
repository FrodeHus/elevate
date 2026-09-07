using System.Security.Claims;
using Elevate.Cli.Infrastructure;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Microsoft.Identity.Client;

namespace Elevate.Cli.Auth;

/// <summary>How the CLI runs an interactive sign-in.</summary>
public enum InteractiveFlow
{
    /// <summary>The system browser, returning to <c>http://localhost</c>. The default on a desktop.</summary>
    Browser,

    /// <summary>The device code flow: a code to type at microsoft.com/devicelogin on any device. For SSH and containers.</summary>
    DeviceCode,
}

/// <summary>
/// One MSAL public client, for one client id, as an <see cref="ITokenProvider"/>. The own-app
/// registration asks for the PIM scopes by name; the Azure CLI, Azure PowerShell and custom
/// registrations ask for each resource's <c>.default</c>, since what they may do is whatever was
/// consented to them. A port of the desktop apps' MSAL providers without a window to anchor to:
/// interaction goes through the system browser or a device code, never a broker.
/// </summary>
public sealed class MsalCliProvider : ITokenProvider
{
    private const string GraphDefault = "https://graph.microsoft.com/.default";

    private readonly IPublicClientApplication _app;
    private readonly TokenCacheStore _cache;
    private readonly InteractiveGate _gate;
    private readonly InteractiveFlow _flow;
    private readonly Action<string> _say;
    private readonly Lazy<Task> _registered;

    public MsalCliProvider(SignInMethod method, string clientId, TokenCacheStore cache, InteractiveGate gate, InteractiveFlow flow, Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(say);
        if (!CliSettings.IsValidClientId(clientId))
        {
            throw new CliException("The application (client) ID must be a GUID.", ExitCodes.Usage);
        }

        Method = method;
        ClientId = clientId.Trim();
        _cache = cache;
        _gate = gate;
        _flow = flow;
        _say = say;
        _app = Build(ClientId);
        _registered = new Lazy<Task>(() => cache.RegisterAsync(_app));
    }

    public SignInMethod Method { get; }

    public string ClientId { get; }

    private IReadOnlyList<string> SignInScopes => Method.UsesMsal ? [Scopes.GraphUserRead] : [GraphDefault];

    private IReadOnlyList<string> Requested(IReadOnlyList<string> scopes) => Method.UsesMsal ? scopes : [ResourceDefault(scopes)];

    public async Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct = default)
    {
        if (method != Method)
        {
            throw new PimException(PimErrorKind.Unexpected, $"This provider signs in with the {Method.DisplayName} only");
        }

        await _registered.Value.ConfigureAwait(false);
        var result = await _gate.RunAsync(() => InteractiveAsync(SignInScopes, null, null, null, ct), ct).ConfigureAwait(false);
        return IdentityFrom(result.Account, result.ClaimsPrincipal);
    }

    public async Task SignOutAsync(Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await _registered.Value.ConfigureAwait(false);
        if (await FindAccountAsync(identity).ConfigureAwait(false) is { } account)
        {
            await Run(() => _app.RemoveAsync(account)).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct = default)
    {
        await _registered.Value.ConfigureAwait(false);
        var accounts = await Run(() => _app.GetAccountsAsync()).ConfigureAwait(false);
        return [.. accounts.Select(a => IdentityFrom(a, null))];
    }

    public async Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await _registered.Value.ConfigureAwait(false);
        var account = await FindAccountAsync(identity).ConfigureAwait(false)
            ?? throw new PimException(PimErrorKind.InteractionRequired);
        var result = await Run(() => _app.AcquireTokenSilent(Requested(scopes), account)
            .WithTenantId(tenantId)
            .ExecuteAsync(ct)).ConfigureAwait(false);
        return result.AccessToken;
    }

    public async Task<string> AcquireInteractivelyAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await _registered.Value.ConfigureAwait(false);
        var account = await FindAccountAsync(identity).ConfigureAwait(false);
        var result = await _gate.RunAsync(
            () => InteractiveAsync(Requested(scopes), account, tenantId, claims, ct), ct).ConfigureAwait(false);
        return result.AccessToken;
    }

    private Task<AuthenticationResult> InteractiveAsync(IReadOnlyList<string> scopes, IAccount? account, string? tenantId, string? claims, CancellationToken ct)
    {
        var who = account?.Username is { } name ? $" as {name}" : string.Empty;
        var where = tenantId is not null ? $" for tenant {tenantId}" : string.Empty;
        return Run(() =>
        {
            if (_flow == InteractiveFlow.DeviceCode)
            {
                var device = _app.AcquireTokenWithDeviceCode(scopes, callback =>
                {
                    _say(callback.Message);
                    return Task.CompletedTask;
                });
                if (tenantId is not null)
                {
                    device = device.WithTenantId(tenantId);
                }

                if (!string.IsNullOrEmpty(claims))
                {
                    device = device.WithClaims(claims);
                }

                return device.ExecuteAsync(ct);
            }

            _say($"Opening the browser to sign in{who}{where}…");
            var builder = _app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .WithPrompt(account is null ? Prompt.SelectAccount : Prompt.NoPrompt);
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

            return builder.ExecuteAsync(ct);
        });
    }

    private async Task<IAccount?> FindAccountAsync(Identity identity)
    {
        try
        {
            return await _app.GetAccountAsync(identity.Id).ConfigureAwait(false);
        }
        catch (MsalException)
        {
            return null;
        }
    }

    private Identity IdentityFrom(IAccount account, ClaimsPrincipal? claims)
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

    /// <summary><c>https://host/.default</c> for the resource the scopes belong to.</summary>
    public static string ResourceDefault(IReadOnlyList<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var first = scopes.FirstOrDefault(s => s.Contains("://", StringComparison.Ordinal));
        if (first is not null && Uri.TryCreate(first, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Host}/.default";
        }

        return GraphDefault;
    }

    private static IPublicClientApplication Build(string clientId)
    {
        try
        {
            return PublicClientApplicationBuilder.Create(clientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, "organizations")
                .WithRedirectUri("http://localhost")
                .Build();
        }
        catch (MsalException e)
        {
            throw Map(e);
        }
    }

    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not PimException and not CliException)
        {
            throw Map(e);
        }
    }

    private static async Task Run(Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not PimException and not CliException)
        {
            throw Map(e);
        }
    }

    /// <summary>The desktop apps' MSAL error mapping, so the same failure reads the same everywhere.</summary>
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

    private static string FirstLine(string text)
    {
        var line = (text ?? string.Empty).Split('\n', 2)[0].Trim();
        return line.Length == 0 ? "Unknown MSAL failure" : line;
    }
}

/// <summary>Serialises interactive sign-ins, so two tenants needing a prompt queue instead of racing for the browser.</summary>
public sealed class InteractiveGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
