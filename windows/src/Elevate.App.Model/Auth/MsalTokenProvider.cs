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
    /// <param name="pinned">
    /// True for a provider serving accounts that keep this client id of their own
    /// (<c>ownApp:&lt;client id&gt;</c>) instead of following Settings; the identities it returns
    /// are stamped with that method, so each routes back to its own registration.
    /// </param>
    public MsalTokenProvider(string clientId, TokenCache cache, InteractiveGate gate, Func<IntPtr> parentWindow, bool pinned = false)
        : base(Build(clientId, parentWindow), cache, gate)
    {
        ClientId = clientId.Trim();
        Method = pinned ? SignInMethod.PinnedApp(ClientId) : SignInMethod.OwnApp;
    }

    public string ClientId { get; }

    protected override SignInMethod Method { get; }

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

/// <summary>
/// One <see cref="MsalTokenProvider"/> per pinned client id, created on first use and kept for the
/// life of the app. The Settings registration keeps its own provider in <c>AppModel</c>; these
/// stamp <c>ownApp:&lt;client id&gt;</c>, so each account routes back to its own registration. All
/// share the token cache file (MSAL keys its entries by client id), the parent window and the
/// interactive gate. Port of the macOS <c>MSALProviderRegistry</c>.
/// </summary>
public sealed class PinnedProviderRegistry : IPinnedProviders
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MsalTokenProvider> _providers = new(StringComparer.Ordinal);
    private readonly TokenCache _cache;
    private readonly InteractiveGate _interactive;
    private readonly Func<IntPtr> _parentWindow;

    public PinnedProviderRegistry(TokenCache cache, InteractiveGate interactive, Func<IntPtr> parentWindow)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(interactive);
        ArgumentNullException.ThrowIfNull(parentWindow);
        _cache = cache;
        _interactive = interactive;
        _parentWindow = parentWindow;
    }

    public ITokenProvider? Provider(string clientId)
    {
        if (!AppSettings.IsValidClientId(clientId))
        {
            return null;
        }

        // Normalised the same way SignInMethod.PinnedApp normalises it, so one provider serves
        // every spelling of the id and the method it stamps matches the one it is asked for.
        var id = SignInMethod.NormalizeClientId(clientId);
        lock (_gate)
        {
            if (_providers.TryGetValue(id, out var existing))
            {
                return existing;
            }
        }

        // Build MSAL's client outside the lock; if another caller won the race meanwhile, keep
        // theirs so every account of this id shares one provider and one cache slot.
        var created = new MsalTokenProvider(id, _cache, _interactive, _parentWindow, pinned: true);
        lock (_gate)
        {
            if (_providers.TryGetValue(id, out var existing))
            {
                return existing;
            }

            _providers[id] = created;
            return created;
        }
    }

    public IReadOnlyCollection<ITokenProvider> Known
    {
        get
        {
            lock (_gate)
            {
                return [.. _providers.Values];
            }
        }
    }
}
