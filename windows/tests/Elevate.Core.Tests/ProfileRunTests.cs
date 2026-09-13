using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class ProfileRunTests
{
    private static ActiveAssignment Assignment() => new(new RoleKey("id", "tenant", new EntraDirectoryScope("role", "/")),
        "assignment", DateTimeOffset.Parse("2026-09-11T10:00:00Z"), DateTimeOffset.Parse("2026-09-11T11:00:00Z"), AssignmentStatus.Active);

    // Ten minutes into the fixture's interval, mirroring the Swift suite (start = now - 600 s).
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-11T10:10:00Z");

    [Fact]
    public void ExactAssignmentOnlyAndCompletedNeverRetries()
    {
        var a = Assignment();
        var entry = new ProfileRun.Entry(a, "Reader");
        entry.Disposition(a, Now).Should().Be(ProfileRun.Disposition.Ready);
        entry.Disposition(a with { StartDateTime = Now }, Now).Should().Be(ProfileRun.Disposition.Skipped("Assignment replaced"));
        entry.Disposition(a with { AssignmentId = "later" }, Now).Should().Be(ProfileRun.Disposition.Skipped("Assignment replaced"));
        (entry with { Completed = true }).Disposition(a, Now).Should().Be(ProfileRun.Disposition.Completed);
    }

    [Fact]
    public void ExpiredMissingAndMinimumPeriodArePerEntry()
    {
        var a = Assignment();
        var entry = new ProfileRun.Entry(a, "Reader");
        entry.Disposition(null, Now).Should().Be(ProfileRun.Disposition.Skipped("Already inactive or expired"));
        entry.Disposition(a, Now.AddSeconds(3601)).Should().Be(ProfileRun.Disposition.Skipped("Already inactive or expired"));
        a = a with { StartDateTime = Now.AddSeconds(-100) };
        new ProfileRun.Entry(a, "Reader").Disposition(a, Now).Should()
            .Be(ProfileRun.Disposition.Blocked("Can be deactivated in 200 s (minimum activation period)"));
        a = a with { AssignmentId = null };
        new ProfileRun.Entry(a, "Reader").Disposition(a, Now).Should()
            .Be(ProfileRun.Disposition.Blocked("Assignment identity could not be verified"));
    }

    [Fact]
    public void PendingAssignmentAbsenceStaysRetryableAndRequestCorrelationSurvivesApproval()
    {
        var pending = Assignment() with { Status = AssignmentStatus.PendingApproval, EndDateTime = null, AssignmentId = "request" };
        var entry = new ProfileRun.Entry(pending, "Reader");
        entry.Disposition(null, Now).Should().Be(ProfileRun.Disposition.Blocked("Awaiting active assignment confirmation"));
        var approved = Assignment() with { ScheduleId = "schedule", ActivationRequestId = "request" };
        entry.Disposition(approved, Now).Should()
            .Be(ProfileRun.Disposition.Blocked("Original activation interval could not be verified; deactivate this role individually"));
        var knownInterval = new ProfileRun.Entry(pending with { EndDateTime = approved.EndDateTime }, "Reader");
        knownInterval.Disposition(approved, Now).Should().Be(ProfileRun.Disposition.Ready);
        ProfileRun.Matches(knownInterval.Assignment, approved with { EndDateTime = approved.EndDateTime!.Value.AddHours(1) }).Should().BeFalse();
        knownInterval.Disposition(approved with { ActivationRequestId = "different-request" }, Now).Should()
            .Be(ProfileRun.Disposition.Skipped("Assignment replaced"));
    }

    [Fact]
    public void ScheduleCorrelationSurvivesDifferentRequestAndInstanceIds()
    {
        var requested = Assignment() with { AssignmentId = "request", ScheduleId = "schedule" };
        var refreshed = requested with { AssignmentId = "instance" };
        var entry = new ProfileRun.Entry(requested, "Reader");
        entry.Disposition(refreshed, Now).Should().Be(ProfileRun.Disposition.Ready);
        entry.Disposition(refreshed with { EndDateTime = refreshed.EndDateTime!.Value.AddHours(1) }, Now).Should()
            .Be(ProfileRun.Disposition.Skipped("Assignment replaced"));
        entry.Disposition(refreshed with { ScheduleId = "replacement" }, Now).Should().Be(ProfileRun.Disposition.Skipped("Assignment replaced"));
    }

    [Fact]
    public void PendingEntryWhoseAssignmentIsNotYetActiveWaitsForConfirmation()
    {
        var a = Assignment();
        new ProfileRun.Entry(a, "Reader").Disposition(a with { Status = AssignmentStatus.PendingProvisioning }, Now).Should()
            .Be(ProfileRun.Disposition.Blocked("Awaiting active assignment confirmation"));
        ProfileRun.Disposition.Blocked("x").Should().NotBe(ProfileRun.Disposition.Skipped("x"));
        ProfileRun.Disposition.Blocked("x").Reason.Should().Be("x");
        ProfileRun.Disposition.Ready.Reason.Should().BeNull();
    }

    [Fact]
    public void PendingAndScheduledSnapshotsCannotMatchExtendedIntervals()
    {
        var active = Assignment() with { ScheduleId = "schedule" };
        ProfileRun.Matches(active with { Status = AssignmentStatus.PendingApproval, EndDateTime = null }, active).Should().BeFalse();
        ProfileRun.Matches(active with { Status = AssignmentStatus.Scheduled }, active with { EndDateTime = active.EndDateTime!.Value.AddHours(1) }).Should().BeFalse();
        ProfileRun.Matches(active with { Status = AssignmentStatus.Scheduled }, active).Should().BeTrue();
    }

    [Fact]
    public void LegacyStateAndExplicitNullHaveEmptyRunHistory()
    {
        Json.Deserialize<AppState>("{}")!.ProfileRuns.Should().BeEmpty();
        Json.Deserialize<AppState>("{\"profileRuns\":null}")!.ProfileRuns.Should().BeEmpty();
        Json.Serialize(new AppState()).Should().NotContain("profileRuns");
    }

    [Fact]
    public void RunStateRoundTripsAndCloneOwnsItsEntries()
    {
        var state = new AppState { ProfileRuns = [new(Guid.NewGuid(), Guid.NewGuid(), "Ops", DateTimeOffset.Parse("2026-09-11T10:00:00Z"),
            [new(Assignment(), "Reader")])] };
        var decoded = Json.Deserialize<AppState>(Json.Serialize(state))!;
        decoded.Should().Be(state);
        var copy = state.Clone();
        copy.ProfileRuns[0].Entries[0] = copy.ProfileRuns[0].Entries[0] with { Completed = true };
        state.ProfileRuns[0].Entries[0].Completed.Should().BeFalse();
    }

    [Fact]
    public void DifferentInstancesAndExtensionsAreNeverMatchedByRoleAlone()
    {
        var original = Assignment();
        ProfileRun.Matches(original with { AssignmentId = null }, original with { AssignmentId = null }).Should().BeFalse();
        ProfileRun.Matches(original, original with { AssignmentId = "replacement" }).Should().BeFalse();
        ProfileRun.Matches(original, original with { EndDateTime = original.EndDateTime!.Value.AddHours(1) }).Should().BeFalse();
        ProfileRun.Matches(original, original with { StartDateTime = original.StartDateTime.AddMinutes(1) }).Should().BeFalse();
        ProfileRun.Matches(original with { ScheduleId = "schedule" }, original with { AssignmentId = "instance", ScheduleId = "schedule" }).Should().BeTrue();
        ProfileRun.Matches(original with { ScheduleId = "schedule" }, original with { ScheduleId = "other" }).Should().BeFalse();
    }
}
