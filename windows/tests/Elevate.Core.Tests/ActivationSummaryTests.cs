using Elevate.Core.Coordination;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>ActivationSummaryTests</c>.</summary>
public class ActivationSummaryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    private static RoleKey Key(string n) => new("i", "t", new EntraDirectoryScope(n, "/"));

    private static ActiveAssignment Assignment(string n, AssignmentStatus status, double start = 0, double? end = null) =>
        new(Key(n), n, Now.AddSeconds(start), end is { } e ? Now.AddSeconds(e) : null, status);

    private static ActivationOutcome Outcome(string n, ActivationResult result) => new(Key(n), result);

    /// <summary>The role definition id stands in for the display name in these tests.</summary>
    private static string Body(IReadOnlyList<ActivationOutcome> outcomes, int attempted) =>
        ActivationSummary.Body(outcomes, attempted, k => k.Scope is EntraDirectoryScope s ? s.RoleDefinitionId : "?", Now);

    [Fact]
    public void NothingAttemptedReadsNothingToDo() => Body([], 0).Should().Be("Nothing to do");

    [Fact]
    public void AttemptedWithNoOutcomesReadsNotCompleted() =>
        Body([], 3).Should().Be("Not completed; open Elevate for details");

    [Fact]
    public void SingleActivatedWithEndShowsDuration()
    {
        var o = Outcome("a", new ActivationResult.Activated(Assignment("a", AssignmentStatus.Active, end: 7200)));
        Body([o], 1).Should().Be("Active for 02:00");
    }

    [Fact]
    public void SingleActivatedWithoutEndIsJustActive()
    {
        var o = Outcome("a", new ActivationResult.Activated(Assignment("a", AssignmentStatus.Active)));
        Body([o], 1).Should().Be("Active");
    }

    [Fact]
    public void SingleScheduledCountsDownToStart()
    {
        var o = Outcome("a", new ActivationResult.Scheduled(Assignment("a", AssignmentStatus.Scheduled, start: 3 * 3600 + 30 * 60)));
        Body([o], 1).Should().Be("Scheduled to start in 3 h 30 m");
    }

    [Fact]
    public void SinglePendingApprovalReadsAwaitingApproval()
    {
        var o = Outcome("a", new ActivationResult.PendingApproval(Assignment("a", AssignmentStatus.PendingApproval)));
        Body([o], 1).Should().Be("Awaiting approval");
    }

    [Fact]
    public void SingleFailureShowsTheUserMessage()
    {
        var o = Outcome("a", new ActivationResult.Failed(new PimException(PimErrorKind.NotEligible)));
        Body([o], 1).Should().Be("Failed: Not eligible for this role");
    }

    [Fact]
    public void SeveralOutcomesAreCountedWithPlurals()
    {
        var outcomes = new[]
        {
            Outcome("a", new ActivationResult.Activated(Assignment("a", AssignmentStatus.Active, end: 3600))),
            Outcome("b", new ActivationResult.Activated(Assignment("b", AssignmentStatus.Active, end: 3600))),
            Outcome("c", new ActivationResult.Scheduled(Assignment("c", AssignmentStatus.Scheduled, start: 600))),
            Outcome("d", new ActivationResult.PendingApproval(Assignment("d", AssignmentStatus.PendingApproval))),
            Outcome("e", new ActivationResult.Failed(new PimException(PimErrorKind.NotEligible))),
        };

        Body(outcomes, 5).Should().Be("2 roles activated, 1 scheduled, 1 awaiting approval, 1 failed: e: Not eligible for this role");
    }

    [Fact]
    public void SeveralFailuresCollapseToTheFirstPlusACount()
    {
        var outcomes = new[]
        {
            Outcome("a", new ActivationResult.Failed(new PimException(PimErrorKind.NotEligible))),
            Outcome("b", new ActivationResult.Failed(new PimException(PimErrorKind.PendingApproval))),
            Outcome("c", new ActivationResult.Failed(new PimException(PimErrorKind.ConsentRequired))),
        };

        Body(outcomes, 3).Should().Be("3 roles failed: a: Not eligible for this role and 2 more");
    }

    [Fact]
    public void ASingleRoleIsSingularWhenItLeadsTheCounts()
    {
        var outcomes = new[]
        {
            Outcome("a", new ActivationResult.Activated(Assignment("a", AssignmentStatus.Active, end: 3600))),
            Outcome("b", new ActivationResult.Scheduled(Assignment("b", AssignmentStatus.Scheduled, start: 600))),
        };

        Body(outcomes, 2).Should().Be("1 role activated, 1 scheduled");
    }
}
