using Elevate.App.Auth;
using Elevate.App.Services;
using Elevate.App.Tests.Support;
using Elevate.App.ViewModels;
using Elevate.Core.Auth;
using Elevate.Core.Managed;
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
    public async Task PinnedAppNeedsAPinnedRegistryAndAGuid()
    {
        using var noRegistry = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider(), clientId: ClientId);
        noRegistry.Model.CanPin.Should().BeFalse();
        noRegistry.Model.IsAvailable(SignInMethod.PinnedApp(OtherClientId)).Should().BeFalse();

        using var pinning = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider(), clientId: ClientId, pinning: true);
        pinning.Model.CanPin.Should().BeTrue();
        pinning.Model.IsAvailable(SignInMethod.PinnedApp(OtherClientId)).Should().BeTrue();
        pinning.Model.IsAvailable(SignInMethod.PinnedApp("not-a-guid")).Should().BeFalse();
        // A pinned account does not need the Settings registration to be configured.
        using var unconfigured = await TestModel.BootstrappedAsync(pinning: true);
        unconfigured.Model.IsAvailable(SignInMethod.OwnApp).Should().BeFalse();
        unconfigured.Model.IsAvailable(SignInMethod.PinnedApp(OtherClientId)).Should().BeTrue();
    }

    [Fact]
    public async Task ManagedClientIdTurnsPinningOff()
    {
        var managed = ManagedConfiguration.Load(new DictionaryManagedSource(
            new Dictionary<string, object?> { [ManagedKey.ClientId.Name()] = ClientId }, "test policy"));
        using var test = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider(), managed: managed, pinning: true);

        test.Model.CanPin.Should().BeFalse();
        test.Model.IsAvailable(SignInMethod.PinnedApp(OtherClientId)).Should().BeFalse();
        test.Model.SettingsRegistrationLabel.Should().Be("managed by your organization");
    }

    [Fact]
    public async Task APinnedIdentityIsReconciledLikeAnyOtherWhenTheRegistryExists()
    {
        var tokens = new FakeTokenProvider();
        var state = new AppState
        {
            Identities = [Sample.Identity("pinned", SignInMethod.PinnedApp(OtherClientId))],
            Tenants = [Sample.Tenant("pinned")],
        };
        using var test = await TestModel.BootstrappedAsync(state, tokens: tokens, ownApp: new FakeOwnAppProvider(), clientId: ClientId, pinning: true);

        // The pinned provider knows no account, so the identity is flagged rather than refused.
        test.Model.NeedsSignIn("pinned").Should().BeTrue();
        test.Model.Identities.Should().ContainSingle();
        test.Model.Notice.Should().Contain("sign in again").And.NotContain("does not support");
        test.Pinned!.Asked.Should().Contain(OtherClientId);
    }

    [Fact]
    public async Task APinnedIdentityIsLeftAloneWithoutARegistry()
    {
        var state = new AppState
        {
            Identities = [Sample.Identity("pinned", SignInMethod.PinnedApp(OtherClientId))],
            Tenants = [Sample.Tenant("pinned")],
        };
        using var test = await TestModel.BootstrappedAsync(state, tokens: new FakeTokenProvider(), ownApp: new FakeOwnAppProvider(), clientId: ClientId);

        test.Model.NeedsSignIn("pinned").Should().BeFalse();
        test.Model.Identities.Should().ContainSingle();
    }

    [Fact]
    public async Task AddingAPinnedAccountRemembersItsClientId()
    {
        var tokens = new FakeTokenProvider();
        var http = new StubHttpClient();
        http.On("GET", "/organization", """{"value":[{"id":"home","displayName":"Home Org"}]}""");
        using var test = await TestModel.BootstrappedAsync(http: http, tokens: tokens, ownApp: new FakeOwnAppProvider(), clientId: ClientId, pinning: true);

        var added = await test.Model.AddAccountAsync(SignInMethod.PinnedApp(OtherClientId));

        added.Should().BeTrue();
        test.Model.Identities.Should().ContainSingle(i => i.SignInMethod == SignInMethod.PinnedApp(OtherClientId));
        test.Model.RememberedPinnedClientId.Should().Be(OtherClientId);
        test.Settings.PinnedClientId.Should().Be(OtherClientId);
    }

    [Fact]
    public void TheFirstPartyRegistryNeverServesEntraAppRegistrationForms()
    {
        var registry = new FirstPartyProviderRegistry(
            new TokenCache(Path.Combine(Path.GetTempPath(), $"elevate-tests-{Guid.NewGuid():N}")), new InteractiveGate(), () => IntPtr.Zero);

        registry.Provider(SignInMethod.PinnedApp(OtherClientId)).Should().BeNull();
        registry.Provider(SignInMethod.OwnApp).Should().BeNull();
        registry.Known.Should().BeEmpty();
    }

    [Fact]
    public async Task RetryingSignInForAPinnedIdentityRefusesUnderAManagedClientId()
    {
        var tokens = new FakeTokenProvider();
        var state = new AppState
        {
            Identities = [Sample.Identity("pinned", SignInMethod.PinnedApp(OtherClientId))],
            Tenants = [Sample.Tenant("pinned")],
        };
        var managed = ManagedConfiguration.Load(new DictionaryManagedSource(
            new Dictionary<string, object?> { [ManagedKey.ClientId.Name()] = ClientId }, "test policy"));
        using var test = await TestModel.BootstrappedAsync(state, tokens: tokens, ownApp: new FakeOwnAppProvider(), managed: managed, pinning: true);
        var identity = test.Model.Identity("pinned")!;

        var ok = await test.Model.RetrySignInAsync(identity);

        ok.Should().BeFalse();
        test.Model.Notice.Should().Be(AppModel.ManagedPinnedNotice);
        tokens.StoredIdentities.Should().BeEmpty();
    }

    [Fact]
    public async Task RetryingSignInForAPinnedIdentitySignsItInAgain()
    {
        var tokens = new FakeTokenProvider();
        var state = new AppState
        {
            Identities = [Sample.Identity("pinned", SignInMethod.PinnedApp(OtherClientId))],
            Tenants = [Sample.Tenant("pinned")],
        };
        using var test = await TestModel.BootstrappedAsync(state, tokens: tokens, ownApp: new FakeOwnAppProvider(), clientId: ClientId, pinning: true);
        var identity = test.Model.Identity("pinned")!;
        test.Model.NeedsSignIn("pinned").Should().BeTrue();
        tokens.NextSignIn = Sample.Identity("pinned", SignInMethod.PinnedApp(OtherClientId));

        var ok = await test.Model.RetrySignInAsync(identity);

        ok.Should().BeTrue();
        test.Model.NeedsSignIn("pinned").Should().BeFalse();
        test.Model.Notice.Should().BeNull();
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
        http.On("GET", "roleEligibilitySchedules/filterByCurrentUser", """{"value":[]}""");
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
    public async Task ApplyClientIdKeepsTheAccountsThatFollowItAndAsksThemToSignInAgain()
    {
        var previous = new FakeOwnAppProvider();
        var replacement = new FakeOwnAppProvider();
        var pinnedMethod = SignInMethod.PinnedApp("aaaaaaaa-2222-3333-4444-555555555555");
        var state = new AppState
        {
            Identities =
            [
                Sample.Identity("own", SignInMethod.OwnApp),
                Sample.Identity("cli", SignInMethod.AzureCLI),
                Sample.Identity("pinned", pinnedMethod),
            ],
            Tenants = [Sample.Tenant("own"), Sample.Tenant("cli"), Sample.Tenant("pinned")],
        };
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity("own", SignInMethod.OwnApp));
        tokens.AddIdentity(Sample.Identity("cli", SignInMethod.AzureCLI));
        tokens.AddIdentity(Sample.Identity("pinned", pinnedMethod));
        using var test = await TestModel.BootstrappedAsync(state, tokens: tokens, ownApp: previous,
            ownAppFactory: _ => replacement, clientId: ClientId, pinning: true);
        var model = test.Model;
        model.Identities.Should().HaveCount(3);
        model.OwnAppIdentityCount.Should().Be(1, "only the account following Settings is affected");
        model.Roles[new TenantKey("own", Sample.TenantId)] = [Sample.Role(Sample.Key(new EntraDirectoryScope("r", "/"), "own"), "Reader")];

        model.ApplyClientId(OtherClientId);

        model.Settings.ClientId.Should().Be(OtherClientId);
        // The accounts, their tenants and their configured roles stay; only the read state goes.
        model.Identities.Select(i => i.Id).Should().Equal("own", "cli", "pinned");
        model.TenantsFor("own").Should().ContainSingle();
        model.NeedsSignIn("own").Should().BeTrue();
        model.NeedsSignIn("cli").Should().BeFalse();
        model.NeedsSignIn("pinned").Should().BeFalse();
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
    public async Task BootstrapKeepsAndFlagsIdentitiesTheCachesNoLongerKnow()
    {
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity("kept", SignInMethod.AzureCLI));
        using var test = await TestModel.BootstrappedAsync(GoneAndKept(), tokens: tokens);

        // The account, its tenant and its roles stay: a lost token is not the user removing the account.
        test.Model.Identities.Select(i => i.Id).Should().Equal("gone", "kept");
        test.Model.TenantsFor("gone").Should().ContainSingle();
        test.Model.NeedsSignIn("gone").Should().BeTrue();
        test.Model.NeedsSignIn("kept").Should().BeFalse();
        test.Model.Notice.Should().Contain("gone@example.com").And.Contain("sign in again").And.NotContain("signed out");
    }

    [Fact]
    public async Task AFlaggedIdentityIsNotRefreshed()
    {
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity("kept", SignInMethod.AzureCLI));
        using var test = await TestModel.BootstrappedAsync(GoneAndKept(), tokens: tokens);

        // Without a saved sign-in every read would only prompt or fail; the tenant waits for "Sign in again".
        await test.Model.RefreshAsync(new TenantKey("gone", GoneTenantId));

        tokens.SilentCalls.Should().NotContain(GoneTenantId);
        test.Model.TenantErrors.Should().NotContainKey(new TenantKey("gone", GoneTenantId));
    }

    [Fact]
    public async Task SigningInAgainClearsTheFlag()
    {
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity("kept", SignInMethod.AzureCLI));
        using var test = await TestModel.BootstrappedAsync(GoneAndKept(), tokens: tokens);
        var gone = test.Model.Identity("gone")!;
        tokens.NextSignIn = Sample.Identity("gone", SignInMethod.AzureCLI);

        var ok = await test.Model.RetrySignInAsync(gone);

        ok.Should().BeTrue();
        test.Model.NeedsSignIn("gone").Should().BeFalse();
        test.Model.Notice.Should().BeNull();
        test.Model.Identities.Select(i => i.Id).Should().Equal("gone", "kept");
    }

    [Fact]
    public async Task SigningInAgainAsADifferentAccountKeepsTheFlagAndDiscardsThatSignIn()
    {
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity("kept", SignInMethod.AzureCLI));
        using var test = await TestModel.BootstrappedAsync(GoneAndKept(), tokens: tokens);
        var gone = test.Model.Identity("gone")!;
        tokens.NextSignIn = Sample.Identity("other", SignInMethod.AzureCLI);

        var ok = await test.Model.RetrySignInAsync(gone);

        ok.Should().BeFalse();
        test.Model.NeedsSignIn("gone").Should().BeTrue();
        tokens.SignOutCalls.Should().Equal("other");
        test.Model.Identities.Select(i => i.Id).Should().Equal("gone", "kept");
        test.Model.Notice.Should().Contain("other@example.com").And.Contain("gone@example.com");
    }

    [Fact]
    public async Task SigningOutAFlaggedIdentityRemovesItAndTheFlag()
    {
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity("kept", SignInMethod.AzureCLI));
        using var test = await TestModel.BootstrappedAsync(GoneAndKept(), tokens: tokens);

        test.Model.SignOut(test.Model.Identity("gone")!);

        test.Model.Identities.Select(i => i.Id).Should().Equal("kept");
        test.Model.NeedsSignIn("gone").Should().BeFalse();
    }

    private const string GoneTenantId = "tenant-gone";

    /// <summary>Two Azure CLI accounts; only "kept" is added to the fake token cache by the tests.</summary>
    private static AppState GoneAndKept() => new()
    {
        Identities = [Sample.Identity("gone", SignInMethod.AzureCLI), Sample.Identity("kept", SignInMethod.AzureCLI)],
        Tenants = [Sample.Tenant("gone", GoneTenantId), Sample.Tenant("kept")],
    };

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
        test.Model.NeedsSignIn("kept").Should().BeFalse();
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

    // MARK: Change app registration

    /// <summary>An account, its tenant and a role read under the old registration.</summary>
    private static AppState WithOneAccount(SignInMethod method) => new()
    {
        Identities = [Sample.Identity(method: method)],
        Tenants = [Sample.Tenant() with { EntraActivation = EntraActivationSupport.Unsupported("no scope"), LastDiscoveryError = "blocked" }],
    };

    [Fact]
    public async Task ChangingRegistrationKeepsTheAccountAndItsTenantsAndClearsTheLimitedMethodsFlags()
    {
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity(method: SignInMethod.AzureCLI));
        using var test = await TestModel.BootstrappedAsync(WithOneAccount(SignInMethod.AzureCLI), tokens: tokens,
            ownApp: new FakeOwnAppProvider(), clientId: ClientId, pinning: true);
        var model = test.Model;
        var identity = model.Identity(Sample.IdentityId)!;
        model.CanChangeRegistration(identity).Should().BeTrue();
        tokens.NextSignIn = Sample.Identity(method: SignInMethod.OwnApp);

        var changed = await model.ChangeSignInRegistrationAsync(identity, SignInMethod.OwnApp);

        changed.Should().BeTrue();
        model.Identity(Sample.IdentityId)!.SignInMethod.Should().Be(SignInMethod.OwnApp);
        model.TenantsFor(Sample.IdentityId).Should().ContainSingle();
        var tenant = model.Tenant(Sample.TenantKey)!;
        tenant.EntraActivation.Should().BeNull();
        tenant.LastDiscoveryError.Should().BeNull();
        model.Notice.Should().BeNull();
    }

    [Fact]
    public async Task ChangingRegistrationToAPinnedIdRemembersItAndKeepsTheOldSessionOut()
    {
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity(method: SignInMethod.OwnApp));
        using var test = await TestModel.BootstrappedAsync(WithOneAccount(SignInMethod.OwnApp), tokens: tokens,
            ownApp: new FakeOwnAppProvider(), clientId: ClientId, pinning: true);
        var model = test.Model;
        var identity = model.Identity(Sample.IdentityId)!;
        var target = SignInMethod.PinnedApp(OtherClientId);
        tokens.NextSignIn = Sample.Identity(method: target);

        var changed = await model.ChangeSignInRegistrationAsync(identity, target);

        changed.Should().BeTrue();
        model.Identity(Sample.IdentityId)!.SignInMethod.Should().Be(target);
        model.RememberedPinnedClientId.Should().Be(OtherClientId);
        // The old cache slot is a different client id, so the session there is discarded.
        tokens.SignOutCalls.Should().Equal(Sample.IdentityId);
    }

    [Fact]
    public async Task ChangingRegistrationToTheSameClientIdKeepsTheSharedCacheSlot()
    {
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity(method: SignInMethod.OwnApp));
        using var test = await TestModel.BootstrappedAsync(WithOneAccount(SignInMethod.OwnApp), tokens: tokens,
            ownApp: new FakeOwnAppProvider(), clientId: ClientId, pinning: true);
        var model = test.Model;
        var identity = model.Identity(Sample.IdentityId)!;
        var target = SignInMethod.PinnedApp(ClientId);
        tokens.NextSignIn = Sample.Identity(method: target);

        var changed = await model.ChangeSignInRegistrationAsync(identity, target);

        changed.Should().BeTrue();
        tokens.SignOutCalls.Should().BeEmpty("both methods are the same MSAL account under one client id");
    }

    [Fact]
    public async Task ADifferentAccountSigningInChangesNothing()
    {
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity(method: SignInMethod.AzureCLI));
        using var test = await TestModel.BootstrappedAsync(WithOneAccount(SignInMethod.AzureCLI), tokens: tokens,
            ownApp: new FakeOwnAppProvider(), clientId: ClientId, pinning: true);
        var model = test.Model;
        var identity = model.Identity(Sample.IdentityId)!;
        tokens.NextSignIn = Sample.Identity("someone-else", SignInMethod.OwnApp);

        var changed = await model.ChangeSignInRegistrationAsync(identity, SignInMethod.OwnApp);

        changed.Should().BeFalse();
        model.Identity(Sample.IdentityId)!.SignInMethod.Should().Be(SignInMethod.AzureCLI);
        model.Notice.Should().Contain("was expected").And.Contain("Nothing was changed");
        tokens.SignOutCalls.Should().Equal("someone-else");
    }

    [Fact]
    public async Task OnlyAnEntraAppRegistrationCanBeChosenAndAManagedClientIdBlocksPinning()
    {
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity(method: SignInMethod.AzureCLI));
        var managed = ManagedConfiguration.Load(new DictionaryManagedSource(
            new Dictionary<string, object?> { [ManagedKey.ClientId.Name()] = ClientId }, "test policy"));
        using var test = await TestModel.BootstrappedAsync(WithOneAccount(SignInMethod.AzureCLI), tokens: tokens,
            ownApp: new FakeOwnAppProvider(), managed: managed, pinning: true);
        var model = test.Model;
        var identity = model.Identity(Sample.IdentityId)!;

        (await model.ChangeSignInRegistrationAsync(identity, SignInMethod.AzurePowerShell)).Should().BeFalse();
        model.Notice.Should().Contain("Only an Entra app registration");

        (await model.ChangeSignInRegistrationAsync(identity, SignInMethod.PinnedApp(OtherClientId))).Should().BeFalse();
        model.Notice.Should().Contain("managed by your organization");
        tokens.StoredIdentities.Select(i => i.Id).Should().Equal(Sample.IdentityId);
    }

    [Fact]
    public async Task ChangingRegistrationIsRefusedWhileTheAccountIsBusy()
    {
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity(method: SignInMethod.AzureCLI));
        using var test = await TestModel.BootstrappedAsync(WithOneAccount(SignInMethod.AzureCLI), tokens: tokens,
            ownApp: new FakeOwnAppProvider(), clientId: ClientId, pinning: true);
        var model = test.Model;
        var identity = model.Identity(Sample.IdentityId)!;
        model.Busy.Add(Sample.TenantKey);

        var changed = await model.ChangeSignInRegistrationAsync(identity, SignInMethod.OwnApp);

        changed.Should().BeFalse();
        model.Notice.Should().Contain("Wait for this account");
        model.Identity(Sample.IdentityId)!.SignInMethod.Should().Be(SignInMethod.AzureCLI);
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
