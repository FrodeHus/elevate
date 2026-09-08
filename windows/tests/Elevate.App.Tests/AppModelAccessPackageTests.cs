using System.Text;
using System.Text.Json;
using Elevate.App.Tests.Support;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Tests.Support;
using Elevate.App.ViewModels;
using FluentAssertions;

namespace Elevate.App.Tests;

public class AppModelAccessPackageTests
{
    private const string Entitlement = "User.Read EntitlementMgmt-SubjectAccess.ReadWrite";

    /// <summary>A JWT-shaped token whose <c>scp</c> claim carries the given space-separated scopes.</summary>
    private static string Token(string scp)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["aud"] = "https://graph.microsoft.com", ["tid"] = "t", ["scp"] = scp });
        return B64("""{"alg":"none"}""") + "." + B64(payload) + ".sig";
    }

    /// <summary>Stubs for a refresh that finds nothing, so only the probes matter.</summary>
    private static StubHttpClient QuietRefresh()
    {
        var http = new StubHttpClient();
        http.On("GET", "/me", """{"id":"principal-1"}""");
        http.On("GET", "roleEligibilitySchedules/filterByCurrentUser", """{"value":[]}""");
        http.On("GET", "roleAssignmentScheduleInstances", """{"value":[]}""");
        http.On("GET", "roleAssignmentScheduleRequests", """{"value":[]}""");
        http.On("GET", "management.azure.com", """{"value":[]}""");
        http.On("GET", "privilegedAccess/group", """{"value":[]}""");
        return http;
    }

    private static AppState AvailableState() => new()
    {
        Identities = [Sample.Identity()],
        Tenants = [Sample.Tenant() with { AccessPackagesAvailable = true }],
    };

    private static FakeTokenProvider EntitledTokens()
    {
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity());
        tokens.SetToken(Sample.IdentityId, Sample.TenantId, Token(Entitlement));
        return tokens;
    }

    [Fact]
    public async Task RefreshProbesTheTokenForTheEntitlementScope()
    {
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        using var test = await TestModel.BootstrappedAsync(state, http: QuietRefresh(), online: true, tokens: EntitledTokens());

        test.Model.Tenant(Sample.TenantKey)!.AccessPackagesAvailable.Should().BeTrue();
        test.Model.AccessPackagesAvailable(Sample.TenantKey).Should().BeTrue();
    }

    [Fact]
    public async Task TokenWithoutTheScopeLeavesTheEntryPointHidden()
    {
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity());
        tokens.SetToken(Sample.IdentityId, Sample.TenantId, Token("User.Read RoleAssignmentSchedule.ReadWrite.Directory"));
        using var test = await TestModel.BootstrappedAsync(state, http: QuietRefresh(), online: true, tokens: tokens);

        test.Model.Tenant(Sample.TenantKey)!.AccessPackagesAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task FirstPartyAccountsNeverGetTheEntryPoint()
    {
        var identity = Sample.Identity(method: SignInMethod.AzureCLI);
        var state = new AppState { Identities = [identity], Tenants = [Sample.Tenant()] };
        using var test = await TestModel.BootstrappedAsync(state, http: QuietRefresh(), online: true, tokens: EntitledTokens());

        // The Entra provider is skipped for first-party sign-ins, so the probe never runs and the flag stays unset.
        test.Model.AccessPackagesAvailable(Sample.TenantKey).Should().BeFalse();
    }

    [Fact]
    public async Task FirstPollBaselinesAndTheNextOneNotifies()
    {
        var http = QuietRefresh();
        http.On("GET", "assignmentRequests/filterByCurrentUser", Fixtures.Text("ap-requests"));
        http.On("GET", "assignments/filterByCurrentUser", Fixtures.Text("ap-assignments"));
        using var test = await TestModel.BootstrappedAsync(AvailableState(), http: http, online: true, tokens: EntitledTokens());
        var model = test.Model;

        await model.PollAccessPackagesAsync(Sample.TenantKey);

        test.Notifier.Posted.Should().BeEmpty("the first sight of a tenant is a baseline");
        model.AccessPackageSnapshot(Sample.TenantKey)!.Requests.Should().HaveCount(4);
        model.AccessPackagesPolledAt(Sample.TenantKey).Should().NotBeNull();
        model.AccessPackagesPolling.Should().BeEmpty();
        test.Notifier.PackageExpiries.Select(e => e.Id).Should().Equal("asg-1");

        // The pending request is delivered and the on-call assignment disappears before its end date.
        http.On("GET", "assignmentRequests/filterByCurrentUser", Fixtures.Text("ap-requests").Replace("\"state\": \"pendingApproval\"", "\"state\": \"delivered\""));
        http.On("GET", "assignments/filterByCurrentUser", """{"value":[]}""");
        await model.PollAccessPackagesAsync(Sample.TenantKey);

        test.Notifier.Posted.Should().BeEquivalentTo(
        [
            ("Access package approved", "Azure Sandbox Contributor in Contoso"),
            ("Access package revoked", "Exchange Operations in Contoso"),
            ("Access package revoked", "Developer Baseline in Contoso"),
        ]);
        test.Notifier.PackageExpiries.Should().BeEmpty();
    }

    [Fact]
    public async Task PollIfDueHonoursTheThrottleUnlessForced()
    {
        var http = QuietRefresh();
        http.On("GET", "assignmentRequests/filterByCurrentUser", """{"value":[]}""");
        http.On("GET", "assignments/filterByCurrentUser", """{"value":[]}""");
        using var test = await TestModel.BootstrappedAsync(AvailableState(), http: http, online: true, tokens: EntitledTokens());
        var model = test.Model;

        await model.PollAccessPackagesIfDueAsync();
        http.RequestsMatching("assignmentRequests/filterByCurrentUser").Should().HaveCount(1);

        await model.PollAccessPackagesIfDueAsync();
        http.RequestsMatching("assignmentRequests/filterByCurrentUser").Should().HaveCount(1, "a poll within 15 minutes is skipped");

        await model.PollAccessPackagesIfDueAsync(force: true);
        http.RequestsMatching("assignmentRequests/filterByCurrentUser").Should().HaveCount(2);
    }

    [Fact]
    public async Task PollSkipsTenantsWithoutTheScope()
    {
        var http = QuietRefresh();
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant() with { AccessPackagesAvailable = false }] };
        // An opaque token: the refresh's probe learns nothing and keeps the stored answer.
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity());
        using var test = await TestModel.BootstrappedAsync(state, http: http, online: true, tokens: tokens);

        await test.Model.PollAccessPackagesIfDueAsync(force: true);

        http.RequestsMatching("entitlementManagement").Should().BeEmpty();
    }

    [Fact]
    public async Task ConsentRefusalIsKeptAsTheTenantError()
    {
        var http = QuietRefresh();
        http.On("GET", "assignmentRequests/filterByCurrentUser", """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""", status: 403);
        using var test = await TestModel.BootstrappedAsync(AvailableState(), http: http, online: true, tokens: EntitledTokens());
        var model = test.Model;

        await model.PollAccessPackagesAsync(Sample.TenantKey);

        model.AccessPackageConsentError(Sample.TenantKey).Should().Be(AppModel.ConsentMessage);
        model.AccessPackageSnapshot(Sample.TenantKey).Should().BeNull();
        model.AccessPackagesPolling.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestSubmitsThenPollsSoTheRequestedTabShowsIt()
    {
        var http = QuietRefresh();
        http.On("GET", "assignmentRequests/filterByCurrentUser", """{"value":[]}""");
        http.On("GET", "assignments/filterByCurrentUser", """{"value":[]}""");
        http.On("POST", "assignmentRequests", Fixtures.Text("ap-request-created"), status: 201);
        using var test = await TestModel.BootstrappedAsync(AvailableState(), http: http, online: true, tokens: EntitledTokens());
        var model = test.Model;

        await model.RequestPackageAsync(Sample.TenantKey, "pkg-sandbox", "pol-eng", "Need a sandbox");

        var post = http.RequestsMatching("assignmentRequests").Single(r => r.Method == "POST");
        Encoding.UTF8.GetString(post.Body ?? []).Should().Contain("\"assignmentPolicyId\":\"pol-eng\"").And.Contain("Need a sandbox");
        http.RequestsMatching("assignmentRequests/filterByCurrentUser").Should().HaveCount(1, "the request re-polls");
        model.AccessPackagesPolledAt(Sample.TenantKey).Should().NotBeNull();
    }

    [Fact]
    public async Task NewRolesAreAnnouncedOnceAndClearedOnTheSecondPanelOpen()
    {
        var http = QuietRefresh();
        http.On("GET", "roleEligibilitySchedules/filterByCurrentUser", Fixtures.Text("entra-eligible"));
        http.On("GET", "roleManagementPolicyAssignments", Fixtures.Text("entra-policy"));
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        using var test = await TestModel.BootstrappedAsync(state, http: http, online: true, tokens: EntitledTokens());
        var model = test.Model;
        var roles = model.RolesFor(Sample.TenantKey);
        roles.Should().HaveCountGreaterThan(1);

        // The first discovery only baselines.
        test.Notifier.Posted.Should().BeEmpty();
        roles.Should().OnlyContain(r => !model.IsRoleNew(r.Key));

        // One role disappears and comes back: that is news, once.
        var first = roles[0];
        await model.ObserveDiscoveredRolesAsync(Sample.TenantKey, [.. roles.Skip(1)]);
        test.Notifier.Posted.Should().BeEmpty();
        await model.ObserveDiscoveredRolesAsync(Sample.TenantKey, roles);
        test.Notifier.Posted.Should().ContainSingle().Which.Should().Be(("New roles available in Contoso", first.DisplayName));
        model.IsRoleNew(first.Key).Should().BeTrue();

        model.PanelOpened();
        model.IsRoleNew(first.Key).Should().BeTrue("the marker survives the first open");
        model.PanelOpened();
        model.IsRoleNew(first.Key).Should().BeFalse("the second open clears it");
        // The save itself lands on a background task; the state handed to it is what matters here.
        model.State.RoleTrackerFor(Sample.TenantKey).New.Should().BeEmpty("the cleared marker goes into the persisted state");
    }

    [Fact]
    public async Task RetryDiscoveryForgetsTheAvailabilityAnswer()
    {
        using var test = await TestModel.BootstrappedAsync(AvailableState());
        var model = test.Model;
        model.AccessPackageErrors[Sample.TenantKey] = "boom";

        await model.RetryDiscoveryAsync(Sample.TenantKey);

        model.Tenant(Sample.TenantKey)!.AccessPackagesAvailable.Should().BeNull();
        model.AccessPackageErrors.Should().BeEmpty();
    }
}
