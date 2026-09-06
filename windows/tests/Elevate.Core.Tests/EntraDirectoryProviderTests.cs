using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Support;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>EntraDirectoryProviderTests</c>.</summary>
public class EntraDirectoryProviderTests
{
    private static readonly Identity TestIdentity = new("id1", "u@contoso.com", "U", "t-home");
    private static readonly TenantContext Tenant = new("id1", "t1", "Contoso", TenantSource.Home);

    private static (EntraDirectoryProvider Provider, StubHttpClient Http, FakeTokenProvider Tokens) MakeProvider()
    {
        var http = new StubHttpClient();
        var tokens = new FakeTokenProvider();
        return (new EntraDirectoryProvider(http, tokens), http, tokens);
    }

    private static EligibleRole GlobalReader => new(
        new RoleKey("id1", "t1", new EntraDirectoryScope("f2ef992c-3afb-46b9-b7cf-a126ee74c451", "/")),
        "Global Reader", RoleSource.Discovered, RolePolicy.ManualDefault);

    private static JsonObject BodyOf(Elevate.Core.Networking.HttpRequestData request) =>
        JsonNode.Parse(Encoding.UTF8.GetString(request.Body!))!.AsObject();

    [Fact]
    public async Task EligibleRoles_ListsRolesWithBearerTokenForTenant()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleEligibilitySchedules/filterByCurrentUser", body: Fixtures.Data("entra-eligible"));

        var roles = await provider.EligibleRolesAsync(TestIdentity, Tenant);

        roles.Select(r => r.DisplayName).Should().Equal("Global Reader", "User Administrator");
        roles.Should().OnlyContain(r => r.Source == RoleSource.Discovered && r.Key.TenantId == "t1" && r.Key.IdentityId == "id1");
        roles[0].Key.Scope.Should().Be(new EntraDirectoryScope("f2ef992c-3afb-46b9-b7cf-a126ee74c451", "/"));
        roles[0].ViaGroup.Should().BeNull();                 // Global Reader: memberType Direct
        roles[1].ViaGroup.Should().Be("group");              // User Administrator: memberType Group

        var request = http.Requests[0];
        request.Headers["Authorization"].Should().Be("Bearer token-t1");
        request.Url.AbsoluteUri.Should().Contain("expand=roleDefinition");
    }

    [Fact]
    public async Task ActiveAssignments_KeepsActivatedOnlyAndMergesPending()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleAssignmentScheduleInstances/filterByCurrentUser", body: Fixtures.Data("entra-active"));
        http.On("GET", "roleAssignmentScheduleRequests/filterByCurrentUser", body: Fixtures.Data("entra-pending-requests"));

        var active = await provider.ActiveAssignmentsAsync(TestIdentity, Tenant);

        active.Should().HaveCount(2);
        var globalReader = active.Single(a => a.RoleKey.Scope == new EntraDirectoryScope("f2ef992c-3afb-46b9-b7cf-a126ee74c451", "/"));
        globalReader.Status.Should().Be(AssignmentStatus.Active);
        globalReader.AssignmentId.Should().Be("inst-1");
        globalReader.EndDateTime.Should().Be(GraphJson.ParseDate("2026-09-04T16:00:00Z"));

        var userAdmin = active.Single(a => a.RoleKey.Scope == new EntraDirectoryScope("fe930be7-5e62-47db-91af-98c3a49a38b1", "/"));
        userAdmin.Status.Should().Be(AssignmentStatus.PendingApproval);
        userAdmin.AssignmentId.Should().Be("req-9");
    }

    [Fact]
    public async Task Forbidden_MapsToConsentRequired()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleEligibilitySchedules", """{"error":{"code":"Authorization_RequestDenied"}}""", 403);

        var act = () => provider.EligibleRolesAsync(TestIdentity, Tenant);

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.ConsentRequired);
    }

    [Fact]
    public async Task Unauthorized_WithClaims_MapsToClaimsChallenge()
    {
        var (provider, http, _) = MakeProvider();
        const string claims = """{"access_token":{"acrs":{"essential":true,"values":["c1"]}}}""";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(claims));
        http.On("GET", "roleEligibilitySchedules", 401,
            new Dictionary<string, string> { ["WWW-Authenticate"] = $"""Bearer error="insufficient_claims", claims="{b64}" """ });

        var act = () => provider.EligibleRolesAsync(TestIdentity, Tenant);

        var thrown = (await act.Should().ThrowAsync<PimException>()).Which;
        thrown.Kind.Should().Be(PimErrorKind.ClaimsChallenge);
        thrown.Detail.Should().Be(claims);
    }

    [Fact]
    public async Task SilentTokenFailure_PropagatesInteractionRequired()
    {
        var (provider, _, tokens) = MakeProvider();
        tokens.SilentError = new PimException(PimErrorKind.InteractionRequired);

        var act = () => provider.EligibleRolesAsync(TestIdentity, Tenant);

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.InteractionRequired);
    }

    [Fact]
    public async Task Policy_ReadsEndUserActivationRules()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleManagementPolicyAssignments", body: Fixtures.Data("entra-policy"));

        var policy = await provider.PolicyAsync(GlobalReader, TestIdentity);

        policy.MaximumDuration.Should().Be(TimeSpan.FromHours(4));
        policy.DefaultDuration.Should().Be(TimeSpan.FromHours(4));
        policy.RequiresJustification.Should().BeTrue();
        policy.RequiresMfa.Should().BeTrue();
        policy.RequiresTicket.Should().BeFalse();
        policy.RequiresApproval.Should().BeTrue();
        policy.AuthenticationContext.Should().Be("c1");

        var url = http.Requests[0].Url.AbsoluteUri;
        (url.Contains("roleDefinitionId%20eq%20'f2ef992c-3afb-46b9-b7cf-a126ee74c451'", StringComparison.Ordinal)
            || url.Contains("roleDefinitionId eq 'f2ef992c-3afb-46b9-b7cf-a126ee74c451'", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Activate_PostsSelfActivateAndComputesEnd()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "/me?", body: Fixtures.Data("me"));
        http.On("POST", "roleAssignmentScheduleRequests", 201, body: Fixtures.Data("entra-activate-response"));

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(GlobalReader.Key, TimeSpan.FromSeconds(7200), "Ticket 42"), TestIdentity);

        assignment.Status.Should().Be(AssignmentStatus.Active);
        assignment.AssignmentId.Should().Be("req-1");
        assignment.StartDateTime.Should().Be(GraphJson.ParseDate("2026-09-04T09:00:00Z"));
        assignment.EndDateTime.Should().Be(GraphJson.ParseDate("2026-09-04T11:00:00Z"));

        var body = BodyOf(http.RequestsMatching("roleAssignmentScheduleRequests")[0]);
        body["action"]!.GetValue<string>().Should().Be("selfActivate");
        body["principalId"]!.GetValue<string>().Should().Be("user-obj-1");
        body["roleDefinitionId"]!.GetValue<string>().Should().Be("f2ef992c-3afb-46b9-b7cf-a126ee74c451");
        body["directoryScopeId"]!.GetValue<string>().Should().Be("/");
        body["justification"]!.GetValue<string>().Should().Be("Ticket 42");
        var expiration = body["scheduleInfo"]!["expiration"]!;
        expiration["type"]!.GetValue<string>().Should().Be("afterDuration");
        expiration["duration"]!.GetValue<string>().Should().Be("PT2H");
        body.ContainsKey("ticketInfo").Should().BeFalse();
    }

    [Fact]
    public async Task Activate_WithFutureStart_SendsItAndReportsScheduled()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "/me?", body: Fixtures.Data("me"));
        http.On("POST", "roleAssignmentScheduleRequests", 201, body: Fixtures.Data("entra-activate-response"));
        var start = GraphJson.ParseDate("2099-01-01T09:00:00Z")!.Value;

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(GlobalReader.Key, TimeSpan.FromSeconds(7200), "later", StartDateTime: start), TestIdentity);

        var schedule = BodyOf(http.RequestsMatching("roleAssignmentScheduleRequests")[0])["scheduleInfo"]!;
        GraphJson.ParseDate(schedule["startDateTime"]!.GetValue<string>()).Should().Be(start);
        // The response echoes a start in the past; the request's future start wins.
        assignment.Status.Should().Be(AssignmentStatus.Scheduled);
        assignment.StartDateTime.Should().Be(start);
        assignment.EndDateTime.Should().Be(GraphJson.ParseDate("2099-01-01T11:00:00Z"));
    }

    [Fact]
    public async Task ActiveAssignments_FutureRequestsAppearAsScheduled()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleAssignmentScheduleInstances/filterByCurrentUser", """{"value":[]}""");
        http.On("GET", "roleAssignmentScheduleRequests/filterByCurrentUser", body: Fixtures.Data("entra-pending-requests"));

        var active = await provider.ActiveAssignmentsAsync(TestIdentity, Tenant);

        active.Should().HaveCount(2);                        // req-8 started this morning: not upcoming
        var globalReader = active.Single(a => a.RoleKey.Scope == new EntraDirectoryScope("f2ef992c-3afb-46b9-b7cf-a126ee74c451", "/"));
        globalReader.Status.Should().Be(AssignmentStatus.Scheduled);
        globalReader.AssignmentId.Should().Be("req-7");
        globalReader.StartDateTime.Should().Be(GraphJson.ParseDate("2099-01-01T09:00:00Z"));
        globalReader.EndDateTime.Should().Be(GraphJson.ParseDate("2099-01-01T11:00:00Z"));

        var url = Uri.UnescapeDataString(http.RequestsMatching("roleAssignmentScheduleRequests")[0].Url.AbsoluteUri);
        url.Should().Contain("status eq 'ScheduleCreated'").And.Contain("status eq 'Provisioned'");
        // The schedules list is no longer read at all.
        http.RequestsMatching("roleAssignmentSchedules/filterByCurrentUser").Should().BeEmpty();
    }

    [Fact]
    public async Task ActiveAssignments_FutureRequestDoesNotOverrideAnActiveAssignment()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "roleAssignmentScheduleInstances/filterByCurrentUser", body: Fixtures.Data("entra-active"));
        http.On("GET", "roleAssignmentScheduleRequests/filterByCurrentUser", body: Fixtures.Data("entra-pending-requests"));

        var active = await provider.ActiveAssignmentsAsync(TestIdentity, Tenant);

        active.Should().HaveCount(2);
        // req-7 is a future request for the same role: the live assignment still wins.
        var globalReader = active.Single(a => a.RoleKey.Scope == new EntraDirectoryScope("f2ef992c-3afb-46b9-b7cf-a126ee74c451", "/"));
        globalReader.Status.Should().Be(AssignmentStatus.Active);
        globalReader.AssignmentId.Should().Be("inst-1");
    }

    private static byte[] ActivateResponseWithStatus(string status)
    {
        var json = JsonNode.Parse(Fixtures.Text("entra-activate-response"))!.AsObject();
        json["status"] = status;
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    [Fact]
    public async Task Activate_FutureStartDoesNotMaskPendingApproval()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "/me?", body: Fixtures.Data("me"));
        http.On("POST", "roleAssignmentScheduleRequests", 201, body: ActivateResponseWithStatus("PendingApproval"));
        var start = GraphJson.ParseDate("2099-01-01T09:00:00Z")!.Value;

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(GlobalReader.Key, TimeSpan.FromSeconds(3600), "later", StartDateTime: start), TestIdentity);

        assignment.Status.Should().Be(AssignmentStatus.PendingApproval);
    }

    [Fact]
    public async Task Activate_FutureStartDoesNotMaskFailure()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "/me?", body: Fixtures.Data("me"));
        http.On("POST", "roleAssignmentScheduleRequests", 201, body: ActivateResponseWithStatus("Denied"));
        var start = GraphJson.ParseDate("2099-01-01T09:00:00Z")!.Value;

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(GlobalReader.Key, TimeSpan.FromSeconds(3600), "later", StartDateTime: start), TestIdentity);

        assignment.Status.Should().Be(AssignmentStatus.Failed("Denied"));
    }

    [Fact]
    public async Task Activate_ReportsPendingApproval()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "/me?", body: Fixtures.Data("me"));
        http.On("POST", "roleAssignmentScheduleRequests", 201, body: ActivateResponseWithStatus("PendingApproval"));

        var assignment = await provider.ActivateAsync(
            new ActivationRequest(GlobalReader.Key, TimeSpan.FromSeconds(3600), "x"), TestIdentity);

        assignment.Status.Should().Be(AssignmentStatus.PendingApproval);
        assignment.AssignmentId.Should().Be("req-1");
    }

    [Fact]
    public async Task Activate_PolicyFailureMapsToPolicyViolation()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "/me?", body: Fixtures.Data("me"));
        http.On("POST", "roleAssignmentScheduleRequests",
            """{"error":{"code":"RoleAssignmentRequestPolicyValidationFailed","message":"The following policy rules failed: [\"JustificationRule\"]"}}""",
            400);

        var act = () => provider.ActivateAsync(
            new ActivationRequest(GlobalReader.Key, TimeSpan.FromSeconds(3600), ""), TestIdentity);

        var thrown = (await act.Should().ThrowAsync<PimException>()).Which;
        thrown.Kind.Should().Be(PimErrorKind.PolicyViolation);
        thrown.Detail.Should().Be("""The following policy rules failed: ["JustificationRule"]""");
    }

    [Fact]
    public async Task Deactivate_PostsSelfDeactivate()
    {
        var (provider, http, _) = MakeProvider();
        http.On("GET", "/me?", body: Fixtures.Data("me"));
        http.On("POST", "roleAssignmentScheduleRequests", 201, body: Fixtures.Data("entra-activate-response"));
        var assignment = new ActiveAssignment(GlobalReader.Key, "inst-1", DateTimeOffset.UtcNow, null, AssignmentStatus.Active);

        await provider.DeactivateAsync(assignment, TestIdentity);

        var body = BodyOf(http.RequestsMatching("roleAssignmentScheduleRequests")[0]);
        body["action"]!.GetValue<string>().Should().Be("selfDeactivate");
        body.ContainsKey("scheduleInfo").Should().BeFalse();
    }

    [Fact]
    public async Task CancelPendingRequest_PostsToCancelEndpoint()
    {
        var (provider, http, _) = MakeProvider();
        http.On("POST", "roleAssignmentScheduleRequests/req-9/cancel", 204);
        var assignment = new ActiveAssignment(GlobalReader.Key, "req-9", DateTimeOffset.UtcNow, null, AssignmentStatus.PendingApproval);

        await provider.CancelPendingRequestAsync(assignment, TestIdentity);

        var post = http.RequestsMatching("/cancel")[0];
        post.Method.Should().Be("POST");
        post.Url.AbsoluteUri.Should().Be("https://graph.microsoft.com/v1.0/roleManagement/directory/roleAssignmentScheduleRequests/req-9/cancel");
        post.Headers["Authorization"].Should().Be("Bearer token-t1");
    }

    [Fact]
    public async Task CancelPendingRequest_WithoutIdThrowsNotEligible()
    {
        var (provider, _, _) = MakeProvider();
        var assignment = new ActiveAssignment(GlobalReader.Key, null, DateTimeOffset.UtcNow, null, AssignmentStatus.PendingApproval);

        var act = () => provider.CancelPendingRequestAsync(assignment, TestIdentity);

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.NotEligible);
    }
}

/// <summary>Port of the Swift <c>FirstPartyForbiddenTests</c>.</summary>
public class FirstPartyForbiddenTests
{
    [Fact]
    public async Task ForbiddenForFirstPartyIdentity_CarriesServerMessage()
    {
        var http = new StubHttpClient();
        http.On("GET", "roleEligibilitySchedules",
            """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""",
            403);
        var provider = new EntraDirectoryProvider(http, new FakeTokenProvider());
        var identity = new Identity("id1", "u@x", "U", "t1", SignInMethod.AzureCLI);
        var tenant = new TenantContext("id1", "t1", "T", TenantSource.Home);

        var act = () => provider.EligibleRolesAsync(identity, tenant);

        var thrown = (await act.Should().ThrowAsync<PimException>()).Which;
        thrown.Kind.Should().Be(PimErrorKind.Forbidden);
        thrown.Detail.Should().Be("Insufficient privileges to complete the operation.");

        new PimException(PimErrorKind.Forbidden, "x").UserMessage.Should().Be("Not permitted: x");
        new PimException(PimErrorKind.Unexpected, "boom", 500).UserMessage.Should().Be("Unexpected response (500): boom");
    }
}

/// <summary>Port of the Swift <c>FirstPartyScopeMessageTests</c>.</summary>
public class FirstPartyScopeMessageTests
{
    [Fact]
    public void PermissionScopeNotGranted_ExplainsTheLimitation()
    {
        const string body = """{"error":{"code":"Authorization_RequestDenied","message":"Authorization failed due to missing permission scope RoleAssignmentSchedule.ReadWrite.Directory,RoleManagement.ReadWrite.Directory.","innerError":{"errorCode":"PermissionScopeNotGranted"}}}""";

        var message = GraphTransport.FirstPartyForbiddenMessage(body, SignInMethod.AzureCLI);

        message.Should().StartWith("The Azure CLI app is not granted RoleAssignmentSchedule.ReadWrite.Directory in this tenant.");
        message.Should().Contain("try the Azure PowerShell app");
        GraphTransport.FirstPartyForbiddenMessage("""{"error":{"code":"x","message":"plain"}}""", SignInMethod.AzureCLI)
            .Should().Be("plain");
    }
}
