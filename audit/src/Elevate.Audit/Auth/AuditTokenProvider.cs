using Elevate.Audit.Infrastructure;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Microsoft.Identity.Client;

namespace Elevate.Audit.Auth;

/// <summary>
/// Two MSAL public clients, one per resource, with an in-memory token cache only: a scan signs in,
/// runs, and exits. Graph uses <see cref="ClientIds.GraphDefault"/> (or <c>--client-id</c>); ARM uses
/// the Azure CLI client, as Elevate's Azure CLI sign-in method does. The Identity and tenant the
/// transport passes are ignored: this provider knows one account and one tenant.
/// </summary>
public sealed class AuditTokenProvider : ITokenProvider
{
    private readonly IPublicClientApplication _graph;
    private readonly IPublicClientApplication _arm;
    private readonly bool _customGraph;
    private readonly bool _deviceCode;
    private readonly Action<string> _say;
    private readonly SemaphoreSlim _interactive = new(1, 1);
    private IAccount? _graphAccount;
    private IAccount? _armAccount;

    public AuditTokenProvider(string? graphClientId, string tenant, bool deviceCode, Action<string> say)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentNullException.ThrowIfNull(say);
        if (graphClientId is not null && !ClientIds.IsValidClientId(graphClientId))
        {
            throw new AuditException("The --client-id value must be a GUID (the application/client id of a public client registration).");
        }

        GraphClientId = graphClientId?.Trim() ?? ClientIds.GraphDefault;
        _customGraph = GraphClientId != ClientIds.GraphDefault;
        Tenant = tenant;
        _deviceCode = deviceCode;
        _say = say;
        _graph = Build(GraphClientId, tenant);
        _arm = Build(ClientIds.AzureCli, tenant);
    }

    public string GraphClientId { get; }

    public string Tenant { get; }

    /// <summary>The interactive Graph sign-in that starts a scan; consent for the read scopes happens here.</summary>
    public async Task<Identity> SignInAsync(CancellationToken ct)
    {
        var result = await InteractiveAsync(Resource.Graph, ClientIds.ScopesFor(ClientIds.GraphReadScopes, _customGraph).Scopes, ct).ConfigureAwait(false);
        _graphAccount = result.Account;
        return IdentityFrom(result);
    }

    Task<Identity> ITokenProvider.SignInAsync(SignInMethod method, CancellationToken ct) => SignInAsync(ct);

    public Task SignOutAsync(Identity identity, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Identity>>([]);

    public Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct = default) =>
        AcquireAsync(scopes, ct);

    public Task<string> AcquireInteractivelyAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct = default) =>
        AcquireAsync(scopes, ct);

    private async Task<string> AcquireAsync(IReadOnlyList<string> requested, CancellationToken ct)
    {
        var (resource, scopes) = ClientIds.ScopesFor(requested, _customGraph);
        var app = resource == Resource.Arm ? _arm : _graph;
        var account = resource == Resource.Arm ? _armAccount : _graphAccount;
        if (account is not null)
        {
            try
            {
                var silent = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Fall through to an interactive acquisition.
            }
            catch (MsalException e)
            {
                throw MsalErrors.Map(e);
            }
        }

        var result = await InteractiveAsync(resource, scopes, ct).ConfigureAwait(false);
        if (resource == Resource.Arm)
        {
            _armAccount = result.Account;
        }
        else
        {
            _graphAccount = result.Account;
        }

        return result.AccessToken;
    }

    private async Task<AuthenticationResult> InteractiveAsync(Resource resource, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var app = resource == Resource.Arm ? _arm : _graph;
        var hint = _graphAccount?.Username;
        await _interactive.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var what = resource == Resource.Arm ? "Azure Resource Manager" : "Microsoft Graph";
            if (_deviceCode)
            {
                var device = app.AcquireTokenWithDeviceCode(scopes, callback =>
                {
                    _say($"{what}: {callback.Message}");
                    return Task.CompletedTask;
                });
                return await device.ExecuteAsync(ct).ConfigureAwait(false);
            }

            _say($"Opening the browser to sign in to {what}{(hint is null ? string.Empty : $" as {hint}")}…");
            var builder = app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .WithPrompt(hint is null ? Prompt.SelectAccount : Prompt.NoPrompt);
            if (hint is not null)
            {
                builder = builder.WithLoginHint(hint);
            }

            return await builder.ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw MsalErrors.Map(e);
        }
        finally
        {
            _interactive.Release();
        }
    }

    private Identity IdentityFrom(AuthenticationResult result)
    {
        var claims = result.ClaimsPrincipal;
        var name = claims?.FindFirst("name")?.Value;
        var tenant = result.TenantId ?? claims?.FindFirst("tid")?.Value ?? string.Empty;
        return new Identity(
            result.Account.HomeAccountId?.Identifier ?? result.Account.Username,
            result.Account.Username,
            name ?? result.Account.Username,
            tenant,
            SignInMethod.Custom(GraphClientId));
    }

    private static IPublicClientApplication Build(string clientId, string tenant)
    {
        try
        {
            return PublicClientApplicationBuilder.Create(clientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, tenant)
                .WithRedirectUri("http://localhost")
                .Build();
        }
        catch (MsalException e)
        {
            throw MsalErrors.Map(e);
        }
    }
}
