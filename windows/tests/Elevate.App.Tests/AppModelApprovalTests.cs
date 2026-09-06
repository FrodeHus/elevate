using Elevate.App.Tests.Support;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.App.Tests;

public class AppModelApprovalTests
{
    private const string EntraApprover = "directory/roleAssignmentScheduleRequests/filterByCurrentUser(on='approver')";

    private static StubHttpClient EmptyGraph()
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

    private static async Task<TestModel> OnlineModelAsync(StubHttpClient http)
    {
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity());
        return await TestModel.BootstrappedAsync(state, http: http, online: true, tokens: tokens);
    }

    [Fact]
    public async Task RefreshReadsPendingApprovalsAndNotifiesOncePerNewRequest()
    {
        var http = EmptyGraph();
        http.On("GET", EntraApprover, Fixtures.Text("entra-approver-requests"));
        using var test = await OnlineModelAsync(http);
        var model = test.Model;

        model.PendingApprovalCount.Should().Be(4);
        model.ApprovalsOrdered.Select(r => r.Id).Should().Equal("areq-1", "areq-2", "areq-3", "areq-4");
        model.ApprovalTenantName(model.ApprovalsOrdered[0]).Should().Be("Contoso");
        test.Notifier.Posted.Should().HaveCount(4);
        test.Notifier.Posted[0].Should().Be(("Approval requested", "Global Administrator for Ann Approver, Contoso"));
        test.Settings.SeenApprovalIds.Should().BeEquivalentTo(["areq-1", "areq-2", "areq-3", "areq-4"]);

        await model.RefreshAllAsync();

        test.Notifier.Posted.Should().HaveCount(4);

        // A request that is gone falls out of the seen set after a full sweep, so it notifies again if it returns.
        http.On("GET", EntraApprover, """{"value":[]}""");
        await model.RefreshAllAsync();
        model.PendingApprovalCount.Should().Be(0);
        test.Settings.SeenApprovalIds.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedApprovalReadKeepsThePreviousList()
    {
        var http = EmptyGraph();
        http.On("GET", EntraApprover, Fixtures.Text("entra-approver-requests"));
        using var test = await OnlineModelAsync(http);
        var model = test.Model;
        model.PendingApprovalCount.Should().Be(4);

        http.On("GET", EntraApprover, """{"error":{"code":"Authorization_RequestDenied"}}""", 403);
        await model.RefreshAllAsync();

        model.PendingApprovalCount.Should().Be(4);
        model.TenantErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task ApprovalsFilterWithThePanelSearchButTheCountDoesNot()
    {
        var http = EmptyGraph();
        http.On("GET", EntraApprover, Fixtures.Text("entra-approver-requests"));
        using var test = await OnlineModelAsync(http);
        var model = test.Model;

        model.SearchQuery = "global";

        model.ApprovalsOrdered.Select(r => r.Id).Should().Equal("areq-1");
        model.PendingApprovalCount.Should().Be(4);
        model.Approval("areq-3").Should().NotBeNull();
    }

    [Fact]
    public async Task DecideDropsTheRowRemembersTheJustificationAndReportsFailures()
    {
        var http = EmptyGraph();
        http.On("GET", EntraApprover, Fixtures.Text("entra-approver-requests"));
        http.On("GET", "/steps", Fixtures.Text("entra-approval-steps"));
        http.On("PATCH", "steps/step-1", 204);
        using var test = await OnlineModelAsync(http);
        var model = test.Model;

        http.On("PATCH", "steps/step-1", """{"error":{"code":"Bad","message":"nope"}}""", 400);
        var second = model.Approval("areq-2")!;
        (await model.DecideAsync(second, false, "no")).Should().BeFalse();

        model.ApprovalErrors[second.Id].Should().Contain("nope");
        model.Approval("areq-2").Should().NotBeNull();
        model.ErrorLog.Entries.Should().Contain(e => e.Message.Contains("nope", StringComparison.Ordinal));

        // Once the service accepts the decision the follow-up refresh no longer lists the request.
        http.On("PATCH", "steps/step-1", 204);
        http.On("GET", EntraApprover, """{"value":[]}""");
        var request = model.Approval("areq-1")!;
        (await model.DecideAsync(request, true, "fine")).Should().BeTrue();

        model.Approval("areq-1").Should().BeNull();
        model.ApprovalErrors.Should().NotContainKey("areq-1");
        model.DecisionInFlight.Should().BeEmpty();
        test.Settings.LastApprovalJustification.Should().Be("fine");
        http.Requests.Should().Contain(r => r.Method == "PATCH" && r.Url.AbsoluteUri.EndsWith("/steps/step-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DecideRefusesAnUnknownAccountOrKind()
    {
        using var test = await TestModel.BootstrappedAsync();
        var request = new ApprovalRequest("r", new TenantKey("ghost", "t"), RoleScopeKind.EntraDirectory, ApprovalAction.Activate, "Reader", "Ann");
        (await test.Model.DecideAsync(request, true, "x")).Should().BeFalse();
        test.Model.ApprovalErrors["r"].Should().Contain("no longer signed in");
    }

    [Fact]
    public async Task RemovingTheTenantOrAccountDropsItsApprovals()
    {
        var http = EmptyGraph();
        http.On("GET", EntraApprover, Fixtures.Text("entra-approver-requests"));
        using var test = await OnlineModelAsync(http);
        var model = test.Model;
        model.ApprovalErrors["areq-1"] = "old";

        model.RemoveTenant(Sample.TenantKey);

        model.PendingApprovalCount.Should().Be(0);
        model.ApprovalErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task CollapsedApprovalsIsRemembered()
    {
        using var test = await TestModel.BootstrappedAsync();
        test.Model.CollapsedApprovals.Should().BeFalse();
        test.Model.ToggleApprovals();
        test.Model.CollapsedApprovals.Should().BeTrue();
        new Elevate.App.Services.AppSettings(test.Directory).CollapsedApprovals.Should().BeTrue();
    }
}
