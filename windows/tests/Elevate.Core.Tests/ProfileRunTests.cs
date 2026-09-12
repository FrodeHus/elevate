using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class ProfileRunTests
{
    private static ActiveAssignment Assignment() => new(new RoleKey("id", "tenant", new EntraDirectoryScope("role", "/")),
        "assignment", DateTimeOffset.Parse("2026-09-11T10:00:00Z"), DateTimeOffset.Parse("2026-09-11T11:00:00Z"), AssignmentStatus.Active);

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
