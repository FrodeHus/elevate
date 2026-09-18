using Elevate.App.Auth;
using Elevate.App.Notifications;
using Elevate.App.Services;
using Elevate.App.ViewModels;
using Elevate.Core.Auth;
using Elevate.Core.Managed;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Tests.Support;

namespace Elevate.App.Tests.Support;

/// <summary>Routes every non-own-app method to one fake provider.</summary>
public sealed class FakeFirstPartyProviders(ITokenProvider? provider = null) : IFirstPartyProviders
{
    public ITokenProvider? Provider(SignInMethod method) => method.UsesMsal ? null : provider;

    public IReadOnlyCollection<ITokenProvider> Known { get; } = provider is null ? [] : [provider];
}

/// <summary>Routes every pinned client id to one fake provider, recording the ids asked for.</summary>
public sealed class FakePinnedProviders(ITokenProvider? provider = null) : IPinnedProviders
{
    public List<string> Asked { get; } = [];

    public ITokenProvider? Provider(string clientId)
    {
        Asked.Add(clientId);
        return AppSettings.IsValidClientId(clientId) ? provider : null;
    }

    public IReadOnlyCollection<ITokenProvider> Known { get; } = provider is null ? [] : [provider];
}

/// <summary>A <see cref="FakeTokenProvider"/> that can also stand in for the own-app (MSAL) provider.</summary>
public sealed class FakeOwnAppProvider : IOwnAppTokenProvider
{
    public FakeTokenProvider Inner { get; } = new();

    public List<string> Removed { get; } = [];

    public Task RemoveCachedAccountsAsync(IEnumerable<Identity> identities, CancellationToken ct)
    {
        Removed.AddRange(identities.Select(i => i.Id));
        return Task.CompletedTask;
    }

    public Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct) => Inner.SignInAsync(method, ct);

    public Task SignOutAsync(Identity identity, CancellationToken ct) => Inner.SignOutAsync(identity, ct);

    public Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct) => Inner.IdentitiesAsync(ct);

    public Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct) =>
        Inner.AccessTokenAsync(identity, tenantId, scopes, ct);

    public Task<string> AcquireInteractivelyAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct) =>
        Inner.AcquireInteractivelyAsync(identity, tenantId, scopes, claims, ct);
}

/// <summary>
/// Records every notification the model posts. Guarded: a propagation probe reports from a
/// background task, so the list is appended from one thread while a test reads it from another.
/// </summary>
public sealed class RecordingNotifier : IExpiryNotifier
{
    private readonly List<(string Title, string Body)> _posted = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<(string Title, string Body)> Posted
    {
        get { lock (_gate) { return [.. _posted]; } }
    }

    public Task RescheduleAsync(
        IReadOnlyList<ActiveAssignment> assignments,
        IReadOnlyDictionary<RoleKey, string> names,
        IReadOnlyDictionary<TenantKey, string> tenantNames) => Task.CompletedTask;

    /// <summary>The last set of package expiries the model handed over.</summary>
    public IReadOnlyList<PackageExpiry> PackageExpiries { get; private set; } = [];

    public Task SetPackageExpiriesAsync(IReadOnlyList<PackageExpiry> expiries)
    {
        PackageExpiries = expiries;
        return Task.CompletedTask;
    }

    public Task NotifyAsync(string title, string body)
    {
        lock (_gate)
        {
            _posted.Add((title, body));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Builds an <see cref="AppModel"/> wired entirely to fakes: no MSAL, no real network, its own
/// state file and settings file in a temp directory. Offline by default so bootstrap performs no
/// refresh. Dispose it to remove the directory.
/// </summary>
public sealed class TestModel : IDisposable
{
    public TestModel(
        AppState? state = null,
        StubHttpClient? http = null,
        bool online = false,
        ITokenProvider? tokens = null,
        FakeOwnAppProvider? ownApp = null,
        Func<string, IOwnAppTokenProvider>? ownAppFactory = null,
        string? clientId = null,
        RecordingNotifier? notifier = null,
        ManagedConfiguration? managed = null,
        bool pinning = false)
    {
        Directory = Path.Combine(Path.GetTempPath(), "elevate-tests-" + Guid.NewGuid().ToString("N"));
        Store = new AppStateStore(Directory);
        if (state is not null)
        {
            Store.Save(state);
        }

        // Never the machine's real policy: an empty configuration unless a test pushes one.
        Settings = new AppSettings(Directory, managed ?? ManagedConfiguration.None);
        if (clientId is not null)
        {
            Settings.ClientId = clientId;
        }

        Http = http ?? new StubHttpClient();
        Tokens = tokens ?? new FakeTokenProvider();
        // Every method routes to the same fake: the composite's routing has tests of its own.
        FirstParty = new FakeFirstPartyProviders(Tokens);
        // Pinning is off unless a test asks for it, so the default model behaves like a build
        // without a pinned registry.
        Pinned = pinning ? new FakePinnedProviders(Tokens) : null;
        Notifier = notifier ?? new RecordingNotifier();
        HotKeys = new NoopHotKeyCenter();
        // AppModel captures SynchronizationContext.Current and marshals callbacks from the
        // coordinator back onto it, because in the app that is the one UI thread it requires. The
        // context xUnit installs is not a thread at all: its Post hands the callback to the thread
        // pool, so an activation's progress callback could mutate Progress and Active while the
        // authoritative loop in ActivateCoreAsync was writing the same dictionaries — a genuine
        // data race, and an intermittent "non-concurrent collections must have exclusive access".
        // Constructing the model with no ambient context makes Post run inline instead: the
        // coordinator raises progress from inside the call the model is awaiting, so the callback
        // lands before that await returns. That is one of the two orderings the UI thread already
        // produces, and it is the deterministic one.
        var ambient = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            Model = new AppModel(Tokens, Http, Store, Notifier, new FixedNetworkMonitor(online), Settings, FirstParty,
                ownApp, ownAppFactory, HotKeys, Pinned);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(ambient);
        }
    }

    public RecordingNotifier Notifier { get; }

    public NoopHotKeyCenter HotKeys { get; }

    public string Directory { get; }

    public AppStateStore Store { get; }

    public AppSettings Settings { get; }

    public StubHttpClient Http { get; }

    public ITokenProvider Tokens { get; }

    public FakeFirstPartyProviders FirstParty { get; }

    /// <summary>Null unless the test asked for pinning.</summary>
    public FakePinnedProviders? Pinned { get; }

    public AppModel Model { get; }

    public static async Task<TestModel> BootstrappedAsync(
        AppState? state = null,
        StubHttpClient? http = null,
        bool online = false,
        ITokenProvider? tokens = null,
        FakeOwnAppProvider? ownApp = null,
        Func<string, IOwnAppTokenProvider>? ownAppFactory = null,
        string? clientId = null,
        RecordingNotifier? notifier = null,
        ManagedConfiguration? managed = null,
        bool pinning = false)
    {
        var test = new TestModel(state, http, online, tokens, ownApp, ownAppFactory, clientId, notifier, managed, pinning);
        await test.Model.BootstrapAsync();
        return test;
    }

    public void Dispose()
    {
        Model.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A save may still be landing; the temp directory is harmless.
        }
    }
}

public static class Sample
{
    public const string IdentityId = "id-1";
    public const string TenantId = "tenant-1";

    public static TenantKey TenantKey => new(IdentityId, TenantId);

    public static Identity Identity(string id = IdentityId, SignInMethod? method = null) =>
        new(id, $"{id}@example.com", id.ToUpperInvariant(), TenantId, method ?? SignInMethod.OwnApp);

    public static TenantContext Tenant(string identityId = IdentityId, string tenantId = TenantId, string name = "Contoso") =>
        new(identityId, tenantId, name, TenantSource.Home);

    public static RoleKey Key(RoleScope scope, string identityId = IdentityId, string tenantId = TenantId) =>
        new(identityId, tenantId, scope);

    public static RoleKey EntraKey => Key(new EntraDirectoryScope("role-def", "/"));

    public static RoleKey AzureKey => Key(new AzureResourceScope("/subscriptions/s1", "owner-def"));

    public static RoleKey GroupKey => Key(new GroupScope("group-1", GroupAccess.Member));

    public static EligibleRole Role(RoleKey key, string name) =>
        new(key, name, RoleSource.Discovered, RolePolicy.ManualDefault);

    public static ActiveAssignment Assignment(RoleKey key, DateTimeOffset? ends = null) =>
        new(key, "a", DateTimeOffset.UtcNow, ends ?? DateTimeOffset.UtcNow.AddHours(1), AssignmentStatus.Active);
}
