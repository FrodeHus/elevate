using Elevate.Core.Models;
using Elevate.Core.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>ApprovalDiffTests</c>.</summary>
public class ApprovalDiffTests
{
    private static readonly TenantKey Tenant = new("id1", "t1");

    private static ApprovalRequest Request(string id) =>
        new(id, Tenant, RoleScopeKind.EntraDirectory, ApprovalAction.Activate, "Reader", "Ann");

    [Fact]
    public void ReturnsUnseenRequestsInInputOrder()
    {
        var current = new[] { Request("c"), Request("a"), Request("b") };
        var fresh = ApprovalDiff.NewRequests(new HashSet<string> { "a" }, current);
        fresh.Select(r => r.Id).Should().Equal("c", "b");
    }

    [Fact]
    public void ExcludesEverythingAlreadySeen()
    {
        var current = new[] { Request("a"), Request("b") };
        ApprovalDiff.NewRequests(new HashSet<string> { "a", "b" }, current).Should().BeEmpty();
    }

    [Fact]
    public void EmptyInputs()
    {
        ApprovalDiff.NewRequests(new HashSet<string>(), []).Should().BeEmpty();
        ApprovalDiff.NewRequests(new HashSet<string> { "x" }, []).Should().BeEmpty();
        ApprovalDiff.NewRequests(new HashSet<string>(), [Request("a")]).Select(r => r.Id).Should().Equal("a");
    }

    [Fact]
    public void ModelDefaultsAndRoundTrip()
    {
        var r = new ApprovalRequest("r1", Tenant, RoleScopeKind.Group, ApprovalAction.Extend, "Ops Admins", "Bo",
            scopeCaption: "member", justification: "need it", requestedDuration: TimeSpan.FromHours(1),
            createdAt: GraphJson.ParseDate("2026-09-04T09:00:00Z"), decisionRef: "appr-1");

        var json = Json.Serialize(r);
        Json.Deserialize<ApprovalRequest>(json).Should().Be(r);
        json.Should().Contain("\"kind\":\"group\"").And.Contain("\"action\":\"extend\"");

        var bare = new ApprovalRequest("r2", Tenant, RoleScopeKind.AzureResource, ApprovalAction.Other, "Contributor", "Cy");
        bare.ScopeCaption.Should().BeNull();
        bare.Justification.Should().BeNull();
        bare.RequestedDuration.Should().BeNull();
        bare.CreatedAt.Should().BeNull();
        bare.DecisionRef.Should().BeNull();
    }
}
