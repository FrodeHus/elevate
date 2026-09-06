using Elevate.Core.Models;
using Elevate.Core.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class PolicyNotesTests
{
    private static RolePolicy Policy(bool approval = false, bool mfa = false, string? context = null) => new(
        TimeSpan.FromHours(1), TimeSpan.FromHours(8), RequiresJustification: true, RequiresTicket: false,
        RequiresMfa: mfa, RequiresApproval: approval, AuthenticationContext: context);

    [Fact]
    public void PlainPolicyHasNoNotes()
    {
        PolicyNotes.Labels(Policy()).Should().BeEmpty();
        PolicyNotes.Caption(Policy()).Should().BeNull();
        PolicyNotes.Explanation(Policy()).Should().BeNull();
        PolicyNotes.ActionTitle(Policy()).Should().Be("Activate");
    }

    [Fact]
    public void LabelsAreOrderedApprovalThenStepUps()
    {
        var policy = Policy(approval: true, mfa: true, context: "c1");

        PolicyNotes.Labels(policy).Should().Equal("approval", "MFA", "Conditional Access");
        PolicyNotes.Caption(policy).Should().Be("approval · MFA · Conditional Access");
        PolicyNotes.ActionTitle(policy).Should().Be("Request");
    }

    [Fact]
    public void ConditionalAccessExplanationNamesTheContext()
    {
        var text = PolicyNotes.Explanation(Policy(context: "c1"));

        text.Should().Contain("authentication context c1");
        text.Should().NotContain("approver");
    }
}
