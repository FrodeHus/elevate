using System.Text;
using System.Text.Json.Nodes;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>AccessPackageProviderTests</c>.</summary>
public class AccessPackageProviderTests
{
    private static readonly Identity TestIdentity = new("id1", "alex.rivera@contoso.com", "Alex", "t1");

    private static (AccessPackageProvider Provider, StubHttpClient Http) MakeProvider()
    {
        var http = new StubHttpClient();
        return (new AccessPackageProvider(http, new FakeTokenProvider()), http);
    }

    [Fact]
    public async Task ListsRequestablePackagesAcrossPages()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "accessPackages/filterByCurrentUser", body: Fixtures.Data("ap-packages"));
        http.On("GET", "skiptoken=page2", body: Fixtures.Data("ap-packages-page2"));

        var packages = await p.RequestablePackagesAsync(TestIdentity, "t1");

        packages.Select(x => x.Id).Should().Equal("pkg-finance", "pkg-exchange", "pkg-sandbox", "pkg-hidden");
        packages[3].IsHidden.Should().BeTrue();
        packages[3].Description.Should().BeNull();
        var first = http.Requests[0];
        first.Headers["Authorization"].Should().Be("Bearer token-t1");
        first.Url.AbsoluteUri.Should().Contain("identityGovernance/entitlementManagement/accessPackages/filterByCurrentUser(on='allowedRequestor')");
        first.Url.AbsoluteUri.Should().StartWith("https://graph.microsoft.com/v1.0/");
    }

    [Fact]
    public async Task ListsMyRequestsWithPackageNamesAndStates()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "assignmentRequests/filterByCurrentUser", body: Fixtures.Data("ap-requests"));

        var requests = await p.MyRequestsAsync(TestIdentity, "t1");

        requests.Select(r => r.Id).Should().Equal("req-1", "req-2", "req-3", "req-4");
        requests[0].State.Should().Be(AccessPackageRequestState.PendingApproval);
        requests[0].PackageName.Should().Be("Azure Sandbox Contributor");
        requests[0].PackageId.Should().Be("pkg-sandbox");
        requests[0].PolicyId.Should().Be("pol-eng");
        requests[0].Justification.Should().Be("Need a sandbox for the cost-alerting spike (INC-4412).");
        requests[1].State.Should().Be(AccessPackageRequestState.Delivered);
        requests[1].CompletedAt.Should().Be(Fixtures.Date("2026-09-07T14:40:00Z"));
        requests[2].State.Should().Be(AccessPackageRequestState.Denied);
        // Unknown state and a missing package still decode; the name falls back to the id.
        requests[3].State.Should().Be(AccessPackageRequestState.Unknown);
        requests[3].PackageName.Should().Be("req-4");
        requests[3].PackageId.Should().BeEmpty();
        var url = http.Requests[0].Url.AbsoluteUri;
        url.Should().Contain("assignmentRequests/filterByCurrentUser(on='target')").And.Contain("expand=accessPackage,assignment");
    }

    [Fact]
    public async Task ListsMyAssignmentsWithExpiryAndPolicy()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "assignments/filterByCurrentUser", body: Fixtures.Data("ap-assignments"));

        var assignments = await p.MyAssignmentsAsync(TestIdentity, "t1");

        assignments.Select(a => a.Id).Should().Equal("asg-1", "asg-2", "asg-3");
        assignments[0].State.Should().Be(AccessPackageAssignmentState.Delivered);
        assignments[0].ExpiresAt.Should().Be(Fixtures.Date("2027-03-07T14:40:00Z"));
        assignments[0].PolicyName.Should().Be("On-call staff");
        assignments[1].ExpiresAt.Should().BeNull();
        assignments[2].State.Should().Be(AccessPackageAssignmentState.Expired);
        assignments[2].PolicyName.Should().BeNull();
        var url = http.Requests[0].Url.AbsoluteUri;
        url.Should().Contain("assignments/filterByCurrentUser(on='target')").And.Contain("expand=accessPackage,assignmentPolicy");
    }

    [Fact]
    public async Task ForbiddenMapsToConsentRequiredForOwnApp()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "accessPackages/filterByCurrentUser", """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""", status: 403);

        var act = () => p.RequestablePackagesAsync(TestIdentity, "t1");

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.ConsentRequired);
    }

    [Fact]
    public async Task ForbiddenIsAPlainRefusalForAFirstPartyApp()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "accessPackages/filterByCurrentUser", """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""", status: 403);

        var act = () => p.RequestablePackagesAsync(TestIdentity with { SignInMethod = SignInMethod.AzureCLI }, "t1");

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.Forbidden);
    }

    [Fact]
    public async Task ForbiddenIsAPlainRefusalForACustomApp()
    {
        // The consent link is built for Elevate's own client id, so it cannot help a custom registration.
        var (p, http) = MakeProvider();
        http.On("GET", "accessPackages/filterByCurrentUser", """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""", status: 403);

        var act = () => p.RequestablePackagesAsync(TestIdentity with { SignInMethod = SignInMethod.Custom("11111111-2222-3333-4444-555555555555") }, "t1");

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.Forbidden);
    }

    [Fact]
    public async Task RequirementsWithOnePolicy()
    {
        var (p, http) = MakeProvider();
        http.On("POST", "getApplicablePolicyRequirements", body: Fixtures.Data("ap-requirements-one"));

        var reqs = await p.RequirementsAsync("pkg-sandbox", TestIdentity, "t1");

        reqs.Should().ContainSingle();
        reqs[0].Id.Should().Be("pol-eng");
        reqs[0].DisplayName.Should().Be("Engineers");
        reqs[0].Description.Should().Be("30 days, approval by the platform team.");
        reqs[0].IsApprovalRequired.Should().BeTrue();
        reqs[0].RequiresAnswers.Should().BeFalse();
        var sent = http.Requests[0];
        sent.Method.Should().Be("POST");
        sent.Url.AbsoluteUri.Should().EndWith("/entitlementManagement/accessPackages/pkg-sandbox/getApplicablePolicyRequirements");
    }

    [Fact]
    public async Task RequirementsWithTwoPoliciesAndQuestions()
    {
        var (p, http) = MakeProvider();
        http.On("POST", "pkg-two/getApplicablePolicyRequirements", body: Fixtures.Data("ap-requirements-two"));
        http.On("POST", "pkg-q/getApplicablePolicyRequirements", body: Fixtures.Data("ap-requirements-questions"));

        var two = await p.RequirementsAsync("pkg-two", TestIdentity, "t1");
        two.Select(r => r.Id).Should().Equal("pol-eng", "pol-lead");
        two[1].IsApprovalRequired.Should().BeFalse();

        var q = await p.RequirementsAsync("pkg-q", TestIdentity, "t1");
        q.Should().ContainSingle().Which.RequiresAnswers.Should().BeTrue();
    }

    [Fact]
    public async Task RequestPostsUserAddBodyWithOptionalPolicy()
    {
        var (p, http) = MakeProvider();
        http.On("POST", "assignmentRequests", status: 201, body: Fixtures.Data("ap-request-created"));

        var created = await p.RequestAsync("pkg-sandbox", "pol-eng", "Need it", TestIdentity, "t1");

        created.Id.Should().Be("req-new");
        created.State.Should().Be(AccessPackageRequestState.Submitted);
        created.PackageId.Should().Be("pkg-sandbox");
        created.PolicyId.Should().Be("pol-eng");
        var body = JsonNode.Parse(Encoding.UTF8.GetString(http.Requests[0].Body!))!.AsObject();
        body["requestType"]!.GetValue<string>().Should().Be("userAdd");
        body["justification"]!.GetValue<string>().Should().Be("Need it");
        var assignment = body["assignment"]!.AsObject();
        assignment["accessPackageId"]!.GetValue<string>().Should().Be("pkg-sandbox");
        assignment["assignmentPolicyId"]!.GetValue<string>().Should().Be("pol-eng");

        _ = await p.RequestAsync("pkg-sandbox", null, "Again", TestIdentity, "t1");
        var second = JsonNode.Parse(Encoding.UTF8.GetString(http.Requests[^1].Body!))!.AsObject();
        second["assignment"]!.AsObject().ContainsKey("assignmentPolicyId").Should().BeFalse();
    }

    [Fact]
    public async Task CancelPostsToTheCancelAction()
    {
        var (p, http) = MakeProvider();
        http.On("POST", "assignmentRequests/req-1/cancel", status: 204);

        await p.CancelAsync("req-1", TestIdentity, "t1");

        var sent = http.Requests[0];
        sent.Method.Should().Be("POST");
        sent.Url.AbsoluteUri.Should().EndWith("/assignmentRequests/req-1/cancel");
    }

    [Fact]
    public void MyAccessUrlPointsAtThePackageInTheTenant()
    {
        AccessPackageProvider.MyAccessUrl("11111111-2222-3333-4444-555555555555", "pkg-sandbox").ToString()
            .Should().Be("https://myaccess.microsoft.com/@11111111-2222-3333-4444-555555555555#/access-packages/pkg-sandbox");
    }
}
