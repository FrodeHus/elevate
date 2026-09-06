using Elevate.App.Services;
using Elevate.App.Tests.Support;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.App.Tests;

public class AppModelPanelTests
{
    /// <summary>Builds a model holding one Entra, one Azure and one group role in the same tenant.</summary>
    private static async Task<TestModel> ModelWithOneOfEachKindAsync()
    {
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        var test = await TestModel.BootstrappedAsync(state);
        test.Model.Roles[Sample.TenantKey] =
        [
            Sample.Role(Sample.EntraKey, "Global Reader"),
            Sample.Role(Sample.AzureKey, "Owner"),
            Sample.Role(Sample.GroupKey, "Platform Admins"),
        ];
        return test;
    }

    [Fact]
    public async Task RolesForTabFilterByKind()
    {
        using var test = await ModelWithOneOfEachKindAsync();
        var model = test.Model;
        model.RolesFor(Sample.TenantKey, PanelTab.Roles).Select(r => r.DisplayName).Should().Equal("Global Reader");
        model.RolesFor(Sample.TenantKey, PanelTab.Azure).Select(r => r.DisplayName).Should().Equal("Owner");
        model.RolesFor(Sample.TenantKey, PanelTab.Groups).Select(r => r.DisplayName).Should().Equal("Platform Admins");
    }

    [Fact]
    public async Task VisibleIdentitiesDropsAccountsWithoutAMatchWhileFiltering()
    {
        var state = new AppState
        {
            Identities = [Sample.Identity("id-1"), Sample.Identity("id-2")],
            Tenants = [Sample.Tenant(identityId: "id-1"), Sample.Tenant(identityId: "id-2", tenantId: "tenant-2")],
        };
        using var test = await TestModel.BootstrappedAsync(state);
        var model = test.Model;
        var otherKey = new TenantKey("id-2", "tenant-2");
        model.Roles[Sample.TenantKey] = [Sample.Role(Sample.EntraKey, "Global Reader")];
        model.Roles[otherKey] =
        [
            Sample.Role(Sample.Key(new EntraDirectoryScope("other", "/"), identityId: "id-2", tenantId: "tenant-2"), "Billing Admin"),
        ];

        model.VisibleIdentities.Select(i => i.Id).Should().Equal("id-1", "id-2");
        model.SearchQuery = "billing";
        model.VisibleIdentities.Select(i => i.Id).Should().Equal("id-2");
        model.VisibleTenants("id-1").Should().BeEmpty();
    }

    [Fact]
    public async Task ActiveAssignmentsOrderedShowsOnlyTheCurrentTabsKinds()
    {
        using var test = await ModelWithOneOfEachKindAsync();
        var model = test.Model;
        model.Active[Sample.EntraKey] = Sample.Assignment(Sample.EntraKey);
        model.Active[Sample.AzureKey] = Sample.Assignment(Sample.AzureKey);
        model.Active[Sample.GroupKey] = Sample.Assignment(Sample.GroupKey);
        model.PanelTab = PanelTab.Roles;
        model.ActiveAssignmentsOrdered.Select(a => a.RoleKey).Should().Equal(Sample.EntraKey);
        model.PanelTab = PanelTab.Azure;
        model.ActiveAssignmentsOrdered.Select(a => a.RoleKey).Should().Equal(Sample.AzureKey);
        model.ActiveCount(PanelTab.Groups).Should().Be(1);
    }

    [Fact]
    public async Task CanActivateIsFalseForAnEntraRoleOnAFirstPartyIdentity()
    {
        using var test = await TestModel.BootstrappedAsync();
        var model = test.Model;
        // Set after bootstrap, which reconciles identities against the token caches.
        model.State.Identities.Add(Sample.Identity(method: SignInMethod.AzureCLI));
        model.State.Tenants.Add(Sample.Tenant());

        model.CanActivate(Sample.EntraKey).Should().BeFalse();
        model.EntraViewOnlyReason(Sample.TenantKey).Should().NotBeNull();
        // Azure resource roles go through ARM and stay activatable with the same account.
        model.CanActivate(Sample.AzureKey).Should().BeTrue();
    }

    [Fact]
    public async Task PanelTabIsPersistedInSettings()
    {
        using var test = await TestModel.BootstrappedAsync();
        test.Model.PanelTab = PanelTab.Groups;
        new AppSettings(test.Directory).PanelTab.Should().Be(PanelTab.Groups);
    }
}

public class AppModelSelectionTests
{
    private static async Task<TestModel> SelectedModelAsync()
    {
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        var test = await TestModel.BootstrappedAsync(state);
        var model = test.Model;
        model.Roles[Sample.TenantKey] =
        [
            Sample.Role(Sample.EntraKey, "Global Reader"),
            Sample.Role(Sample.AzureKey, "Owner"),
            Sample.Role(Sample.GroupKey, "Platform Admins"),
        ];
        model.SelectMode = true;
        model.ToggleSelection(Sample.EntraKey);
        model.ToggleSelection(Sample.AzureKey);
        model.ToggleSelection(Sample.GroupKey);
        return test;
    }

    [Fact]
    public async Task SelectionSurvivesATabChangeAndClearsOnASearchChange()
    {
        using var test = await SelectedModelAsync();
        var model = test.Model;
        model.SelectionCount.Should().Be(3);

        model.PanelTab = PanelTab.Azure;
        model.SelectionCount.Should().Be(3); // a selection may span pivots

        model.SearchQuery = "owner";
        model.Selection.Should().BeEmpty();
    }

    [Fact]
    public async Task SelectionBreakdownCountsEachKind()
    {
        using var test = await SelectedModelAsync();
        var model = test.Model;
        model.SelectionBreakdown.Should().Be((1, 1, 1));
        model.SelectionNoun.Should().Be("item");

        model.ToggleSelection(Sample.EntraKey);
        model.ToggleSelection(Sample.AzureKey);
        model.SelectionBreakdown.Should().Be((0, 0, 1));
        model.SelectionNoun.Should().Be("group");
    }

    [Fact]
    public async Task LeavingSelectModeClearsTheSelection()
    {
        using var test = await SelectedModelAsync();
        test.Model.SelectMode = false;
        test.Model.Selection.Should().BeEmpty();
    }
}
