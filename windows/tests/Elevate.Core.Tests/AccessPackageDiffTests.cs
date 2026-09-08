using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>AccessPackageDiffTests</c>.</summary>
public class AccessPackageDiffTests
{
    private static readonly DateTimeOffset Now = Fixtures.Date("2026-09-08T12:00:00Z")!.Value;

    private static AccessPackageRequest Request(string id, AccessPackageRequestState state) =>
        new(id, $"p-{id}", $"Package {id}", "userAdd", state);

    private static AccessPackageAssignment Assignment(string id, AccessPackageAssignmentState state = AccessPackageAssignmentState.Delivered, string? expires = null) =>
        new(id, $"p-{id}", $"Package {id}", state, ExpiresAt: expires is null ? null : Fixtures.Date(expires));

    [Fact]
    public void NullPreviousIsBaselineAndYieldsNothing()
    {
        var current = new AccessPackageSnapshot([Request("r", AccessPackageRequestState.Delivered)], [Assignment("a")]);
        AccessPackageDiff.Events(null, current, Now).Should().BeEmpty();
    }

    [Fact]
    public void UnchangedYieldsNothing()
    {
        var s = new AccessPackageSnapshot([Request("r", AccessPackageRequestState.PendingApproval)], [Assignment("a")]);
        AccessPackageDiff.Events(s, s, Now).Should().BeEmpty();
    }

    [Fact]
    public void RequestTransitionsProduceEvents()
    {
        var before = new AccessPackageSnapshot([
            Request("a", AccessPackageRequestState.PendingApproval), Request("b", AccessPackageRequestState.Submitted),
            Request("c", AccessPackageRequestState.Delivering), Request("d", AccessPackageRequestState.PendingApproval)]);
        var after = new AccessPackageSnapshot([
            Request("a", AccessPackageRequestState.Delivered), Request("b", AccessPackageRequestState.Denied),
            Request("c", AccessPackageRequestState.DeliveryFailed), Request("d", AccessPackageRequestState.Canceled)]);

        var events = AccessPackageDiff.Events(before, after, Now);

        events.Should().Equal(
            new AccessPackageEvent.Approved(Request("a", AccessPackageRequestState.Delivered)),
            new AccessPackageEvent.Denied(Request("b", AccessPackageRequestState.Denied)),
            new AccessPackageEvent.DeliveryFailed(Request("c", AccessPackageRequestState.DeliveryFailed)));
    }

    [Fact]
    public void ARequestFirstSeenAlreadyDeliveredDoesNotNotify()
    {
        var before = new AccessPackageSnapshot();
        var after = new AccessPackageSnapshot([Request("a", AccessPackageRequestState.Delivered)]);
        AccessPackageDiff.Events(before, after, Now).Should().BeEmpty();
    }

    [Fact]
    public void RevokedBeforeExpiryAndExpiredAtExpiry()
    {
        var before = new AccessPackageSnapshot(assignments: [
            Assignment("keep", expires: "2027-01-01T00:00:00Z"),
            Assignment("gone-early", expires: "2027-01-01T00:00:00Z"),
            Assignment("gone-late", expires: "2026-09-08T11:00:00Z"),
            Assignment("marked", expires: "2026-09-01T00:00:00Z"),
            Assignment("open-ended")]);
        var after = new AccessPackageSnapshot(assignments: [
            Assignment("keep", expires: "2027-01-01T00:00:00Z"),
            Assignment("marked", AccessPackageAssignmentState.Expired, "2026-09-01T00:00:00Z")]);

        var events = AccessPackageDiff.Events(before, after, Now);

        events.Should().HaveCount(4);
        events.Should().Contain(new AccessPackageEvent.Revoked(before.Assignments[1]));
        events.Should().Contain(new AccessPackageEvent.Expired(before.Assignments[2]));
        events.Should().Contain(new AccessPackageEvent.Expired(before.Assignments[3]));
        events.Should().Contain(new AccessPackageEvent.Revoked(before.Assignments[4]));
    }

    [Fact]
    public void GraphsExpiredStateWinsOverTheClock()
    {
        // The service already says expired, but this clock has not reached the end yet (skew, or an early poll).
        var before = new AccessPackageSnapshot(assignments: [Assignment("a", expires: "2026-09-08T12:00:05Z")]);
        var after = new AccessPackageSnapshot(assignments: [Assignment("a", AccessPackageAssignmentState.Expired, "2026-09-08T12:00:05Z")]);

        AccessPackageDiff.Events(before, after, Now).Should().Equal(new AccessPackageEvent.Expired(before.Assignments[0]));
    }

    [Fact]
    public void EventsCarryTheirPackageName()
    {
        new AccessPackageEvent.Approved(Request("x", AccessPackageRequestState.Delivered)).PackageName.Should().Be("Package x");
        new AccessPackageEvent.Expired(Assignment("y")).PackageName.Should().Be("Package y");
    }
}
