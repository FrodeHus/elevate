using Elevate.App.Auth;
using Elevate.App.Services;
using Elevate.App.Tests.Support;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.App.Tests;

public class AppModelAccountTests
{
    private const string ClientId = "11111111-2222-3333-4444-555555555555";
    private const string OtherClientId = "99999999-2222-3333-4444-555555555555";

    [Fact]
    public async Task OwnAppIsAvailableOnlyWithAClientIdAndAProvider()
    {
        using var unconfigured = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider());
        unconfigured.Model.IsConfigured.Should().BeFalse();
        unconfigured.Model.IsAvailable(SignInMethod.OwnApp).Should().BeFalse();
        unconfigured.Model.IsAvailable(SignInMethod.AzureCLI).Should().BeTrue();

        using var noProvider = await TestModel.BootstrappedAsync(clientId: ClientId);
        noProvider.Model.IsConfigured.Should().BeFalse();

        using var configured = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider(), clientId: ClientId);
        configured.Model.IsConfigured.Should().BeTrue();
        configured.Model.IsAvailable(SignInMethod.OwnApp).Should().BeTrue();
        configured.Model.IsAvailable(SignInMethod.Custom("not-a-guid")).Should().BeFalse();
        configured.Model.IsAvailable(SignInMethod.Custom(OtherClientId)).Should().BeTrue();
    }

    [Fact]
    public async Task AddingAnAccountAlreadyPresentUnderAnotherMethodIsRefusedAndTheSignInDiscarded()
    {
        var tokens = new FakeTokenProvider();
        using var test = await TestModel.BootstrappedAsync(tokens: tokens, ownApp: new FakeOwnAppProvider(), clientId: ClientId);
        var model = test.Model;
        // FakeTokenProvider.SignInAsync always returns the identity id "new".
        model.State.Identities.Add(Sample.Identity("new", SignInMethod.AzureCLI));

        var added = await model.AddAccountAsync(SignInMethod.OwnApp);

        added.Should().BeFalse();
        model.Notice.Should().Contain("already added");
        tokens.SignOutCalls.Should().Equal("new");
        model.Identities.Should().ContainSingle(i => i.Id == "new" && i.SignInMethod == SignInMethod.AzureCLI);
    }

    [Fact]
    public async Task DuplicateOverTheSameClientIdKeepsTheSharedCache()
    {
        var tokens = new FakeTokenProvider();
        using var test = await TestModel.BootstrappedAsync(tokens: tokens, ownApp: new FakeOwnAppProvider(), clientId: ClientId);
        var model = test.Model;
        model.State.Identities.Add(Sample.Identity("new", SignInMethod.Custom(SignInMethod.AzureCLIClientId)));

        var added = await model.AddAccountAsync(SignInMethod.AzureCLI);

        added.Should().BeFalse();
        tokens.SignOutCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task AddingAnAccountRecordsItsHomeTenantAndReadsIt()
    {
        var http = new StubHttpClient();
        http.On("GET", "/organization", """{"value":[{"id":"home","displayName":"Home Org"}]}""");
        http.On("GET", "roleEligibilityScheduleInstances", """{"value":[]}""");
        http.On("GET", "roleAssignmentScheduleInstances", """{"value":[]}""");
        http.On("GET", "roleAssignmentScheduleRequests", """{"value":[]}""");
        http.On("GET", "management.azure.com", """{"value":[]}""");
        http.On("GET", "privilegedAccess/group", """{"value":[]}""");
        using var test = await TestModel.BootstrappedAsync(http: http);
        var model = test.Model;

        var added = await model.AddAccountAsync(SignInMethod.AzureCLI);

        added.Should().BeTrue();
        model.Identities.Should().ContainSingle(i => i.Id == "new");
        var tenant = model.Tenant(new TenantKey("new", "home"));
        tenant.Should().NotBeNull();
        tenant!.DisplayName.Should().Be("Home Org");
        tenant.Source.Should().Be(TenantSource.Home);
        model.RolesFor(new TenantKey("new", "home")).Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyClientIdSignsOutOwnAppAccountsAndKeepsFirstPartyOnes()
    {
        var previous = new FakeOwnAppProvider();
        var replacement = new FakeOwnAppProvider();
        var state = new AppState
        {
            Identities = [Sample.Identity("own", SignInMethod.OwnApp), Sample.Identity("cli", SignInMethod.AzureCLI)],
            Tenants = [Sample.Tenant("own"), Sample.Tenant("cli")],
        };
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity("own", SignInMethod.OwnApp));
        tokens.AddIdentity(Sample.Identity("cli", SignInMethod.AzureCLI));
        using var test = await TestModel.BootstrappedAsync(state, tokens: tokens, ownApp: previous,
            ownAppFactory: _ => replacement, clientId: ClientId);
        var model = test.Model;
        model.Identities.Should().HaveCount(2);
        model.Roles[new TenantKey("own", Sample.TenantId)] = [Sample.Role(Sample.Key(new EntraDirectoryScope("r", "/"), "own"), "Reader")];

        model.ApplyClientId(OtherClientId);

        model.Settings.ClientId.Should().Be(OtherClientId);
        model.Identities.Select(i => i.Id).Should().Equal("cli");
        model.Roles.Keys.Should().NotContain(k => k.IdentityId == "own");
        model.IsConfigured.Should().BeTrue();
        model.Tokens.Should().BeOfType<CompositeTokenProvider>();
        // The old cache is dropped in the background; wait for it.
        await Task.Delay(100);
        previous.Removed.Should().Equal("own");
        new AppSettings(test.Directory).ClientId.Should().Be(OtherClientId);
    }

    [Fact]
    public async Task ApplyClientIdRejectsANonGuidWithoutTouchingAnything()
    {
        using var test = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider(), ownAppFactory: _ => new FakeOwnAppProvider(), clientId: ClientId);
        var act = () => test.Model.ApplyClientId("nope");
        act.Should().Throw<PimException>().Which.UserMessage.Should().Contain("GUID");
        test.Model.Settings.ClientId.Should().Be(ClientId);
    }

    [Fact]
    public async Task BootstrapDropsIdentitiesTheCachesNoLongerKnow()
    {
        var state = new AppState
        {
            Identities = [Sample.Identity("gone", SignInMethod.AzureCLI), Sample.Identity("kept", SignInMethod.AzureCLI)],
            Tenants = [Sample.Tenant("gone"), Sample.Tenant("kept")],
        };
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity("kept", SignInMethod.AzureCLI));
        using var test = await TestModel.BootstrappedAsync(state, tokens: tokens);

        test.Model.Identities.Select(i => i.Id).Should().Equal("kept");
        test.Model.Notice.Should().Contain("gone@example.com").And.Contain("signed out");
    }

    [Fact]
    public async Task BootstrapKeepsOwnAppIdentitiesWhenThereIsNoOwnAppProvider()
    {
        var state = new AppState { Identities = [Sample.Identity("own", SignInMethod.OwnApp)], Tenants = [Sample.Tenant("own")] };
        using var test = await TestModel.BootstrappedAsync(state, tokens: new FakeTokenProvider());

        test.Model.Identities.Select(i => i.Id).Should().Equal("own");
    }

    [Fact]
    public async Task BootstrapFailsOpenWhenTheCachesCannotBeRead()
    {
        var state = new AppState { Identities = [Sample.Identity("kept", SignInMethod.AzureCLI)], Tenants = [Sample.Tenant("kept")] };
        using var test = new TestModel(state, tokens: new ThrowingIdentitiesProvider());
        await test.Model.BootstrapAsync();

        test.Model.Identities.Select(i => i.Id).Should().Equal("kept");
        test.Model.Notice.Should().Contain("accounts were kept");
    }

    [Fact]
    public async Task BootstrapQuarantinesACorruptStateFile()
    {
        using var test = new TestModel();
        await File.WriteAllTextAsync(test.Store.FilePath, "{ not json");
        await test.Model.BootstrapAsync();

        test.Model.Identities.Should().BeEmpty();
        test.Model.Notice.Should().Contain("state.json.bak");
        File.Exists(Path.Combine(test.Directory, "state.json.bak")).Should().BeTrue();
    }

    [Fact]
    public async Task AnActivationRefusedForConsentPutsTheTenantInManualMode()
    {
        var http = new StubHttpClient();
        http.On("GET", "/me", """{"id":"principal-1"}""");
        http.On("POST", "roleAssignmentScheduleRequests", """{"error":{"code":"Authorization_RequestDenied"}}""", 403);
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        using var test = await TestModel.BootstrappedAsync(state, http: http);
        var model = test.Model;
        model.Roles[Sample.TenantKey] = [Sample.Role(Sample.EntraKey, "Global Reader")];

        var outcomes = await model.ActivateAsync([new ActivationRequest(Sample.EntraKey, TimeSpan.FromHours(1), "reason")]);

        outcomes.Should().ContainSingle();
        model.Progress[Sample.EntraKey].Should().BeOfType<Elevate.Core.Coordination.ActivationResult.Failed>()
            .Which.Error.Kind.Should().Be(PimErrorKind.ConsentRequired);
        var tenant = model.Tenant(Sample.TenantKey)!;
        tenant.DiscoveryMode.Should().Be(DiscoveryMode.ManualRoles);
        tenant.LastDiscoveryError.Should().Contain("admin consents");
        model.InFlight.Should().BeEmpty();
    }

    [Fact]
    public async Task AFirstPartyActivationRefusedForTheWriteScopeMarksEntraViewOnly()
    {
        var http = new StubHttpClient();
        http.On("GET", "/me", """{"id":"principal-1"}""");
        http.On("POST", "roleAssignmentScheduleRequests", """{"error":{"code":"Authorization_RequestDenied"}}""", 403);
        var identity = Sample.Identity(method: SignInMethod.Custom(OtherClientId));
        var state = new AppState { Identities = [identity], Tenants = [Sample.Tenant()] };
        // Bootstrap reconciles against the caches: the fake must know the account or it is signed out.
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(identity);
        using var test = await TestModel.BootstrappedAsync(state, http: http, tokens: tokens);
        var model = test.Model;
        model.Roles[Sample.TenantKey] = [Sample.Role(Sample.EntraKey, "Global Reader")];

        await model.ActivateAsync([new ActivationRequest(Sample.EntraKey, TimeSpan.FromHours(1), "reason")]);

        model.Tenant(Sample.TenantKey)!.EntraActivation!.IsSupported.Should().BeFalse();
        model.CanActivate(Sample.EntraKey).Should().BeFalse();
    }

    /// <summary>A provider whose account list cannot be read at all.</summary>
    private sealed class ThrowingIdentitiesProvider : ITokenProvider
    {
        public Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct) => throw new PimException(PimErrorKind.Network, "down");

        public Task SignOutAsync(Identity identity, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct) => throw new PimException(PimErrorKind.Network, "cache unreadable");

        public Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct) => Task.FromResult("t");

        public Task<string> AcquireInteractivelyAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct) => Task.FromResult("t");
    }
}
