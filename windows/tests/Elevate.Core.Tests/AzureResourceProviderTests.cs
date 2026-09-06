using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Providers;
using Elevate.Core.Support;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>AzureResourceProviderTests</c>.</summary>
public class AzureResourceProviderTests
{
    private const string ContributorId =
        "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c";

    private const string ReaderId =
        "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7";

    private static readonly Identity TestIdentity = new("id1", "u@contoso.com", "U", "t1");
    private static readonly TenantContext Tenant = new("id1", "t1", "Contoso", TenantSource.Home);

    private static (AzureResourceProvider Provider, StubHttpClient Http, FakeTokenProvider Tokens) MakeProvider()
    {
        var http = new StubHttpClient();
        var tokens = new FakeTokenProvider();
        return (new AzureResourceProvider(http, tokens), http, tokens);
    }

    /// <summary>A JWT-shaped ARM token whose <c>oid</c> names the caller, as the real providers return.</summary>
    private sealed class JwtTokenProvider(string oid) : ITokenProvider
    {
        private string Token
        {
            get
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["oid"] = oid, ["tid"] = "t1" });
                var b64 = Convert.ToBase64String(payload).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                return $"eyJhbGciOiJub25lIn0.{b64}.sig";
            }
        }

        public Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct)
            => Task.FromException<Identity>(new PimException(PimErrorKind.NotEligible));

        public Task SignOutAsync(Identity identity, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Identity>>([]);

        public Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct)
            => Task.FromResult(Token);

        public Task<string> AcquireInteractivelyAsync(
            Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct)
            => Task.FromResult(Token);
    }

    private static EligibleRole Contributor => new(
        new RoleKey("id1", "t1", new AzureResourceScope("/subscriptions/sub-1", ContributorId)),
        "Contributor", RoleSource.Discovered, RolePolicy.ManualDefault, Detail: "Pay-As-You-Go · subscription");

    private static JsonObject PutProperties(StubHttpClient http)
    {
        var put = http.RequestsMatching("roleAssignmentScheduleRequests").First(r => r.Method == "PUT");
        return JsonNode.Parse(Encoding.UTF8.GetString(put.Body!))!.AsObject()["properties"]!.AsObject();
    }

    private static byte[] ActivateResponse(string status)
    {
        var json = JsonNode.Parse(Fixtures.Text("arm-activate-response"))!.AsObject();
        json["properties"]!.AsObject()["status"] = status;
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    private static void StubEligibilityPages(StubHttpClient http)
    {
        http.On("GET", "roleEligibilityScheduleInstances?", body: Fixtures.Data("arm-eligible"));
        http.On("GET", "skiptoken=page2", body: Fixtures.Data("arm-eligible-page2"));
    }

    [Fact]
    public async Task EligibleRoles_ListsAcrossPagesWithScopeCaption()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleEligibilityScheduleInstances", body: Fixtures.Data("arm-eligible"));
        http.On("GET", "skiptoken=page2", body: Fixtures.Data("arm-eligible-page2"));

        var roles = await provider.EligibleRolesAsync(TestIdentity, Tenant);

        roles.Select(r => r.DisplayName).Should().Equal("Contributor", "Reader");
        roles[0].Detail.Should().Be("Pay-As-You-Go · subscription");
        roles[1].Detail.Should().Be("rg-ops · resource group");
        roles[0].Key.Scope.Should().Be(new AzureResourceScope("/subscriptions/sub-1", ContributorId));
        roles.Should().OnlyContain(r => r.Source == RoleSource.Discovered && r.Key.TenantId == "t1");
        roles.Single(r => r.ViaGroup is not null).ViaGroup.Should().Be("Platform Team");

        var first = http.Requests[0];
        first.Url.AbsoluteUri.Should().StartWith(
            "https://management.azure.com/providers/Microsoft.Authorization/roleEligibilityScheduleInstances");
        first.Url.AbsoluteUri.Should().Contain("api-version=2020-10-01").And.Contain("asTarget()");
        first.Headers["Authorization"].Should().Be("Bearer token-t1");
        http.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ActiveAssignments_ListsActivatedAndPending()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleAssignmentScheduleInstances", body: Fixtures.Data("arm-active"));
        http.On("GET", "roleAssignmentScheduleRequests", body: Fixtures.Data("arm-pending"));

        var active = await provider.ActiveAssignmentsAsync(TestIdentity, Tenant);

        active.Should().HaveCount(2);
        var contributor = active.Single(a => a.RoleKey.Scope == new AzureResourceScope("/subscriptions/sub-1", ContributorId));
        contributor.Status.Should().Be(AssignmentStatus.Active);
        contributor.AssignmentId.Should().Be("inst-1");
        contributor.EndDateTime.Should().Be(GraphJson.ParseDate("2026-09-04T12:00:00Z"));

        var reader = active.Single(a =>
            a.RoleKey.Scope == new AzureResourceScope("/subscriptions/sub-1/resourceGroups/rg-ops", ReaderId));
        reader.Status.Should().Be(AssignmentStatus.PendingApproval);
        reader.AssignmentId.Should().Be("req-77");
    }

    [Fact]
    public async Task ActiveAssignments_FutureRequestsAppearAsScheduled()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleAssignmentScheduleInstances", """{"value":[]}""");
        http.On("GET", "roleAssignmentScheduleRequests", body: Fixtures.Data("arm-pending"));

        var active = await provider.ActiveAssignmentsAsync(TestIdentity, Tenant);

        active.Should().HaveCount(2);                        // req-70 started this morning: not upcoming
        var contributor = active.Single(a => a.RoleKey.Scope == new AzureResourceScope("/subscriptions/sub-1", ContributorId));
        contributor.Status.Should().Be(AssignmentStatus.Scheduled);
        contributor.AssignmentId.Should().Be("req-71");
        contributor.StartDateTime.Should().Be(GraphJson.ParseDate("2099-01-01T09:00:00Z"));
        contributor.EndDateTime.Should().Be(GraphJson.ParseDate("2099-01-01T11:00:00Z"));

        // req-72 is a future request on the pending request's key: the pending one still wins.
        var reader = active.Single(a =>
            a.RoleKey.Scope == new AzureResourceScope("/subscriptions/sub-1/resourceGroups/rg-ops", ReaderId));
        reader.Status.Should().Be(AssignmentStatus.PendingApproval);
        reader.AssignmentId.Should().Be("req-77");

        // The schedules list is no longer read at all.
        http.RequestsMatching("roleAssignmentSchedules?").Should().BeEmpty();
    }

    [Fact]
    public async Task ActiveAssignments_FutureRequestDoesNotOverrideAnActiveAssignment()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleAssignmentScheduleInstances", body: Fixtures.Data("arm-active"));
        http.On("GET", "roleAssignmentScheduleRequests", body: Fixtures.Data("arm-pending"));

        var active = await provider.ActiveAssignmentsAsync(TestIdentity, Tenant);

        active.Should().HaveCount(2);
        var contributor = active.Single(a => a.RoleKey.Scope == new AzureResourceScope("/subscriptions/sub-1", ContributorId));
        contributor.Status.Should().Be(AssignmentStatus.Active);
        contributor.AssignmentId.Should().Be("inst-1");

        var reader = active.Single(a =>
            a.RoleKey.Scope == new AzureResourceScope("/subscriptions/sub-1/resourceGroups/rg-ops", ReaderId));
        reader.Status.Should().Be(AssignmentStatus.PendingApproval);
        reader.AssignmentId.Should().Be("req-77");
    }

    [Fact]
    public async Task ForbiddenIsNotTreatedAsConsent()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleEligibilityScheduleInstances",
            """{"error":{"code":"AuthorizationFailed","message":"x"}}""", 403);

        var act = () => provider.EligibleRolesAsync(TestIdentity, Tenant);

        var thrown = (await act.Should().ThrowAsync<PimException>()).Which;
        thrown.Kind.Should().Be(PimErrorKind.PolicyViolation);
        thrown.Detail.Should().Be("Not permitted at this scope");
    }

    [Fact]
    public async Task Policy_ReadsEndUserRulesForTheMatchingRoleAtScope()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleManagementPolicyAssignments", body: Fixtures.Data("arm-policy"));

        var policy = await provider.PolicyAsync(Contributor, TestIdentity);

        policy.MaximumDuration.Should().Be(TimeSpan.FromHours(4));
        policy.DefaultDuration.Should().Be(TimeSpan.FromHours(4));
        policy.RequiresJustification.Should().BeTrue();
        policy.RequiresMfa.Should().BeTrue();
        policy.RequiresTicket.Should().BeTrue();
        policy.RequiresApproval.Should().BeTrue();
        policy.AuthenticationContext.Should().Be("c2");

        http.Requests[0].Url.AbsoluteUri.Should().StartWith(
            "https://management.azure.com/subscriptions/sub-1/providers/Microsoft.Authorization/roleManagementPolicyAssignments");
    }

    [Fact]
    public async Task Activate_GroupInheritedEligibilityActivatesAsTheCaller()
    {
        // The eligibility instance names the group ("user-obj-1" stands in); ARM wants the requestor's own oid.
        var http = new StubHttpClient();
        var provider = new AzureResourceProvider(http, new JwtTokenProvider("caller-oid"));
        StubEligibilityPages(http);
        http.On("PUT", "roleAssignmentScheduleRequests", 201, body: Fixtures.Data("arm-activate-response"));

        await provider.ActivateAsync(
            new ActivationRequest(Contributor.Key, TimeSpan.FromHours(1), "x"), TestIdentity);

        var props = PutProperties(http);
        props["principalId"]!.GetValue<string>().Should().Be("caller-oid");
        props["linkedRoleEligibilityScheduleId"]!.GetValue<string>().Should().Be("b1477448-2cc6-4ceb-93b4-54a202a89413");
    }

    [Fact]
    public async Task Activate_LooksUpEligibilityAndPutsSelfActivate()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleEligibilityScheduleInstances", body: Fixtures.Data("arm-eligible-page2"));
        StubEligibilityPages(http);
        http.On("PUT", "roleAssignmentScheduleRequests", 201, body: Fixtures.Data("arm-activate-response"));

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(Contributor.Key, TimeSpan.FromHours(2), "INC-1", new TicketInfo("42", "Jira")),
            TestIdentity);

        assignment.Status.Should().Be(AssignmentStatus.Active);
        assignment.AssignmentId.Should().Be("fea7a502-9a96-4806-a26f-eee560e52045");
        assignment.EndDateTime.Should().Be(GraphJson.ParseDate("2026-09-04T11:00:00Z"));

        var put = http.RequestsMatching("roleAssignmentScheduleRequests").First(r => r.Method == "PUT");
        put.Method.Should().Be("PUT");
        put.Url.AbsoluteUri.Should().StartWith(
            "https://management.azure.com/subscriptions/sub-1/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/");
        var name = put.Url.AbsolutePath.Split('/')[^1];
        Guid.TryParse(name, out _).Should().BeTrue();

        var props = PutProperties(http);
        props["requestType"]!.GetValue<string>().Should().Be("SelfActivate");
        props["principalId"]!.GetValue<string>().Should().Be("user-obj-1");
        props["roleDefinitionId"]!.GetValue<string>().Should().Be(ContributorId);
        props["linkedRoleEligibilityScheduleId"]!.GetValue<string>().Should().Be("b1477448-2cc6-4ceb-93b4-54a202a89413");
        props["justification"]!.GetValue<string>().Should().Be("INC-1");
        props["ticketInfo"]!["ticketNumber"]!.GetValue<string>().Should().Be("42");

        var expiration = props["scheduleInfo"]!["expiration"]!;
        expiration["type"]!.GetValue<string>().Should().Be("AfterDuration");
        expiration["duration"]!.GetValue<string>().Should().Be("PT2H");
    }

    [Fact]
    public async Task Activate_WithFutureStartSendsItAndReportsScheduled()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleEligibilityScheduleInstances", body: Fixtures.Data("arm-eligible-page2"));
        StubEligibilityPages(http);
        http.On("PUT", "roleAssignmentScheduleRequests", 201, body: Fixtures.Data("arm-activate-response"));
        var start = GraphJson.ParseDate("2099-01-01T09:00:00Z")!.Value;

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(Contributor.Key, TimeSpan.FromHours(2), "later", StartDateTime: start), TestIdentity);

        var sent = PutProperties(http)["scheduleInfo"]!["startDateTime"]!.GetValue<string>();
        GraphJson.ParseDate(sent).Should().Be(start);

        // The response echoes a start in the past; the request's future start wins.
        assignment.Status.Should().Be(AssignmentStatus.Scheduled);
        assignment.StartDateTime.Should().Be(start);
        assignment.EndDateTime.Should().Be(GraphJson.ParseDate("2099-01-01T11:00:00Z"));
    }

    [Fact]
    public async Task Activate_FutureStartDoesNotMaskPendingApproval()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleEligibilityScheduleInstances", body: Fixtures.Data("arm-eligible-page2"));
        StubEligibilityPages(http);
        http.On("PUT", "roleAssignmentScheduleRequests", 201, body: ActivateResponse("PendingApproval"));

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(
                Contributor.Key, TimeSpan.FromHours(1), "later",
                StartDateTime: GraphJson.ParseDate("2099-01-01T09:00:00Z")),
            TestIdentity);

        assignment.Status.Should().Be(AssignmentStatus.PendingApproval);
    }

    [Fact]
    public async Task Activate_FutureStartDoesNotMaskFailure()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleEligibilityScheduleInstances", body: Fixtures.Data("arm-eligible-page2"));
        StubEligibilityPages(http);
        http.On("PUT", "roleAssignmentScheduleRequests", 201, body: ActivateResponse("Denied"));

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(
                Contributor.Key, TimeSpan.FromHours(1), "later",
                StartDateTime: GraphJson.ParseDate("2099-01-01T09:00:00Z")),
            TestIdentity);

        assignment.Status.Should().Be(AssignmentStatus.Failed("Denied"));
    }

    [Fact]
    public async Task Activate_ManualRoleNameIsResolvedFirst()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleDefinitions?", body: Fixtures.Data("arm-roledefinitions"));
        StubEligibilityPages(http);
        http.On("PUT", "roleAssignmentScheduleRequests", 201, body: Fixtures.Data("arm-activate-response"));
        var manualKey = new RoleKey("id1", "t1", new AzureResourceScope("/subscriptions/SUB-1", "Contributor"));

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(manualKey, TimeSpan.FromHours(1), "x"), TestIdentity);

        assignment.Status.Should().Be(AssignmentStatus.Active);
        var defs = http.RequestsMatching("roleDefinitions?")[0].Url.AbsoluteUri;
        defs.Should().Contain("api-version=2022-04-01");
        Uri.UnescapeDataString(defs).ToLowerInvariant().Should().Contain("rolename eq 'contributor'");

        PutProperties(http)["roleDefinitionId"]!.GetValue<string>().Should().Be(ContributorId);

        // The assignment comes back keyed by the resolved id, not by the role name we asked with.
        assignment.RoleKey.Scope.Should().Be(new AzureResourceScope("/subscriptions/SUB-1", ContributorId));
        assignment.RoleKey.IdentityId.Should().Be("id1");
        assignment.RoleKey.TenantId.Should().Be("t1");
    }

    [Fact]
    public async Task ResolveRoleDefinitionId_EscapesAnApostropheInTheODataFilter()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleDefinitions?", body: Fixtures.Data("arm-roledefinitions"));
        StubEligibilityPages(http);

        await provider.ResolveRoleDefinitionIdAsync("O'Brien Operator", "/subscriptions/sub-1", TestIdentity, "t1");

        var defs = http.RequestsMatching("roleDefinitions?")[0].Url;
        var filter = System.Web.HttpUtility.ParseQueryString(defs.Query)["$filter"];
        filter.Should().Be("roleName eq 'O''Brien Operator'");
    }

    [Fact]
    public async Task Activate_WithoutMatchingEligibilityIsNotEligible()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleEligibilityScheduleInstances?", body: Fixtures.Data("arm-eligible-page2"));
        var key = new RoleKey("id1", "t1", new AzureResourceScope("/subscriptions/sub-9", ContributorId));

        var act = () => provider.ActivateAsync(
            new ActivationRequest(key, TimeSpan.FromHours(1), "x"), TestIdentity);

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.NotEligible);
    }

    [Fact]
    public async Task Deactivate_PutsSelfDeactivate()
    {
        var (provider, http, _) = MakeProvider();
        StubEligibilityPages(http);
        http.On("PUT", "roleAssignmentScheduleRequests", 201, body: Fixtures.Data("arm-activate-response"));
        var assignment = new ActiveAssignment(
            Contributor.Key, "inst-1", DateTimeOffset.UtcNow, null, AssignmentStatus.Active);

        await provider.DeactivateAsync(assignment, TestIdentity);

        var props = PutProperties(http);
        props["requestType"]!.GetValue<string>().Should().Be("SelfDeactivate");
        props["linkedRoleEligibilityScheduleId"]!.GetValue<string>().Should().Be("b1477448-2cc6-4ceb-93b4-54a202a89413");
        props.ContainsKey("scheduleInfo").Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_PostsToTheRequestAtItsScope()
    {
        var (provider, http, _) = MakeProvider();
        http.On("POST", "/cancel", 200, body: []);
        var key = new RoleKey("id1", "t1", new AzureResourceScope("/subscriptions/sub-1/resourceGroups/rg-ops", ReaderId));
        var assignment = new ActiveAssignment(key, "req-77", DateTimeOffset.UtcNow, null, AssignmentStatus.PendingApproval);

        await provider.CancelPendingRequestAsync(assignment, TestIdentity);

        var post = http.Requests[0];
        post.Method.Should().Be("POST");
        post.Url.AbsoluteUri.Should().StartWith(
            "https://management.azure.com/subscriptions/sub-1/resourceGroups/rg-ops/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/req-77/cancel");
    }

    [Fact]
    public void ArmUrl_PutsApiVersionFirstAndSortsTheRest()
    {
        var url = AzureResourceProvider.ArmUrl(
            "providers/Microsoft.Authorization/roleDefinitions",
            "2022-04-01",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["$top"] = "1", ["$filter"] = "asTarget()" });

        url.AbsoluteUri.Should().Be(
            "https://management.azure.com/providers/Microsoft.Authorization/roleDefinitions"
            + "?api-version=2022-04-01&$filter=asTarget()&$top=1");
    }
}
