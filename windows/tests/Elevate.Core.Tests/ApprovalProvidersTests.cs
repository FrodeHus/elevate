using System.Text;
using System.Text.Json;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Support;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>GraphApprovalsActionMappingTests</c>.</summary>
public class GraphApprovalsActionMappingTests
{
    [Fact]
    public void SelfRenewMapsToRenewAndUnknownMapsToOther()
    {
        GraphApprovals.Action("selfRenew").Should().Be(ApprovalAction.Renew);
        GraphApprovals.Action("SelfRenew").Should().Be(ApprovalAction.Renew);
        GraphApprovals.Action("somethingUnknown").Should().Be(ApprovalAction.Other);
        GraphApprovals.Action(null).Should().Be(ApprovalAction.Other);
    }
}

internal static class ApprovalTestSupport
{
    public static readonly Identity Identity = new("id1", "u@contoso.com", "U", "t-home");
    public static readonly TenantContext Tenant = new("id1", "t1", "Contoso", TenantSource.Home);

    public static Dictionary<string, string> Body(Networking.HttpRequestData request) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(request.Body!))!;
}

/// <summary>Port of the Swift <c>EntraApprovalProviderTests</c>.</summary>
public class EntraApprovalProviderTests
{
    private static (EntraApprovalProvider Provider, StubHttpClient Http) MakeProvider()
    {
        var http = new StubHttpClient();
        return (new EntraApprovalProvider(http, new FakeTokenProvider()), http);
    }

    private static ApprovalRequest Request(string id, string? decisionRef = null) =>
        new(id, new TenantKey("id1", "t1"), RoleScopeKind.EntraDirectory, ApprovalAction.Activate,
            "Global Administrator", "Ann", decisionRef: decisionRef ?? id);

    [Fact]
    public async Task ListsPendingApprovalsWithActionTargetAndRequester()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "approver", body: Fixtures.Data("entra-approver-requests"));

        var items = await p.PendingApprovalsAsync(ApprovalTestSupport.Identity, ApprovalTestSupport.Tenant);

        items.Select(i => i.Id).Should().Equal("areq-1", "areq-2", "areq-3", "areq-4");
        items.Select(i => i.Action).Should().Equal(ApprovalAction.Activate, ApprovalAction.Extend, ApprovalAction.Renew, ApprovalAction.Other);
        items.Select(i => i.TargetName).Should().Equal("Global Administrator", "def-role-2", "def-role-3", "def-role-4");
        items.Select(i => i.RequesterName).Take(2).Should().Equal("Ann Approver", "user-obj-2");
        items[0].RequestedDuration.Should().Be(TimeSpan.FromHours(4));
        items[0].Justification.Should().Be("Incident 4711");
        items[0].CreatedAt.Should().Be(Fixtures.Date("2026-09-05T08:00:00Z"));
        items[0].DecisionRef.Should().Be("areq-1");
        items.Should().OnlyContain(i => i.Kind == RoleScopeKind.EntraDirectory && i.TenantKey == ApprovalTestSupport.Tenant.Key);

        var url = Uri.UnescapeDataString(http.Requests[0].Url.AbsoluteUri);
        url.Should().Contain("/v1.0/roleManagement/directory/roleAssignmentScheduleRequests/filterByCurrentUser(on='approver')");
        url.Should().Contain("status eq 'PendingApproval'");
        url.Should().Contain("$expand=roleDefinition,principal");
    }

    [Fact]
    public async Task ListForbiddenThrows()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "approver", """{"error":{"code":"Authorization_RequestDenied","message":"nope"}}""", 403);

        var act = () => p.PendingApprovalsAsync(ApprovalTestSupport.Identity, ApprovalTestSupport.Tenant);

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.ConsentRequired);
    }

    [Fact]
    public async Task DecideApprovesTheInProgressStepOnBeta()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "/steps", body: Fixtures.Data("entra-approval-steps"));
        http.On("PATCH", "steps/step-1", 204);

        await p.DecideAsync(Request("areq-1"), true, "ok", ApprovalTestSupport.Identity);

        http.Requests[0].Url.AbsoluteUri.Should().Be("https://graph.microsoft.com/beta/roleManagement/directory/roleAssignmentApprovals/areq-1/steps");
        var patch = http.Requests[^1];
        patch.Method.Should().Be("PATCH");
        patch.Url.AbsoluteUri.Should().Be("https://graph.microsoft.com/beta/roleManagement/directory/roleAssignmentApprovals/areq-1/steps/step-1");
        ApprovalTestSupport.Body(patch).Should().Equal(new Dictionary<string, string> { ["reviewResult"] = "Approve", ["justification"] = "ok" });
    }

    [Fact]
    public async Task DecideDeniesUsingTheDecisionRef()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "/steps", body: Fixtures.Data("entra-approval-steps"));
        http.On("PATCH", "steps/step-1", 204);

        await p.DecideAsync(Request("areq-1", decisionRef: "appr-9"), false, "no", ApprovalTestSupport.Identity);

        var patch = http.Requests[^1];
        patch.Url.AbsoluteUri.Should().Contain("roleAssignmentApprovals/appr-9/steps/step-1");
        ApprovalTestSupport.Body(patch).Should().Equal(new Dictionary<string, string> { ["reviewResult"] = "Deny", ["justification"] = "no" });
    }

    [Fact]
    public async Task DecideWithNoStepsThrows()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "/steps", """{"value":[]}""");

        var act = () => p.DecideAsync(Request("areq-1"), true, "ok", ApprovalTestSupport.Identity);

        var error = (await act.Should().ThrowAsync<PimException>()).Which;
        error.Kind.Should().Be(PimErrorKind.Unexpected);
        error.Status.Should().Be(0);
        error.Detail.Should().Be("No approval step to decide");
    }

    [Fact]
    public async Task DecideFallsBackToTheStepAssignedToMe()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "/steps",
            """{"value":[{"id":"step-0","status":"Completed","assignedToMe":false},{"id":"step-1","status":"NotStarted","assignedToMe":true}]}""");
        http.On("PATCH", "steps/step-1", 204);

        await p.DecideAsync(Request("areq-1"), true, "ok", ApprovalTestSupport.Identity);

        http.Requests[^1].Url.AbsoluteUri.Should().EndWith("/steps/step-1");
    }
}

/// <summary>Port of the Swift <c>GroupApprovalProviderTests</c>.</summary>
public class GroupApprovalProviderTests
{
    private static (GroupApprovalProvider Provider, StubHttpClient Http) MakeProvider()
    {
        var http = new StubHttpClient();
        return (new GroupApprovalProvider(http, new FakeTokenProvider()), http);
    }

    private static ApprovalRequest Request(string id) =>
        new(id, new TenantKey("id1", "t1"), RoleScopeKind.Group, ApprovalAction.Activate, "Ops Admins", "Ann",
            scopeCaption: "member", decisionRef: id);

    [Fact]
    public async Task ListsPendingApprovalsWithAccessCaption()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "approver", body: Fixtures.Data("group-approver-requests"));

        var items = await p.PendingApprovalsAsync(ApprovalTestSupport.Identity, ApprovalTestSupport.Tenant);

        items.Select(i => i.Id).Should().Equal("gareq-1", "gareq-2");
        items.Select(i => i.Action).Should().Equal(ApprovalAction.Activate, ApprovalAction.Extend);
        items.Select(i => i.TargetName).Should().Equal("Ops Admins", "grp-sec");
        items.Select(i => i.RequesterName).Should().Equal("Ann Approver", "user-obj-2");
        items.Select(i => i.ScopeCaption).Should().Equal("member", "owner");
        items[0].RequestedDuration.Should().Be(TimeSpan.FromHours(4));
        items[0].DecisionRef.Should().Be("gareq-1");
        items.Should().OnlyContain(i => i.Kind == RoleScopeKind.Group && i.TenantKey == ApprovalTestSupport.Tenant.Key);

        var url = Uri.UnescapeDataString(http.Requests[0].Url.AbsoluteUri);
        url.Should().Contain("/v1.0/identityGovernance/privilegedAccess/group/assignmentScheduleRequests/filterByCurrentUser(on='approver')");
        url.Should().Contain("status eq 'PendingApproval'");
        url.Should().Contain("$expand=group,principal");
    }

    [Fact]
    public async Task ListForbiddenThrows()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "approver", """{"error":{"code":"Authorization_RequestDenied","message":"nope"}}""", 403);

        var act = () => p.PendingApprovalsAsync(ApprovalTestSupport.Identity, ApprovalTestSupport.Tenant);

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.ConsentRequired);
    }

    [Fact]
    public async Task DecidePatchesTheStepOnBeta()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "/steps", body: Fixtures.Data("group-approval-steps"));
        http.On("PATCH", "steps/step-1", 204);

        await p.DecideAsync(Request("gareq-1"), false, "no", ApprovalTestSupport.Identity);

        http.Requests[0].Url.AbsoluteUri.Should().Be("https://graph.microsoft.com/beta/identityGovernance/privilegedAccess/group/assignmentApprovals/gareq-1/steps");
        var patch = http.Requests[^1];
        patch.Method.Should().Be("PATCH");
        patch.Url.AbsoluteUri.Should().EndWith("/assignmentApprovals/gareq-1/steps/step-1");
        ApprovalTestSupport.Body(patch).Should().Equal(new Dictionary<string, string> { ["reviewResult"] = "Deny", ["justification"] = "no" });
    }
}
