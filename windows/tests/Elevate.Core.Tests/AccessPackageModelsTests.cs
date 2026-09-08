using Elevate.Core.Models;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>AccessPackageModelsTests</c>.</summary>
public class AccessPackageModelsTests
{
    [Fact]
    public void RequestStateParsesCaseInsensitivelyAndFallsBackToUnknown()
    {
        AccessPackageRequestStates.Parse("PendingApproval").Should().Be(AccessPackageRequestState.PendingApproval);
        AccessPackageRequestStates.Parse("delivered").Should().Be(AccessPackageRequestState.Delivered);
        AccessPackageRequestStates.Parse("partiallyDelivered").Should().Be(AccessPackageRequestState.PartiallyDelivered);
        AccessPackageRequestStates.Parse("somethingNew").Should().Be(AccessPackageRequestState.Unknown);
        AccessPackageRequestStates.Parse("3").Should().Be(AccessPackageRequestState.Unknown);
        AccessPackageRequestStates.Parse(null).Should().Be(AccessPackageRequestState.Unknown);
    }

    [Fact]
    public void RequestStateGroupsIntoTabs()
    {
        AccessPackageRequestState.PendingApproval.IsOpen().Should().BeTrue();
        AccessPackageRequestState.Scheduled.IsOpen().Should().BeTrue();
        AccessPackageRequestState.Delivered.IsOpen().Should().BeFalse();
        AccessPackageRequestState.Denied.IsDeclined().Should().BeTrue();
        AccessPackageRequestState.Canceled.IsDeclined().Should().BeTrue();
        AccessPackageRequestState.DeliveryFailed.IsDeclined().Should().BeTrue();
        AccessPackageRequestState.Submitted.IsDeclined().Should().BeFalse();
        AccessPackageRequestState.Submitted.IsCancellable().Should().BeTrue();
        AccessPackageRequestState.PendingApproval.IsCancellable().Should().BeTrue();
        AccessPackageRequestState.Delivering.IsCancellable().Should().BeFalse();
    }

    [Fact]
    public void AssignmentStateParses()
    {
        AccessPackageAssignmentStates.Parse("Delivered").Should().Be(AccessPackageAssignmentState.Delivered);
        AccessPackageAssignmentStates.Parse("expired").Should().Be(AccessPackageAssignmentState.Expired);
        AccessPackageAssignmentStates.Parse("nope").Should().Be(AccessPackageAssignmentState.Unknown);
        AccessPackageAssignmentStates.Parse(null).Should().Be(AccessPackageAssignmentState.Unknown);
    }

    [Fact]
    public void ModelsRoundTripThroughJsonWithSwiftKeys()
    {
        var request = new AccessPackageRequest("r", "p", "Pkg", "userAdd", AccessPackageRequestState.Denied, "Denied", "why",
            Fixtures.Date("2026-09-02T09:00:00Z"), Fixtures.Date("2026-09-02T09:03:00Z"), "pol");
        var assignment = new AccessPackageAssignment("a", "p", "Pkg", AccessPackageAssignmentState.Delivered, "All", Fixtures.Date("2027-01-01T00:00:00Z"));
        var snapshot = new AccessPackageSnapshot([request], [assignment]);

        var json = Json.Serialize(snapshot);
        json.Should().Contain("\"state\":\"denied\"").And.Contain("\"createdAt\":\"2026-09-02T09:00:00Z\"")
            .And.Contain("\"packageName\":\"Pkg\"").And.Contain("\"expiresAt\":\"2027-01-01T00:00:00Z\"");
        Json.Deserialize<AccessPackageSnapshot>(json).Should().Be(snapshot);

        var requirement = new PolicyRequirement("pol", "Engineers", null, true, false);
        Json.Deserialize<PolicyRequirement>(Json.Serialize(requirement)).Should().Be(requirement);
    }

    [Fact]
    public void SnapshotDefaultsToEmptyListsAndComparesByContent()
    {
        new AccessPackageSnapshot().Requests.Should().BeEmpty();
        new AccessPackageSnapshot().Assignments.Should().BeEmpty();
        new AccessPackageSnapshot().Should().Be(new AccessPackageSnapshot([], []));
        Json.Deserialize<AccessPackageSnapshot>("""{"requests":[],"assignments":[]}""").Should().Be(new AccessPackageSnapshot());
        new AccessPackageSnapshot([new AccessPackageRequest("r", "p", "Pkg", "userAdd", AccessPackageRequestState.Submitted)])
            .Should().NotBe(new AccessPackageSnapshot());
    }
}
