using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>QuickActivateTests</c>.</summary>
public class QuickActivateTests
{
    private static readonly RoleKey Key = new("i", "t", new EntraDirectoryScope("r", "/"));
    private static readonly RoleMemory Memory = new(Key, "INC-1", TimeSpan.FromHours(8));

    private static EligibleRole Role(bool justification = true, bool ticket = false, bool approval = false, bool mfa = true, int max = 4) =>
        new(Key, "R", RoleSource.Discovered, new RolePolicy(
            TimeSpan.FromHours(1), TimeSpan.FromHours(max), justification, ticket, mfa, approval, "c1"));

    [Fact]
    public void ReadyUsesRememberedReasonAndCappedDuration()
    {
        var decision = QuickActivate.Decide(Role(), Memory);

        var ready = decision.Should().BeOfType<QuickActivateDecision.Ready>().Subject;
        ready.Requests.Should().HaveCount(1);
        var request = ready.Requests[0];
        request.Justification.Should().Be("INC-1");
        request.Duration.Should().Be(TimeSpan.FromHours(4));
        request.AuthenticationContext.Should().Be("c1");
        request.StartDateTime.Should().BeNull();
        request.Ticket.Should().BeNull();
    }

    [Fact]
    public void DialogReasons()
    {
        QuickActivate.Decide(Role(), null).Should().Be(new QuickActivateDecision.NeedsDialog("no remembered reason"));
        QuickActivate.Decide(Role(justification: false), null).Should().NotBe(new QuickActivateDecision.NeedsDialog("no remembered reason"));
        QuickActivate.Decide(Role(ticket: true), Memory).Should().Be(new QuickActivateDecision.NeedsDialog("ticket required"));
        QuickActivate.Decide(Role(approval: true), Memory).Should().Be(new QuickActivateDecision.NeedsDialog("approval required"));
    }

    [Fact]
    public void ProfileDecision()
    {
        var r = Role();
        var ok = new ProfilePlanItem(Key, r, TimeSpan.FromHours(1), ProfilePlanDisposition.Activate);
        var skipped = new ProfilePlanItem(Key, r, TimeSpan.FromHours(1), ProfilePlanDisposition.AlreadyActive);

        var ready = QuickActivate.Decide([ok, skipped], "INC-2").Should().BeOfType<QuickActivateDecision.Ready>().Subject;
        ready.Requests.Should().HaveCount(1);
        ready.Requests[0].Justification.Should().Be("INC-2");

        QuickActivate.Decide([ok], null).Should().Be(new QuickActivateDecision.NeedsDialog("no remembered reason"));
        QuickActivate.Decide([new ProfilePlanItem(Key, null, TimeSpan.FromMinutes(1), ProfilePlanDisposition.NotLoaded)], "x")
            .Should().Be(new QuickActivateDecision.NeedsDialog("roles still loading"));
        QuickActivate.Decide([new ProfilePlanItem(Key, Role(ticket: true), TimeSpan.FromMinutes(1), ProfilePlanDisposition.Activate)], "x")
            .Should().Be(new QuickActivateDecision.NeedsDialog("ticket required"));
        QuickActivate.Decide([skipped], "x").Should().Be(new QuickActivateDecision.NeedsDialog("nothing to activate"));
    }
}
