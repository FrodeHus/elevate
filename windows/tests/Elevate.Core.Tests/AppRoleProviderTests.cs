using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>
/// Port of the Swift <c>AppRoleProviderTests</c>. Payloads are trimmed copies of what Graph beta
/// returned for the preview on 2026-11-25.
/// </summary>
public class AppRoleProviderTests
{
    private static readonly Identity TestIdentity = new("id1", "u@contoso.com", "U", "t-home");
    private static readonly TenantContext Tenant = new("id1", "t1", "Contoso", TenantSource.Home);
    private static readonly AppRoleScope Scope = new("sp-1", "role-sec");
    private static readonly RoleKey Key = new("id1", "t1", Scope);

    private const string Eligible = """
        {"value":[{"id":"sp-1_e1","startDateTime":"2026-11-25T06:55:40.8671888Z","endDateTime":null,"principalId":"user-1",
          "appRoleId":"role-sec","servicePrincipalId":"sp-1","memberType":"direct","eligibilityScheduleId":"sp-1_e1",
          "appRole":{"id":"role-sec","displayName":"Security Manager","value":"SecurityManager","origin":"Application"},
          "servicePrincipal":{"id":"sp-1","displayName":"Insights API","appId":null}}]}
        """;

    private const string Instances = """
        {"value":[
         {"id":"sp-1_standing","startDateTime":null,"endDateTime":null,"principalId":"user-1","appRoleId":"role-admin",
          "servicePrincipalId":"sp-1","memberType":"direct","assignmentType":"assigned","assignmentScheduleId":"sp-1_standing"},
         {"id":"sp-1_req-1","startDateTime":"2026-11-25T07:05:00.915844Z","endDateTime":"2026-11-25T07:35:00.454815Z","principalId":"user-1",
          "appRoleId":"role-sec","servicePrincipalId":"sp-1","memberType":"direct","assignmentType":"activated","assignmentScheduleId":"sp-1_req-1"}]}
        """;

    private const string Created = """
        {"id":"req-1","status":"PendingProvisioning","action":"selfActivate","principalId":"user-1","appRoleId":"role-sec",
         "servicePrincipalId":"sp-1","targetScheduleId":"sp-1_req-1","createdDateTime":"2026-11-25T07:04:59.5547522Z",
         "scheduleInfo":{"startDateTime":"2026-11-25T07:05:00.454815Z","recurrence":null,"expiration":{"type":"afterDuration","endDateTime":null,"duration":"PT30M"}}}
        """;

    private static (AppRoleProvider Provider, StubHttpClient Http) MakeProvider()
    {
        var http = new StubHttpClient();
        return (new AppRoleProvider(http, new FakeTokenProvider()), http);
    }

    [Fact]
    public async Task EligibleRoles_ListsAppRolesFromBetaWithTheAppAsDetail()
    {
        var (provider, http) = MakeProvider();
        http.On("GET", "eligibilityScheduleInstances/filterByCurrentUser", Eligible, 200);

        var roles = await provider.EligibleRolesAsync(TestIdentity, Tenant);

        roles.Should().ContainSingle();
        roles[0].DisplayName.Should().Be("Security Manager");
        roles[0].Detail.Should().Be("Insights API");
        roles[0].Key.Should().Be(Key);
        http.Requests[0].Url.AbsoluteUri.Should().Contain(
            "/beta/identityGovernance/privilegedAccess/appRole/eligibilityScheduleInstances/filterByCurrentUser(on='principal')");
    }

    [Fact]
    public async Task ActiveAssignments_ListsActivationsButNotStandingAssignments()
    {
        var (provider, http) = MakeProvider();
        http.On("GET", "assignmentScheduleInstances/filterByCurrentUser", Instances, 200);
        http.On("GET", "assignmentScheduleRequests/filterByCurrentUser", """{"value":[]}""", 200);

        var active = await provider.ActiveAssignmentsAsync(TestIdentity, Tenant);

        active.Should().ContainSingle();
        active[0].RoleKey.Should().Be(Key);
        active[0].Status.Should().Be(AssignmentStatus.Active);
        active[0].ScheduleId.Should().Be("sp-1_req-1");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task ATenantWithoutThePreview_ReadsAsNoAppRoles(int status)
    {
        var (provider, http) = MakeProvider();
        http.On("GET", "privilegedAccess/appRole", """{"error":{"code":"UnknownError","message":"The provider is missing."}}""", status);

        (await provider.EligibleRolesAsync(TestIdentity, Tenant)).Should().BeEmpty();
        (await provider.ActiveAssignmentsAsync(TestIdentity, Tenant)).Should().BeEmpty();
    }

    [Fact]
    public async Task AServerFault_IsAFailedReadNotAnEmptyOne()
    {
        var (provider, http) = MakeProvider();
        http.On("GET", "privilegedAccess/appRole", """{"error":{"code":"ServiceUnavailable","message":"Try later"}}""", 503);

        await ((Func<Task>)(() => provider.EligibleRolesAsync(TestIdentity, Tenant))).Should().ThrowAsync<PimException>();
        await ((Func<Task>)(() => provider.ActiveAssignmentsAsync(TestIdentity, Tenant))).Should().ThrowAsync<PimException>();
    }

    [Fact]
    public async Task Activate_PostsSelfActivateForTheAppRole()
    {
        var (provider, http) = MakeProvider();
        http.On("GET", "eligibilityScheduleInstances/filterByCurrentUser", Eligible, 200);
        http.On("POST", "assignmentScheduleRequests", Created, 201);

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(Key, TimeSpan.FromMinutes(30), "INC-7"), TestIdentity);

        assignment.Status.Should().Be(AssignmentStatus.PendingProvisioning);
        assignment.AssignmentId.Should().Be("req-1");
        assignment.ScheduleId.Should().Be("sp-1_req-1");

        var post = http.RequestsMatching("assignmentScheduleRequests").First(r => r.Method == "POST");
        post.Url.AbsoluteUri.Should().EndWith("/beta/identityGovernance/privilegedAccess/appRole/assignmentScheduleRequests");
        var body = JsonNode.Parse(Encoding.UTF8.GetString(post.Body!))!.AsObject();
        body["action"]!.GetValue<string>().Should().Be("selfActivate");
        body["servicePrincipalId"]!.GetValue<string>().Should().Be("sp-1");
        body["appRoleId"]!.GetValue<string>().Should().Be("role-sec");
        // FakeTokenProvider's token is opaque: the eligibility's principal stands in.
        body["principalId"]!.GetValue<string>().Should().Be("user-1");
        body["scheduleInfo"]!["expiration"]!["duration"]!.GetValue<string>().Should().Be("PT30M");
    }

    [Fact]
    public async Task Deactivate_PostsSelfDeactivateWithTheSchedule()
    {
        var (provider, http) = MakeProvider();
        http.On("GET", "eligibilityScheduleInstances/filterByCurrentUser", Eligible, 200);
        http.On("POST", "assignmentScheduleRequests",
            """{"id":"req-2","status":"Revoked","servicePrincipalId":"sp-1","appRoleId":"role-sec"}""", 201);

        await provider.DeactivateAsync(
            new ActiveAssignment(Key, "sp-1_req-1", DateTimeOffset.UtcNow, null, AssignmentStatus.Active, "sp-1_req-1"), TestIdentity);

        var post = http.RequestsMatching("assignmentScheduleRequests").First(r => r.Method == "POST");
        var body = JsonNode.Parse(Encoding.UTF8.GetString(post.Body!))!.AsObject();
        body["action"]!.GetValue<string>().Should().Be("selfDeactivate");
        body["targetScheduleId"]!.GetValue<string>().Should().Be("sp-1_req-1");
    }

    [Fact]
    public async Task Policy_IsReadPerAppAndRole()
    {
        var (provider, http) = MakeProvider();
        http.On("GET", "roleManagementPolicyAssignments", body: Fixtures.Data("group-policy"));
        var role = new EligibleRole(Key, "Security Manager", RoleSource.Discovered, RolePolicy.ManualDefault, Detail: "Insights API");

        var policy = await provider.PolicyAsync(role, TestIdentity);

        policy.MaximumDuration.Should().Be(TimeSpan.FromHours(8));
        var url = Uri.UnescapeDataString(http.Requests[0].Url.AbsoluteUri);
        url.Should().Contain("scopeId eq 'sp-1'").And.Contain("scopeType eq 'AppRole'").And.Contain("roleDefinitionId eq 'role-sec'");
    }

    [Fact]
    public void Scope_SurvivesTheStateFileInTheSwiftShape()
    {
        const string json = """{"appRole":{"servicePrincipalId":"sp-1","appRoleId":"role-sec"}}""";
        JsonSerializer.Serialize<RoleScope>(Scope).Should().Be(json);
        JsonSerializer.Deserialize<RoleScope>(json).Should().Be(Scope);
    }

    // MARK: Approvals

    private const string ApproverRequests = """
        {"value":[{"id":"req-9","approvalId":"appr-9","status":"PendingApproval","action":"selfActivate","principalId":"user-2",
          "appRoleId":"role-sec","servicePrincipalId":"sp-1","justification":"INC-9","createdDateTime":"2026-11-25T07:04:59Z",
          "scheduleInfo":{"startDateTime":"2026-11-25T07:05:00Z","expiration":{"type":"afterDuration","duration":"PT2H"}},
          "appRole":{"id":"role-sec","displayName":"Security Manager","value":"SecurityManager"},
          "servicePrincipal":{"id":"sp-1","displayName":"Insights API"},
          "principal":{"@odata.type":"#microsoft.graph.user","displayName":"Sam Chen"}}]}
        """;

    [Fact]
    public async Task PendingApprovals_NameTheRoleItsAppAndTheRequester()
    {
        var http = new StubHttpClient();
        var provider = new AppRoleApprovalProvider(http, new FakeTokenProvider());
        http.On("GET", "approver", ApproverRequests, 200);

        var items = await provider.PendingApprovalsAsync(TestIdentity, Tenant);

        items.Should().ContainSingle();
        items[0].Kind.Should().Be(RoleScopeKind.AppRole);
        items[0].TargetName.Should().Be("Security Manager");
        items[0].ScopeCaption.Should().Be("Insights API");
        items[0].RequesterName.Should().Be("Sam Chen");
        items[0].RequestedDuration.Should().Be(TimeSpan.FromHours(2));
        items[0].DecisionRef.Should().Be("appr-9");
        http.Requests[0].Url.AbsoluteUri.Should().Contain(
            "/beta/identityGovernance/privilegedAccess/appRole/assignmentScheduleRequests/filterByCurrentUser(on='approver')");
    }

    [Fact]
    public async Task Decide_ReadsTheStagesAndPatchesTheOneInProgress()
    {
        var http = new StubHttpClient();
        var provider = new AppRoleApprovalProvider(http, new FakeTokenProvider());
        http.On("GET", "assignmentApprovals/appr-9",
            """{"id":"appr-9","stages":[{"id":"s1","status":"Completed"},{"id":"s2","status":"InProgress","assignedToMe":true}]}""", 200);
        http.On("PATCH", "stages/s2", "", 204);
        var request = new ApprovalRequest("req-9", Tenant.Key, RoleScopeKind.AppRole, ApprovalAction.Activate, "Security Manager", "Sam Chen",
            decisionRef: "appr-9");

        await provider.DecideAsync(request, approve: true, "ok", TestIdentity);

        http.Requests.First(r => r.Method == "GET").Url.AbsoluteUri.Should().EndWith(
            "/beta/identityGovernance/privilegedAccess/appRole/assignmentApprovals/appr-9?$expand=stages");
        var patch = http.Requests.First(r => r.Method == "PATCH");
        patch.Url.AbsoluteUri.Should().EndWith("/assignmentApprovals/appr-9/stages/s2");
        var body = JsonNode.Parse(Encoding.UTF8.GetString(patch.Body!))!.AsObject();
        body["reviewResult"]!.GetValue<string>().Should().Be("Approve");
        body["justification"]!.GetValue<string>().Should().Be("ok");
    }
}
