using Elevate.App.Services;
using Elevate.App.Tests.Support;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Support;
using FluentAssertions;

namespace Elevate.App.Tests;

/// <summary>The Azure pivot's scope tree: what it groups, what a search leaves, and what the subtree checkbox takes.</summary>
public class AppModelScopeTreeTests
{
    private const string SubA = "/subscriptions/aaaaaaaa-0000-0000-0000-000000000000";
    private const string SubB = "/subscriptions/bbbbbbbb-0000-0000-0000-000000000000";

    private static EligibleRole Azure(string scope, string name, string? detail = null) =>
        new(Sample.Key(new AzureResourceScope(scope, "rd-" + name)), name, RoleSource.Discovered, RolePolicy.ManualDefault, Detail: detail);

    /// <summary>Two subscriptions: one branching into resource groups, one holding a single role.</summary>
    private static async Task<TestModel> SeededAsync()
    {
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        var test = await TestModel.BootstrappedAsync(state);
        test.Model.Roles[Sample.TenantKey] =
        [
            Azure(SubA, "Owner", "Alpha · subscription"),
            Azure(SubA + "/resourceGroups/prod-rg", "Contributor", "prod-rg · resource group"),
            Azure(SubA + "/resourceGroups/test-rg", "Reader", "test-rg · resource group"),
            Azure(SubB, "Contributor", "Bravo · subscription"),
            Sample.Role(Sample.EntraKey, "Global Reader"),
        ];
        return test;
    }

    [Fact]
    public async Task AzureTreeGroupsResourceGroupsUnderTheirSubscriptionAndLeavesEntraOut()
    {
        using var test = await SeededAsync();
        var tree = test.Model.AzureTree(Sample.TenantKey);

        tree.Select(n => n.Title).Should().Equal("Alpha", "Bravo");
        tree[0].Children.Select(n => n.Title).Should().Equal("prod-rg", "test-rg");
        tree[0].RoleCount.Should().Be(3);
        tree[1].RoleCount.Should().Be(1);
    }

    [Fact]
    public async Task AzureTreeNarrowsWithTheSearchBox()
    {
        using var test = await SeededAsync();
        test.Model.SearchQuery = "prod-rg";

        var tree = test.Model.AzureTree(Sample.TenantKey);
        tree.Should().ContainSingle();
        tree[0].AllRoles.Select(r => r.DisplayName).Should().Equal("Contributor");
    }

    [Fact]
    public async Task TheSearchBoxReachesTheArmPathEvenWhenNoCaptionShowsIt()
    {
        using var test = await SeededAsync();
        test.Model.SearchQuery = "bbbbbbbb";

        test.Model.AzureTree(Sample.TenantKey).SelectMany(n => n.AllRoles).Should().ContainSingle();
    }

    [Fact]
    public async Task ACollapsedScopeReopensWhenToggledAgain()
    {
        using var test = await SeededAsync();
        var model = test.Model;
        var alpha = model.AzureTree(Sample.TenantKey)[0];

        model.IsScopeCollapsed(Sample.TenantKey, alpha).Should().BeFalse("scopes start open, as tenants do");
        model.ToggleScope(Sample.TenantKey, alpha);
        model.IsScopeCollapsed(Sample.TenantKey, alpha).Should().BeTrue();
        model.ToggleScope(Sample.TenantKey, alpha);
        model.IsScopeCollapsed(Sample.TenantKey, alpha).Should().BeFalse();
    }

    [Fact]
    public async Task AClosedScopeStillShowsItsMatchesWhileSearching()
    {
        // A match must never be hidden behind a node the user closed earlier.
        using var test = await SeededAsync();
        var model = test.Model;
        model.ToggleScope(Sample.TenantKey, model.AzureTree(Sample.TenantKey)[0]);

        model.SearchQuery = "prod";
        var tree = model.AzureTree(Sample.TenantKey);
        tree.Should().NotBeEmpty();
        model.IsScopeCollapsed(Sample.TenantKey, tree[0]).Should().BeFalse();
    }

    [Fact]
    public async Task ToggleSubtreeTakesEveryEligibilityUnderTheScope()
    {
        using var test = await SeededAsync();
        var model = test.Model;
        var alpha = model.AzureTree(Sample.TenantKey)[0];

        model.SubtreeState(alpha).Should().Be(AppModel.SubtreeSelection.None);
        model.ToggleSubtree(alpha).Should().Be(3);
        model.SubtreeState(alpha).Should().Be(AppModel.SubtreeSelection.All);
        model.Selection.Should().HaveCount(3);
    }

    [Fact]
    public async Task PressingAFullyChosenSubtreeAgainLetsItGo()
    {
        using var test = await SeededAsync();
        var model = test.Model;
        var alpha = model.AzureTree(Sample.TenantKey)[0];

        model.ToggleSubtree(alpha);
        model.ToggleSubtree(alpha).Should().Be(3);
        model.Selection.Should().BeEmpty();
    }

    [Fact]
    public async Task APartlyChosenSubtreeFillsUpRatherThanEmptying()
    {
        using var test = await SeededAsync();
        var model = test.Model;
        var alpha = model.AzureTree(Sample.TenantKey)[0];
        model.ToggleSelection(alpha.Roles[0].Key);

        model.SubtreeState(alpha).Should().Be(AppModel.SubtreeSelection.Some);
        model.ToggleSubtree(alpha).Should().Be(2, because: "the one already chosen is left where it is");
        model.SubtreeState(alpha).Should().Be(AppModel.SubtreeSelection.All);
    }

    [Fact]
    public async Task ASubtreeCheckboxOnlyTakesWhatCanActuallyBeActivated()
    {
        using var test = await SeededAsync();
        var model = test.Model;
        var alpha = model.AzureTree(Sample.TenantKey)[0];
        model.Active[alpha.Roles[0].Key] = Sample.Assignment(alpha.Roles[0].Key);

        model.SubtreeKeys(alpha).Should().HaveCount(2);
        model.ToggleSubtree(alpha).Should().Be(2);
        model.SubtreeState(alpha).Should().Be(AppModel.SubtreeSelection.All);
    }

    [Fact]
    public async Task ASubtreeWithNothingToTakeReportsNoSelection()
    {
        using var test = await SeededAsync();
        var model = test.Model;
        var bravo = model.AzureTree(Sample.TenantKey)[1];
        model.Active[bravo.Roles[0].Key] = Sample.Assignment(bravo.Roles[0].Key);

        model.SubtreeKeys(bravo).Should().BeEmpty();
        model.SubtreeState(bravo).Should().Be(AppModel.SubtreeSelection.None);
        model.ToggleSubtree(bravo).Should().Be(0);
    }

    [Fact]
    public async Task TheSameScopeUnderTwoAccountsCollapsesIndependently()
    {
        var state = new AppState
        {
            Identities = [Sample.Identity("id-1"), Sample.Identity("id-2")],
            Tenants = [Sample.Tenant(identityId: "id-1"), Sample.Tenant(identityId: "id-2", tenantId: "tenant-2")],
        };
        using var test = await TestModel.BootstrappedAsync(state);
        var model = test.Model;
        var first = new TenantKey("id-1", Sample.TenantId);
        var second = new TenantKey("id-2", "tenant-2");
        model.Roles[first] =
        [
            new EligibleRole(Sample.Key(new AzureResourceScope(SubA, "a"), "id-1"), "Owner", RoleSource.Discovered, RolePolicy.ManualDefault),
            new EligibleRole(Sample.Key(new AzureResourceScope(SubA + "/resourceGroups/rg", "b"), "id-1"), "Reader", RoleSource.Discovered, RolePolicy.ManualDefault),
        ];
        model.Roles[second] =
        [
            new EligibleRole(Sample.Key(new AzureResourceScope(SubA, "a"), "id-2", "tenant-2"), "Owner", RoleSource.Discovered, RolePolicy.ManualDefault),
            new EligibleRole(Sample.Key(new AzureResourceScope(SubA + "/resourceGroups/rg", "b"), "id-2", "tenant-2"), "Reader", RoleSource.Discovered, RolePolicy.ManualDefault),
        ];

        model.ToggleScope(first, model.AzureTree(first)[0]);
        model.IsScopeCollapsed(first, model.AzureTree(first)[0]).Should().BeTrue();
        model.IsScopeCollapsed(second, model.AzureTree(second)[0]).Should().BeFalse();
    }

    [Fact]
    public async Task TheFlattenedRowsSkipWhatAClosedScopeHides()
    {
        using var test = await SeededAsync();
        var model = test.Model;
        var tree = model.AzureTree(Sample.TenantKey);

        ScopeTree.Flatten(tree, n => model.IsScopeCollapsed(Sample.TenantKey, n)).Should().HaveCount(5);
        model.ToggleScope(Sample.TenantKey, tree[0]);
        ScopeTree.Flatten(tree, n => model.IsScopeCollapsed(Sample.TenantKey, n))
            .Should().HaveCount(2, because: "Alpha's header stays, its three rows go, and Bravo's single role row remains");
    }
}
