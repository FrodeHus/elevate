using Elevate.Cli.Infrastructure;
using Elevate.Cli.Session;
using Elevate.Cli.Tests.Support;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Cli.Tests;

public class ActivationWaiterTests
{
    private static readonly RoleKey Key = TestSession.EntraKey("r1");

    private static ActivationWaiter Waiter(TestSession t, Action? beforeEachPoll = null) =>
        new(t.Session, (_, _) =>
        {
            beforeEachPoll?.Invoke();
            return Task.CompletedTask;
        });

    [Fact]
    public async Task ReturnsAtOnceWhenEverythingIsActive()
    {
        using var t = new TestSession();
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        await t.Session.RefreshAllAsync();
        await t.Session.ActivateAsync([new ActivationRequest(Key, TimeSpan.FromHours(1), "x")], deactivateFirst: false);
        var polls = 0;

        await Waiter(t, () => polls++).WaitAsync([Key], TimeSpan.FromMinutes(1));

        polls.Should().Be(0, "an activated role is already active in the session");
    }

    [Fact]
    public async Task SitsThroughAnApprovalAndReportsIt()
    {
        using var t = new TestSession();
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        await t.Session.RefreshAllAsync();
        await t.Session.ActivateAsync([new ActivationRequest(Key, TimeSpan.FromHours(1), "approve-me")], deactivateFirst: false);
        t.Session.Active[Key].Status.Kind.Should().Be(AssignmentStatusKind.PendingApproval);
        var reports = new List<string>();
        var polls = 0;

        await Waiter(t, () =>
        {
            // The approver decides on the second poll.
            if (++polls == 2)
            {
                var now = DateTimeOffset.UtcNow;
                t.Entra.Assignments.Clear();
                t.Entra.Assignments.Add(new ActiveAssignment(Key, "a", now, now.AddHours(1), AssignmentStatus.Active));
            }
        }).WaitAsync([Key], TimeSpan.FromMinutes(5), reports.Add);

        polls.Should().Be(2);
        t.Session.Active[Key].Status.Kind.Should().Be(AssignmentStatusKind.Active);
        reports.Should().ContainSingle().Which.Should().Contain("approver").And.Contain("Global Reader");
    }

    [Fact]
    public async Task ADeniedRequestIsAFailureNotAWait()
    {
        using var t = new TestSession();
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        await t.Session.RefreshAllAsync();
        await t.Session.ActivateAsync([new ActivationRequest(Key, TimeSpan.FromHours(1), "approve-me")], deactivateFirst: false);

        var act = () => Waiter(t, () => t.Entra.Assignments.Clear()).WaitAsync([Key], TimeSpan.FromMinutes(5));

        (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("Global Reader").And.Contain("denied");
    }

    [Fact]
    public async Task GivesUpAtTheTimeoutNamingWhatIsStillMissing()
    {
        using var t = new TestSession();
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        await t.Session.RefreshAllAsync();
        var reports = new List<string>();

        var act = () => Waiter(t).WaitAsync([Key], TimeSpan.Zero, reports.Add);

        var error = (await act.Should().ThrowAsync<CliException>()).Which;
        error.Message.Should().Contain("Global Reader").And.Contain("stays active");
        error.ExitCode.Should().Be(ExitCodes.Failure);
        reports.Should().ContainSingle().Which.Should().Be("Waiting for Global Reader to become active…");
    }

    [Fact]
    public async Task DescribesSeveralRolesByCount()
    {
        using var t = new TestSession();
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        t.Entra.Eligible.Add(TestSession.Role("r2", "User Administrator"));
        await t.Session.RefreshAllAsync();

        new ActivationWaiter(t.Session).Describe([Key, TestSession.EntraKey("r2")]).Should().Be("Waiting for 2 roles to become active…");
    }
}
