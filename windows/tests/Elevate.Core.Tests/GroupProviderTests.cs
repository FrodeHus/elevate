using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Support;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>GroupProviderTests</c>.</summary>
public class GroupProviderTests
{
    private static readonly Identity TestIdentity = new("id1", "u@contoso.com", "U", "t-home");
    private static readonly TenantContext Tenant = new("id1", "t1", "Contoso", TenantSource.Home);

    private static (GroupProvider Provider, StubHttpClient Http, FakeTokenProvider Tokens) MakeProvider()
    {
        var http = new StubHttpClient();
        var tokens = new FakeTokenProvider();
        return (new GroupProvider(http, tokens), http, tokens);
    }

    /// <summary>A JWT-shaped Graph token whose <c>oid</c> names the caller.</summary>
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

    private static (GroupProvider Provider, StubHttpClient Http) MakeCallerProvider()
    {
        var http = new StubHttpClient();
        return (new GroupProvider(http, new JwtTokenProvider("caller-oid")), http);
    }

    private static EligibleRole OpsMember => new(
        new RoleKey("id1", "t1", new GroupScope("grp-ops", GroupAccess.Member)),
        "Ops Admins", RoleSource.Discovered, RolePolicy.ManualDefault, Detail: "member");

    private static JsonObject PostBody(StubHttpClient http) =>
        JsonNode.Parse(Encoding.UTF8.GetString(
            http.RequestsMatching("assignmentScheduleRequests").First(r => r.Method == "POST").Body!))!.AsObject();

    private static byte[] ActivateResponse(Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(Fixtures.Text("group-activate-response"))!.AsObject();
        edit(json);
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    [Fact]
    public async Task EligibleRoles_ListsGroupsAcrossPagesWithAccessCaption()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.Data("group-eligible"));
        http.On("GET", "skiptoken=page2", body: Fixtures.Data("group-eligible-page2"));

        var roles = await provider.EligibleRolesAsync(TestIdentity, Tenant);

        roles.Select(r => r.DisplayName).Should().Equal("Dev Contributors", "Ops Admins", "Security Owners");
        roles.Select(r => r.Detail).Should().Equal("member", "member", "owner");
        roles[1].Key.Scope.Should().Be(new GroupScope("grp-ops", GroupAccess.Member));
        roles[2].Key.Scope.Should().Be(new GroupScope("grp-sec", GroupAccess.Owner));
        roles[2].ViaGroup.Should().Be("group");
        roles[1].ViaGroup.Should().BeNull();
        roles.Should().OnlyContain(r => r.Source == RoleSource.Discovered && r.Key.TenantId == "t1");

        var first = http.Requests[0];
        first.Headers["Authorization"].Should().Be("Bearer token-t1");
        first.Url.AbsoluteUri.Should().Contain("identityGovernance/privilegedAccess/group/eligibilityScheduleInstances/filterByCurrentUser(on='principal')");
        first.Url.AbsoluteUri.Should().Contain("expand=group");
    }

    [Fact]
    public async Task ActiveAssignments_KeepsActivatedOnlyAndMergesPending()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "assignmentScheduleInstances/filterByCurrentUser", body: Fixtures.Data("group-active"));
        http.On("GET", "assignmentScheduleRequests/filterByCurrentUser", body: Fixtures.Data("group-pending"));

        var active = await provider.ActiveAssignmentsAsync(TestIdentity, Tenant);

        active.Should().HaveCount(2);
        var ops = active.Single(a => a.RoleKey.Scope == new GroupScope("grp-ops", GroupAccess.Member));
        ops.Status.Should().Be(AssignmentStatus.Active);
        ops.AssignmentId.Should().Be("ginst-1");
        ops.EndDateTime.Should().Be(GraphJson.ParseDate("2026-09-04T16:00:00Z"));

        var sec = active.Single(a => a.RoleKey.Scope == new GroupScope("grp-sec", GroupAccess.Owner));
        sec.Status.Should().Be(AssignmentStatus.PendingApproval);
        sec.AssignmentId.Should().Be("greq-9");

        var url = http.RequestsMatching("assignmentScheduleRequests")[0].Url.AbsoluteUri;
        (url.Contains("status eq 'PendingApproval'", StringComparison.Ordinal)
            || url.Contains("status%20eq%20'PendingApproval'", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Fact]
    public async Task ActiveAssignments_FutureRequestsAppearAsScheduled()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "assignmentScheduleInstances/filterByCurrentUser", """{"value":[]}""");
        http.On("GET", "assignmentScheduleRequests/filterByCurrentUser", body: Fixtures.Data("group-pending"));

        var active = await provider.ActiveAssignmentsAsync(TestIdentity, Tenant);

        active.Should().HaveCount(2);                        // greq-9 started this afternoon: not upcoming
        var ops = active.Single(a => a.RoleKey.Scope == new GroupScope("grp-ops", GroupAccess.Member));
        ops.Status.Should().Be(AssignmentStatus.Scheduled);
        ops.AssignmentId.Should().Be("greq-1");
        ops.StartDateTime.Should().Be(GraphJson.ParseDate("2099-01-01T09:00:00Z"));
        ops.EndDateTime.Should().Be(GraphJson.ParseDate("2099-01-01T11:00:00Z"));

        var url = Uri.UnescapeDataString(http.RequestsMatching("assignmentScheduleRequests")[0].Url.AbsoluteUri);
        url.Should().Contain("status eq 'ScheduleCreated'").And.Contain("status eq 'Provisioned'");
        // The schedules list is no longer read at all.
        http.RequestsMatching("assignmentSchedules/filterByCurrentUser").Should().BeEmpty();
    }

    [Fact]
    public async Task ActiveAssignments_FutureRequestDoesNotOverrideAnActiveAssignment()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "assignmentScheduleInstances/filterByCurrentUser", body: Fixtures.Data("group-active"));
        http.On("GET", "assignmentScheduleRequests/filterByCurrentUser", body: Fixtures.Data("group-pending"));

        var active = await provider.ActiveAssignmentsAsync(TestIdentity, Tenant);

        active.Should().HaveCount(2);
        // greq-1 is a future request for the same group: the live assignment still wins.
        var ops = active.Single(a => a.RoleKey.Scope == new GroupScope("grp-ops", GroupAccess.Member));
        ops.Status.Should().Be(AssignmentStatus.Active);
        ops.AssignmentId.Should().Be("ginst-1");
    }

    [Fact]
    public async Task ForbiddenRead_MapsToConsentRequired()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "eligibilityScheduleInstances",
            """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""", 403);

        var act = () => provider.EligibleRolesAsync(TestIdentity, Tenant);

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.ConsentRequired);
    }

    [Fact]
    public async Task Policy_IsReadPerGroupAndAccess()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleManagementPolicyAssignments", body: Fixtures.Data("group-policy"));

        var policy = await provider.PolicyAsync(OpsMember, TestIdentity);

        policy.MaximumDuration.Should().Be(TimeSpan.FromHours(8));
        policy.DefaultDuration.Should().Be(TimeSpan.FromHours(8));
        policy.RequiresJustification.Should().BeTrue();
        policy.RequiresTicket.Should().BeTrue();
        policy.RequiresMfa.Should().BeFalse();
        policy.RequiresApproval.Should().BeFalse();
        policy.AuthenticationContext.Should().Be("c3");

        var url = Uri.UnescapeDataString(http.Requests[0].Url.AbsoluteUri);
        url.Should().Contain("scopeId eq 'grp-ops'").And.Contain("scopeType eq 'Group'").And.Contain("roleDefinitionId eq 'member'");
        url.Should().Contain("$expand=policy($expand=rules)");
    }

    [Fact]
    public async Task Activate_PostsSelfActivateAsTheCaller()
    {
        var (provider, http) = MakeCallerProvider();
        http.On("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.Data("group-eligible-page2"));
        http.On("POST", "assignmentScheduleRequests", 201, body: Fixtures.Data("group-activate-response"));

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(OpsMember.Key, TimeSpan.FromSeconds(7200), "INC-7", new TicketInfo("42", "Jira")),
            TestIdentity);

        assignment.Status.Should().Be(AssignmentStatus.Active);
        assignment.AssignmentId.Should().Be("greq-new");
        assignment.EndDateTime.Should().Be(GraphJson.ParseDate("2026-09-04T14:00:00Z"));
        assignment.RoleKey.Should().Be(OpsMember.Key);

        var post = http.RequestsMatching("assignmentScheduleRequests").First(r => r.Method == "POST");
        post.Url.AbsoluteUri.Should().EndWith("/identityGovernance/privilegedAccess/group/assignmentScheduleRequests");
        var body = JsonNode.Parse(Encoding.UTF8.GetString(post.Body!))!.AsObject();
        body["action"]!.GetValue<string>().Should().Be("selfActivate");
        body["accessId"]!.GetValue<string>().Should().Be("member");
        body["groupId"]!.GetValue<string>().Should().Be("grp-ops");
        body["principalId"]!.GetValue<string>().Should().Be("caller-oid");
        body["justification"]!.GetValue<string>().Should().Be("INC-7");
        body["ticketInfo"]!["ticketNumber"]!.GetValue<string>().Should().Be("42");
        var expiration = body["scheduleInfo"]!["expiration"]!;
        expiration["type"]!.GetValue<string>().Should().Be("afterDuration");
        expiration["duration"]!.GetValue<string>().Should().Be("PT2H");
    }

    [Fact]
    public async Task Activate_OpaqueTokenFallsBackToEligibilityPrincipal()
    {
        var (provider, http, _) = MakeProvider();   // FakeTokenProvider returns "token-t1", not a JWT
        http.On("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.Data("group-eligible"));
        http.On("GET", "skiptoken=page2", body: Fixtures.Data("group-eligible-page2"));
        http.On("POST", "assignmentScheduleRequests", 201, body: Fixtures.Data("group-activate-response"));

        await provider.ActivateAsync(new ActivationRequest(OpsMember.Key, TimeSpan.FromSeconds(3600), "x"), TestIdentity);

        PostBody(http)["principalId"]!.GetValue<string>().Should().Be("user-obj-1");
    }

    [Fact]
    public async Task Activate_WithFutureStart_SendsItAndReportsScheduled()
    {
        var (provider, http) = MakeCallerProvider();
        http.On("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.Data("group-eligible-page2"));
        // Same response as a normal activation, but with no echoed end: the service works it out from the duration.
        http.On("POST", "assignmentScheduleRequests", 201, body: ActivateResponse(json =>
            json["scheduleInfo"]!["expiration"] = new JsonObject { ["type"] = "afterDuration", ["duration"] = "PT2H" }));
        var start = GraphJson.ParseDate("2099-01-01T09:00:00Z")!.Value;

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(OpsMember.Key, TimeSpan.FromSeconds(7200), "later", StartDateTime: start), TestIdentity);

        var schedule = PostBody(http)["scheduleInfo"]!;
        GraphJson.ParseDate(schedule["startDateTime"]!.GetValue<string>()).Should().Be(start);
        // The response echoes a start in the past; the request's future start wins.
        assignment.Status.Should().Be(AssignmentStatus.Scheduled);
        assignment.StartDateTime.Should().Be(start);
        assignment.EndDateTime.Should().Be(GraphJson.ParseDate("2099-01-01T11:00:00Z"));
    }

    [Fact]
    public async Task Activate_FutureStartDoesNotMaskPendingApproval()
    {
        var (provider, http) = MakeCallerProvider();
        http.On("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.Data("group-eligible-page2"));
        http.On("POST", "assignmentScheduleRequests", 201, body: ActivateResponse(json => json["status"] = "PendingApproval"));
        var start = GraphJson.ParseDate("2099-01-01T09:00:00Z")!.Value;

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(OpsMember.Key, TimeSpan.FromSeconds(3600), "later", StartDateTime: start), TestIdentity);

        assignment.Status.Should().Be(AssignmentStatus.PendingApproval);
    }

    [Fact]
    public async Task Activate_FutureStartDoesNotMaskFailure()
    {
        var (provider, http) = MakeCallerProvider();
        http.On("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.Data("group-eligible-page2"));
        http.On("POST", "assignmentScheduleRequests", 201, body: ActivateResponse(json => json["status"] = "Denied"));
        var start = GraphJson.ParseDate("2099-01-01T09:00:00Z")!.Value;

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(OpsMember.Key, TimeSpan.FromSeconds(3600), "later", StartDateTime: start), TestIdentity);

        assignment.Status.Should().Be(AssignmentStatus.Failed("Denied"));
    }

    [Fact]
    public async Task Activate_PendingApprovalResponseIsReported()
    {
        var (provider, http) = MakeCallerProvider();
        http.On("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.Data("group-eligible-page2"));
        http.On("POST", "assignmentScheduleRequests",
            """{"id":"greq-p","status":"PendingApproval","groupId":"grp-ops","accessId":"member","scheduleInfo":{"startDateTime":"2026-09-04T12:00:00Z"}}""",
            201);

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(OpsMember.Key, TimeSpan.FromSeconds(3600), "x"), TestIdentity);

        assignment.Status.Should().Be(AssignmentStatus.PendingApproval);
        assignment.EndDateTime.Should().BeNull();
    }

    [Fact]
    public async Task Deactivate_PostsSelfDeactivateWithoutSchedule()
    {
        var (provider, http) = MakeCallerProvider();
        http.On("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.Data("group-eligible-page2"));
        http.On("POST", "assignmentScheduleRequests", 201, body: Fixtures.Data("group-activate-response"));
        var assignment = new ActiveAssignment(OpsMember.Key, "ginst-1", DateTimeOffset.UtcNow, null, AssignmentStatus.Active);

        await provider.DeactivateAsync(assignment, TestIdentity);

        var body = PostBody(http);
        body["action"]!.GetValue<string>().Should().Be("selfDeactivate");
        body["principalId"]!.GetValue<string>().Should().Be("caller-oid");
        body["groupId"]!.GetValue<string>().Should().Be("grp-ops");
        body["accessId"]!.GetValue<string>().Should().Be("member");
        body.ContainsKey("scheduleInfo").Should().BeFalse();
    }

    [Fact]
    public async Task CancelPendingRequest_PostsToTheRequestCancelAction()
    {
        var (provider, http, _) = MakeProvider();
        http.On("POST", "/cancel", 204);
        var assignment = new ActiveAssignment(OpsMember.Key, "greq-9", DateTimeOffset.UtcNow, null, AssignmentStatus.PendingApproval);

        await provider.CancelPendingRequestAsync(assignment, TestIdentity);

        var post = http.Requests[0];
        post.Method.Should().Be("POST");
        post.Url.AbsoluteUri.Should().EndWith("/identityGovernance/privilegedAccess/group/assignmentScheduleRequests/greq-9/cancel");
    }
}
