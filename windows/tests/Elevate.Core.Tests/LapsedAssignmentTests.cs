using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class LapsedAssignmentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    private static ActiveAssignment Assignment(AssignmentStatus status, double? endsInSeconds) =>
        new(new RoleKey("i", "t", new EntraDirectoryScope("r", "/")), "a", Now.AddHours(-2),
            endsInSeconds is { } s ? Now.AddSeconds(s) : null, status);

    [Fact]
    public void ActiveLapsesOnceItsEndHasPassed()
    {
        Assignment(AssignmentStatus.Active, -1).HasLapsed(Now).Should().BeTrue();
        Assignment(AssignmentStatus.Active, 0).HasLapsed(Now).Should().BeTrue();
        Assignment(AssignmentStatus.Active, 1).HasLapsed(Now).Should().BeFalse();
    }

    [Fact]
    public void ScheduledLapsesOnceItsEndHasPassed()
    {
        Assignment(AssignmentStatus.Scheduled, -60).HasLapsed(Now).Should().BeTrue();
        Assignment(AssignmentStatus.Scheduled, 60).HasLapsed(Now).Should().BeFalse();
    }

    [Fact]
    public void WithoutAnEndNothingLapses()
    {
        Assignment(AssignmentStatus.Active, null).HasLapsed(Now).Should().BeFalse();
    }

    /// <summary>Requests and failures carry no activation window of their own; the service settles them.</summary>
    [Fact]
    public void RequestsAndFailuresNeverLapse()
    {
        Assignment(AssignmentStatus.PendingApproval, -60).HasLapsed(Now).Should().BeFalse();
        Assignment(AssignmentStatus.PendingProvisioning, -60).HasLapsed(Now).Should().BeFalse();
        Assignment(AssignmentStatus.Failed("x"), -60).HasLapsed(Now).Should().BeFalse();
    }
}
