using Elevate.Core.Coordination;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Cli.Tests;

/// <summary>
/// What the CLI puts in front of the user once a role is active: the propagation states Core
/// reports, and the deadlines that bound the wait.
/// </summary>
public class PropagationReporterTests
{
    [Fact]
    public void ADeadlineIsSetPerKindAndTheLongestWins()
    {
        // A run that activates an Entra role and an Azure one must allow for the slower of the two.
        PropagationHints.Deadline([RoleScopeKind.EntraDirectory, RoleScopeKind.Group])
            .Should().Be(PropagationHints.Deadline(RoleScopeKind.EntraDirectory));
        PropagationHints.Deadline([RoleScopeKind.EntraDirectory, RoleScopeKind.AzureResource])
            .Should().Be(PropagationHints.Deadline(RoleScopeKind.AzureResource));
    }

    [Fact]
    public void TheTypicalWaitIsShorterThanTheDeadlineForEveryKind()
    {
        // The spinner promises the typical time; the deadline is what we actually allow.
        foreach (var kind in Enum.GetValues<RoleScopeKind>())
        {
            PropagationHints.Typical(kind).Should().BeLessThan(PropagationHints.Deadline(kind));
        }
    }

    [Fact]
    public void EveryKindNamesSomethingTheUserCanActOn()
    {
        foreach (var kind in Enum.GetValues<RoleScopeKind>())
        {
            PropagationHints.LikelyCause(kind).Should().Contain("signed in");
        }
    }

    [Fact]
    public void OnlyPropagatingIsUnsettled()
    {
        var key = new RoleKey("id1", "t1", new EntraDirectoryScope("r", "/"));
        new PropagationOutcome(key, PropagationState.Propagating).IsSettled.Should().BeFalse();
        foreach (var state in new[] { PropagationState.Ready, PropagationState.Unconfirmed, PropagationState.Unobservable })
        {
            new PropagationOutcome(key, state).IsSettled.Should().BeTrue();
        }
    }
}
