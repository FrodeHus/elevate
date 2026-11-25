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
}
