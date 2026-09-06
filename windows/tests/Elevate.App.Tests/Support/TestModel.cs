using Elevate.App.Auth;
using Elevate.App.Notifications;
using Elevate.App.Services;
using Elevate.App.ViewModels;
using Elevate.Core.Auth;
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

/// <summary>Records every notification the model posts.</summary>
public sealed class RecordingNotifier : IExpiryNotifier
{
    public List<(string Title, string Body)> Posted { get; } = [];

    public Task RescheduleAsync(
        IReadOnlyList<ActiveAssignment> assignments,
        IReadOnlyDictionary<RoleKey, string> names,
        IReadOnlyDictionary<TenantKey, string> tenantNames) => Task.CompletedTask;

    public Task NotifyAsync(string title, string body)
    {
        Posted.Add((title, body));
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
        RecordingNotifier? notifier = null)
    {
        Directory = Path.Combine(Path.GetTempPath(), "elevate-tests-" + Guid.NewGuid().ToString("N"));
        Store = new AppStateStore(Directory);
        if (state is not null)
        {
            Store.Save(state);
        }

        Settings = new AppSettings(Directory);
        if (clientId is not null)
        {
            Settings.ClientId = clientId;
        }

        Http = http ?? new StubHttpClient();
        Tokens = tokens ?? new FakeTokenProvider();
        // Every method routes to the same fake: the composite's routing has tests of its own.
        FirstParty = new FakeFirstPartyProviders(Tokens);
        Notifier = notifier ?? new RecordingNotifier();
        HotKeys = new NoopHotKeyCenter();
        Model = new AppModel(Tokens, Http, Store, Notifier, new FixedNetworkMonitor(online), Settings, FirstParty,
            ownApp, ownAppFactory, HotKeys);
    }

    public RecordingNotifier Notifier { get; }

    public NoopHotKeyCenter HotKeys { get; }

    public string Directory { get; }

    public AppStateStore Store { get; }

    public AppSettings Settings { get; }

    public StubHttpClient Http { get; }

    public ITokenProvider Tokens { get; }

    public FakeFirstPartyProviders FirstParty { get; }

    public AppModel Model { get; }

    public static async Task<TestModel> BootstrappedAsync(
        AppState? state = null,
        StubHttpClient? http = null,
        bool online = false,
        ITokenProvider? tokens = null,
        FakeOwnAppProvider? ownApp = null,
        Func<string, IOwnAppTokenProvider>? ownAppFactory = null,
        string? clientId = null,
        RecordingNotifier? notifier = null)
    {
        var test = new TestModel(state, http, online, tokens, ownApp, ownAppFactory, clientId, notifier);
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
