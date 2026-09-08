using Elevate.App.Services;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace Elevate.App.Auth;

/// <summary>
/// The own-app registration through MSAL with the Windows broker (WAM): the account picker and
/// sign-in are the system's, and the system browser at <c>http://localhost</c> is the fallback
/// when the broker is unavailable. Requests the exact PIM scopes, which the tenant must have
/// admin-consented.
/// </summary>
public sealed class MsalTokenProvider : MsalProviderBase, IOwnAppTokenProvider
{
    public MsalTokenProvider(string clientId, TokenCache cache, InteractiveGate gate, Func<IntPtr> parentWindow)
        : base(Build(clientId, parentWindow), cache, gate)
    {
        ClientId = clientId.Trim();
    }

    public string ClientId { get; }

    protected override SignInMethod Method => SignInMethod.OwnApp;

    /// <summary>
    /// The sign-in asks for the entitlement scope alongside User.Read, as the macOS provider does:
    /// it is user-consentable, so the prompt never blocks adding an account, and the PIM scopes
    /// (admin consent) are still acquired on the first read. Users in already-consented tenants
    /// see one incremental consent prompt on their next interactive sign-in.
    /// </summary>
    protected override IReadOnlyList<string> SignInScopes { get; } = [Scopes.GraphUserRead, .. Scopes.EntitlementAll];

    protected override IReadOnlyList<string> Requested(IReadOnlyList<string> scopes) => scopes;

    protected override AcquireTokenInteractiveParameterBuilder Configure(AcquireTokenInteractiveParameterBuilder builder) => builder;

    /// <summary>Drops the given accounts from this client's cache (and the broker) without a browser round trip.</summary>
    public async Task RemoveCachedAccountsAsync(IEnumerable<Identity> identities, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identities);
        await EnsureCacheAsync().ConfigureAwait(false);
        foreach (var identity in identities)
        {
            if (await FindAccountAsync(identity).ConfigureAwait(false) is { } account)
            {
                await Run(() => App.RemoveAsync(account)).ConfigureAwait(false);
            }
        }
    }

    private static IPublicClientApplication Build(string clientId, Func<IntPtr> parentWindow)
    {
        ArgumentNullException.ThrowIfNull(parentWindow);
        if (!AppSettings.IsValidClientId(clientId))
        {
            throw new PimException(PimErrorKind.Unexpected, "Enter the application (client) ID as a GUID");
        }

        try
        {
            return PublicClientApplicationBuilder.Create(clientId.Trim())
                .WithAuthority(AzureCloudInstance.AzurePublic, "organizations")
                // The broker uses ms-appx-web://microsoft.aad.brokerplugin/{clientId}; localhost is the browser fallback.
                .WithRedirectUri(AppSettings.LoopbackRedirectUri)
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Elevate" })
                .WithParentActivityOrWindow(parentWindow)
                .Build();
        }
        catch (MsalException e)
        {
            throw Map(e);
        }
    }
}
