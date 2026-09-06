using System.Text;
using System.Text.Json;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Providers;
using Elevate.Core.Support;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>AzureApprovalProviderTests</c>.</summary>
public class AzureApprovalProviderTests
{
    private static readonly Identity TestIdentity = new("id1", "u@contoso.com", "U", "t-home");
    private static readonly TenantContext Tenant = new("id1", "t1", "Contoso", TenantSource.Home);

    private static (AzureApprovalProvider Provider, StubHttpClient Http) MakeProvider()
    {
        var http = new StubHttpClient();
        return (new AzureApprovalProvider(http, new FakeTokenProvider()), http);
    }

    private static ApprovalRequest Request(string decisionRef) =>
        new("areq-1", new TenantKey("id1", "t1"), RoleScopeKind.AzureResource, ApprovalAction.Activate, "Reader", "Ann",
            scopeCaption: "rg-ops · resource group", decisionRef: decisionRef);

    private static Dictionary<string, JsonElement> Body(HttpRequestData request) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Encoding.UTF8.GetString(request.Body!))!;

    [Fact]
    public async Task ListsOnlyPendingApprovalRequests()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "asApprover", body: Fixtures.Data("arm-approver-requests"));

        var items = await p.PendingApprovalsAsync(TestIdentity, Tenant);

        items.Select(i => i.Id).Should().Equal("areq-1", "areq-2", "areq-4", "areq-5");
        items.Select(i => i.Action).Should().Equal(ApprovalAction.Activate, ApprovalAction.Extend, ApprovalAction.Renew, ApprovalAction.Other);
        items.Select(i => i.RequesterName).Take(2).Should().Equal("Ann Approver", "user-obj-2");
        items.Select(i => i.ScopeCaption).Take(2).Should().Equal("rg-ops · resource group", "Contoso Production · subscription");
        items.Select(i => i.DecisionRef).Should().Equal("appr-1", "appr-2", "appr-4", "appr-5");
        items[0].TargetName.Should().Be("Reader");
        items[1].TargetName.Should().Be("/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c");
        items[0].RequestedDuration.Should().Be(TimeSpan.FromHours(4));
        items[0].Justification.Should().Be("Incident 4711");
        items[0].CreatedAt.Should().Be(GraphJson.ParseDate("2026-09-05T08:00:00Z"));
        items.Should().OnlyContain(i => i.Kind == RoleScopeKind.AzureResource && i.TenantKey == Tenant.Key);

        var url = Uri.UnescapeDataString(http.Requests[0].Url.AbsoluteUri);
        url.Should().Contain("providers/Microsoft.Authorization/roleAssignmentScheduleRequests");
        url.Should().Contain("$filter=asApprover()");
        url.Should().Contain("api-version=2020-10-01");
    }

    [Fact]
    public async Task ListForbiddenThrows()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "asApprover", """{"error":{"code":"AuthorizationFailed","message":"nope"}}""", 403);

        var act = () => p.PendingApprovalsAsync(TestIdentity, Tenant);

        var error = (await act.Should().ThrowAsync<PimException>()).Which;
        error.Kind.Should().Be(PimErrorKind.PolicyViolation);
        error.UserMessage.Should().Be("Not permitted at this scope");
    }

    [Fact]
    public async Task DecidePatchesTheStageAwaitingReview()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "roleAssignmentApprovals/appr-1?", body: Fixtures.Data("arm-approval"));
        http.On("PATCH", "stages/stage-1", 200);

        await p.DecideAsync(Request("appr-1"), true, "ok", TestIdentity);

        var getUrl = http.Requests[0].Url.AbsoluteUri;
        getUrl.Should().Contain("https://management.azure.com/providers/Microsoft.Authorization/roleAssignmentApprovals/appr-1?");
        getUrl.Should().Contain("api-version=2021-01-01-preview");
        var patch = http.Requests[^1];
        patch.Method.Should().Be("PATCH");
        patch.Url.AbsoluteUri.Should().Contain("roleAssignmentApprovals/appr-1/stages/stage-1?");
        patch.Url.AbsoluteUri.Should().Contain("api-version=2021-01-01-preview");
        var body = Body(patch);
        body["reviewResult"].GetString().Should().Be("Approve");
        body["justification"].GetString().Should().Be("ok");
    }

    [Fact]
    public async Task DecideRetriesWithThePropertiesWrappedBodyOnA400()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "roleAssignmentApprovals/appr-1?", body: Fixtures.Data("arm-approval"));
        var attempts = 0;
        http.On("PATCH", "stages/stage-1", _ => new HttpResponseData(
            Interlocked.Increment(ref attempts) == 1 ? 400 : 200,
            new Dictionary<string, string>(),
            Encoding.UTF8.GetBytes("""{"error":{"code":"BadRequest","message":"bad body"}}""")));

        await p.DecideAsync(Request("appr-1"), false, "no", TestIdentity);

        var patches = http.Requests.Where(r => r.Method == "PATCH").ToList();
        patches.Should().HaveCount(2);
        Body(patches[0])["reviewResult"].GetString().Should().Be("Deny");
        var props = Body(patches[1])["properties"];
        props.GetProperty("reviewResult").GetString().Should().Be("Deny");
        props.GetProperty("justification").GetString().Should().Be("no");
    }

    [Fact]
    public async Task DecideSurfacesTheErrorWhenTheRetryAlsoFails()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "roleAssignmentApprovals/appr-1?", body: Fixtures.Data("arm-approval"));
        http.On("PATCH", "stages/stage-1", """{"error":{"code":"BadRequest","message":"bad body"}}""", 400);

        var act = () => p.DecideAsync(Request("appr-1"), true, "ok", TestIdentity);

        var error = (await act.Should().ThrowAsync<PimException>()).Which;
        error.Kind.Should().Be(PimErrorKind.Unexpected);
        error.Status.Should().Be(400);
        error.Detail.Should().Be("bad body");
        http.Requests.Count(r => r.Method == "PATCH").Should().Be(2);
    }

    [Fact]
    public async Task DecideWithNoStagesThrows()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "roleAssignmentApprovals/appr-1?", """{"properties":{"stages":[]}}""");

        var act = () => p.DecideAsync(Request("appr-1"), true, "ok", TestIdentity);

        var error = (await act.Should().ThrowAsync<PimException>()).Which;
        error.Kind.Should().Be(PimErrorKind.Unexpected);
        error.Status.Should().Be(0);
        error.Detail.Should().Be("No approval step to decide");
    }

    [Fact]
    public async Task DecideFallsBackToTheFirstStage()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "roleAssignmentApprovals/appr-1?",
            """{"properties":{"stages":[{"name":"stage-1","properties":{"reviewResult":"Approve","status":"Completed"}}]}}""");
        http.On("PATCH", "stages/stage-1", 200);

        await p.DecideAsync(Request("appr-1"), true, "ok", TestIdentity);

        http.Requests[^1].Url.AbsoluteUri.Should().Contain("stages/stage-1");
    }
}
