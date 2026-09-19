using Elevate.Core.Models;
using Elevate.Core.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class ScopeTreeTests
{
    private const string SubA = "/subscriptions/aaaaaaaa-0000-0000-0000-000000000000";
    private const string SubB = "/subscriptions/bbbbbbbb-0000-0000-0000-000000000000";
    private const string Mg = "/providers/Microsoft.Management/managementGroups/platform";

    private static EligibleRole Role(string name, string scope, string? detail = null) =>
        new(new RoleKey("i", "t", new AzureResourceScope(scope, "rd-" + name)),
            name, RoleSource.Discovered, RolePolicy.ManualDefault, Detail: detail);

    [Fact]
    public void Build_NestsResourceGroupsUnderTheirSubscription()
    {
        var tree = ScopeTree.Build(
        [
            Role("Contributor", SubA + "/resourceGroups/prod", "prod · resource group"),
            Role("Reader", SubA + "/resourceGroups/test", "test · resource group"),
            Role("Owner", SubA, "Platform · subscription"),
        ]);

        tree.Should().ContainSingle();
        var sub = tree[0];
        sub.Kind.Should().Be(ArmScopeKind.Subscription);
        sub.Title.Should().Be("Platform", because: "the role held on the subscription itself carries its caption");
        sub.Roles.Select(r => r.DisplayName).Should().Equal("Owner");
        sub.Children.Select(c => c.Title).Should().Equal("prod", "test");
        sub.RoleCount.Should().Be(3);
    }

    [Fact]
    public void Build_ShowsTheArmNameWhenNoRoleSitsOnTheScopeItself()
    {
        var tree = ScopeTree.Build(
        [
            Role("Contributor", SubA + "/resourceGroups/prod", "prod · resource group"),
            Role("Reader", SubA + "/resourceGroups/test", "test · resource group"),
        ]);

        // Nothing is eligible on the subscription, so the service never named it.
        tree[0].DisplayName.Should().BeNull();
        tree[0].Title.Should().Be("aaaaaaaa-0000-0000-0000-000000000000");
    }

    [Fact]
    public void Build_PutsManagementGroupsBesideSubscriptionsRatherThanAboveThem()
    {
        // ARM never repeats the management group in a subscription's scope, so the string cannot
        // tell us the subscription sits under it.
        var tree = ScopeTree.Build([Role("Reader", Mg, "Platform · management group"), Role("Owner", SubA, "A · subscription")]);
        tree.Select(n => n.Kind).Should().Equal(ArmScopeKind.ManagementGroup, ArmScopeKind.Subscription);
    }

    [Fact]
    public void Build_IgnoresRolesThatAreNotAzureResourceRoles()
    {
        var entra = new EligibleRole(
            new RoleKey("i", "t", new EntraDirectoryScope("rd", "/")), "Global Reader", RoleSource.Discovered, RolePolicy.ManualDefault);
        ScopeTree.Build([entra, Role("Owner", SubA)]).Should().ContainSingle();
    }

    [Fact]
    public void Build_SortsRootsByKindThenTitle()
    {
        var tree = ScopeTree.Build(
        [
            Role("Owner", SubB, "Zulu · subscription"),
            Role("Owner", SubA, "Alpha · subscription"),
            Role("Reader", Mg, "Platform · management group"),
        ]);
        tree.Select(n => n.Title).Should().Equal("Platform", "Alpha", "Zulu");
    }

    [Fact]
    public void Flatten_ElidesAScopeThatLeadsToOneRoleAndBranchesNowhere()
    {
        // The motivating case: many subscriptions, one eligibility each. A header per subscription
        // would double the rows and show nothing the role row does not already say.
        var tree = ScopeTree.Build([Role("Contributor", SubA, "Alpha · subscription"), Role("Contributor", SubB, "Zulu · subscription")]);
        var rows = ScopeTree.Flatten(tree);

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => !r.IsScope);
        rows.Should().OnlyContain(r => r.Depth == 0);
    }

    [Fact]
    public void Flatten_DrawsAHeaderAsSoonAsAScopeHasSomethingToBranchInto()
    {
        var tree = ScopeTree.Build(
        [
            Role("Owner", SubA, "Alpha · subscription"),
            Role("Contributor", SubA + "/resourceGroups/prod", "prod · resource group"),
        ]);
        var rows = ScopeTree.Flatten(tree);

        rows.Select(r => (r.Depth, r.IsScope, r.Role?.DisplayName)).Should().Equal(
            (0, true, null),
            (1, false, "Owner"),
            (1, false, "Contributor"));
    }

    [Fact]
    public void Flatten_DrawsAHeaderWhenOneScopeHoldsSeveralRoles()
    {
        var tree = ScopeTree.Build([Role("Owner", SubA, "Alpha · subscription"), Role("Reader", SubA, "Alpha · subscription")]);
        var rows = ScopeTree.Flatten(tree);
        rows[0].IsScope.Should().BeTrue();
        rows.Skip(1).Select(r => r.Role!.DisplayName).Should().Equal("Owner", "Reader");
    }

    [Fact]
    public void Flatten_StopsAtACollapsedNode()
    {
        var tree = ScopeTree.Build(
        [
            Role("Owner", SubA, "Alpha · subscription"),
            Role("Contributor", SubA + "/resourceGroups/prod", "prod · resource group"),
        ]);
        var rows = ScopeTree.Flatten(tree, n => n.Scope == SubA);

        rows.Should().ContainSingle();
        rows[0].IsScope.Should().BeTrue();
    }

    [Fact]
    public void Flatten_NestsAResourceUnderItsResourceGroup()
    {
        var rg = SubA + "/resourceGroups/prod";
        var tree = ScopeTree.Build(
        [
            Role("Owner", rg, "prod · resource group"),
            Role("Reader", rg + "/providers/Microsoft.Compute/virtualMachines/web01", "web01 · virtualmachines"),
            Role("Reader", rg + "/providers/Microsoft.Compute/virtualMachines/web02", "web02 · virtualmachines"),
        ]);
        var rows = ScopeTree.Flatten(tree);

        // The subscription is folded away — it holds no role of its own and leads to a single child.
        rows.Select(r => (r.Depth, r.IsScope)).Should().Equal(
            (0, true), (1, false), (1, false), (1, false));
        rows[0].Node!.Kind.Should().Be(ArmScopeKind.ResourceGroup);
    }

    [Fact]
    public void Build_FoldsAPassThroughScopeIntoTheNodeBelowIt()
    {
        var rg = SubA + "/resourceGroups/prod";
        var tree = ScopeTree.Build(
        [
            Role("Owner", rg, "prod · resource group"),
            Role("Reader", rg, "prod · resource group"),
        ]);

        tree.Should().ContainSingle();
        tree[0].Kind.Should().Be(ArmScopeKind.ResourceGroup);
        tree[0].Ancestors.Should().Equal("aaaaaaaa-0000-0000-0000-000000000000");
        tree[0].Path.Should().Be("aaaaaaaa-0000-0000-0000-000000000000 / prod");
        tree[0].Scope.Should().Be(rg, because: "selecting the folded node must still reach the same subtree");
    }

    [Fact]
    public void Build_DoesNotFoldAScopeThatHoldsARoleOfItsOwn()
    {
        var tree = ScopeTree.Build(
        [
            Role("Owner", SubA, "Alpha · subscription"),
            Role("Reader", SubA + "/resourceGroups/prod", "prod · resource group"),
        ]);
        tree[0].Kind.Should().Be(ArmScopeKind.Subscription);
        tree[0].Ancestors.Should().BeEmpty();
    }

    [Fact]
    public void Build_DoesNotFoldAScopeThatBranches()
    {
        var tree = ScopeTree.Build(
        [
            Role("Owner", SubA + "/resourceGroups/prod", "prod · resource group"),
            Role("Reader", SubA + "/resourceGroups/test", "test · resource group"),
        ]);
        tree[0].Kind.Should().Be(ArmScopeKind.Subscription);
        tree[0].Children.Should().HaveCount(2);
    }

    [Fact]
    public void AllRoles_ReachesEverythingBelowTheNode()
    {
        var tree = ScopeTree.Build(
        [
            Role("Owner", SubA, "Alpha · subscription"),
            Role("Contributor", SubA + "/resourceGroups/prod", "prod · resource group"),
            Role("Reader", SubA + "/resourceGroups/prod/providers/Microsoft.Compute/virtualMachines/web01", "web01 · vm"),
        ]);
        tree[0].AllRoles.Select(r => r.DisplayName).Should().BeEquivalentTo("Owner", "Contributor", "Reader");
        tree[0].RoleCount.Should().Be(3);
    }
}
