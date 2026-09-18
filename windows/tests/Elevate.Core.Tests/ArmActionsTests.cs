using Elevate.Core.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>ArmActionsTests</c>.</summary>
public class ArmActionsTests
{
    [Theory]
    [InlineData("*", "Microsoft.Compute/virtualMachines/read", true)]
    [InlineData("Microsoft.Compute/*", "Microsoft.Compute/virtualMachines/read", true)]
    [InlineData("*/read", "Microsoft.Compute/virtualMachines/read", true)]
    [InlineData("Microsoft.Compute/*/read", "Microsoft.Compute/virtualMachines/read", true)]
    [InlineData("microsoft.compute/*", "Microsoft.Compute/virtualMachines/read", true)]
    [InlineData("Microsoft.Storage/*", "Microsoft.Compute/virtualMachines/read", false)]
    [InlineData("*/write", "Microsoft.Compute/virtualMachines/read", false)]
    [InlineData("Microsoft.Compute/virtualMachines/read", "Microsoft.Compute/virtualMachines/read", true)]
    public void Covers_MatchesGlobsAcrossSlashesAndIgnoresCase(string pattern, string action, bool expected) =>
        ArmActions.Covers(pattern, action).Should().Be(expected);

    [Fact]
    public void Covers_GrantedStarAbsorbsTheLiteralStarAnOwnerDefinitionAsksFor() =>
        ArmActions.Covers("*", "*").Should().BeTrue();

    [Fact]
    public void Grants_IsFalseWhenTheSameEntryTakesTheActionBack()
    {
        IReadOnlyList<ArmPermission> held = [new(["*"], ["Microsoft.Authorization/*/write"])];
        ArmActions.Grants(held, "Microsoft.Compute/virtualMachines/read").Should().BeTrue();
        ArmActions.Grants(held, "Microsoft.Authorization/roleAssignments/write").Should().BeFalse();
    }

    [Fact]
    public void Grants_IgnoresAnExclusionInADifferentEntry()
    {
        // ARM evaluates each assignment on its own: Reader's exclusions do not remove what Owner grants.
        IReadOnlyList<ArmPermission> held = [new(["*"], ["Microsoft.Authorization/*/write"]), new(["*"], [])];
        ArmActions.Grants(held, "Microsoft.Authorization/roleAssignments/write").Should().BeTrue();
    }

    [Fact]
    public void Covers_IsTrueOnlyWhenEveryRequiredActionIsGranted()
    {
        IReadOnlyList<ArmPermission> held = [new(["Microsoft.Compute/*"], [])];
        ArmActions.Covers(held, [new(["Microsoft.Compute/virtualMachines/read"], [])]).Should().BeTrue();
        ArmActions.Covers(held, [new(["Microsoft.Compute/virtualMachines/read", "Microsoft.Storage/*"], [])])
            .Should().BeFalse();
    }

    [Fact]
    public void Covers_IsFalseWhenTheRoleDefinitionGrantsNothing() =>
        // An empty permission set is a read that went wrong, not a role that is in effect.
        ArmActions.Covers([new(["*"], [])], []).Should().BeFalse();
}
