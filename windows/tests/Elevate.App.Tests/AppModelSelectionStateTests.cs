using Elevate.App.Services;
using Elevate.App.Tests.Support;
using Elevate.App.ViewModels;
using Elevate.Core.Managed;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.App.Tests;

public class AppModelSelectionStateTests
{
    private static readonly TenantKey One = new("id-1", "tenant-1");
    private static readonly TenantKey Two = new("id-2", "tenant-2");

    /// <summary>Two accounts with one tenant and one Entra role each.</summary>
    private static async Task<TestModel> TwoTenantsAsync()
    {
        var state = new AppState
        {
            Identities = [Sample.Identity("id-1"), Sample.Identity("id-2")],
            Tenants = [Sample.Tenant("id-1", "tenant-1"), Sample.Tenant("id-2", "tenant-2")],
        };
        var test = await TestModel.BootstrappedAsync(state);
        test.Model.Roles[One] = [Sample.Role(Sample.Key(new EntraDirectoryScope("a", "/"), "id-1", "tenant-1"), "Global Reader")];
        test.Model.Roles[Two] = [Sample.Role(Sample.Key(new EntraDirectoryScope("b", "/"), "id-2", "tenant-2"), "Billing Admin")];

        // Home shows only while something is active or awaiting approval.
        var active = test.Model.Roles[One].Single().Key;
        test.Model.Active[active] = Sample.Assignment(active);
        return test;
    }

    [Fact]
    public async Task DefaultsToAllTenantsAndHome()
    {
        using var test = await TwoTenantsAsync();
        test.Model.PanelSelection.Should().Be(new PanelSelection.AllTenants());
        test.Model.PanelContent.Should().Be(new PanelContent.Home());
        test.Model.ScopeTenant.Should().BeNull();
        test.Model.SelectedProfile.Should().BeNull();
    }

    [Fact]
    public async Task SelectingATenantPersistsAndScopesTheList()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.Select(new PanelSelection.Tenant(One));

        test.Settings.PanelTenant.Should().Be(One);
        model.VisibleTenants("id-2").Should().BeEmpty();
        model.VisibleTenants("id-1").Select(t => t.Key).Should().Equal(One);
        model.VisibleIdentities.Select(i => i.Id).Should().Equal("id-1");
        model.PanelContent.Should().Be(new PanelContent.Tenant(One));
    }

    [Fact]
    public async Task ARemovedTenantFallsBackToAllTenants()
    {
        using var test = await TwoTenantsAsync();
        test.Model.Select(new PanelSelection.Tenant(Two));
        test.Model.State.Tenants.RemoveAll(t => t.Key == Two);

        test.Model.ScopeTenant.Should().BeNull();
        test.Model.PanelSelection.Should().Be(new PanelSelection.AllTenants());
    }

    [Fact]
    public async Task AnAccountNeedingSignInFallsBackToAllTenants()
    {
        using var test = await TwoTenantsAsync();
        test.Model.Select(new PanelSelection.Tenant(Two));
        test.Model.SignInNeeded.Add("id-2");

        test.Model.ScopeTenant.Should().BeNull();
        test.Model.PanelSelection.Should().Be(new PanelSelection.AllTenants());
    }

    [Fact]
    public async Task TypingBrowsingOrSelectModeShowTheRoleList()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.SearchQuery = "read";
        model.PanelContent.Should().Be(new PanelContent.RoleList());
        model.SearchQuery = string.Empty;
        model.Browsing = true;
        model.PanelContent.Should().Be(new PanelContent.RoleList());
        model.Browsing = false;
        model.SelectMode = true;
        model.PanelContent.Should().Be(new PanelContent.RoleList());
    }

    [Fact]
    public async Task SelectingATenantKeepsQueryAndSelection()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.SelectMode = true;
        model.SearchQuery = "read";
        var key = model.Roles[One].Single().Key;
        model.ToggleSelection(key);
        model.Selection.Should().ContainSingle();

        model.Select(new PanelSelection.Tenant(One));
        model.Select(new PanelSelection.AllTenants());

        model.SearchQuery.Should().Be("read");
        model.SelectMode.Should().BeTrue();
        model.Selection.Should().Contain(key);
    }

    [Fact]
    public async Task OtherTenantMatchesAreCountedOnlyWhenScopedAndFiltering()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.SearchQuery = "billing";
        model.OtherTenantMatchCount.Should().Be(0);

        model.Select(new PanelSelection.Tenant(One));
        model.RolesFor(One, model.PanelTab).Should().BeEmpty();
        model.OtherTenantMatchCount.Should().Be(1);
        model.SearchQuery = "zzz";
        model.OtherTenantMatchCount.Should().Be(0);
    }

    [Fact]
    public async Task SearchAllTenantsKeepsTheQuery()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.Select(new PanelSelection.Tenant(One));
        model.SearchQuery = "billing";

        model.SearchAllTenants();

        model.ScopeTenant.Should().BeNull();
        model.SearchQuery.Should().Be("billing");
    }

    [Fact]
    public async Task EscapeClearsTheFilterThenReturnsHomeThenReportsNothingLeft()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.Select(new PanelSelection.Tenant(One));
        model.Browsing = true;
        model.SearchQuery = "read";

        model.HandleEscape().Should().BeTrue();
        model.SearchQuery.Should().BeEmpty();
        model.ScopeTenant.Should().Be(One);

        model.HandleEscape().Should().BeTrue();
        model.ScopeTenant.Should().BeNull();
        model.Browsing.Should().BeFalse();
        model.PanelContent.Should().Be(new PanelContent.Home());

        model.HandleEscape().Should().BeFalse();
    }

    [Fact]
    public async Task EscapeOnAProfileReturnsToTheTenantUnderneath()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        var profile = model.SaveProfile("Ops", [model.Roles[One].Single().Key])!;
        model.Select(new PanelSelection.Tenant(One));
        model.Select(new PanelSelection.Profile(profile.Id));
        model.PanelContent.Should().Be(new PanelContent.Profile(profile.Id));
        model.ScopeTenant.Should().Be(One);

        model.HandleEscape().Should().BeTrue();

        model.SelectedProfile.Should().BeNull();
        model.PanelSelection.Should().Be(new PanelSelection.Tenant(One));
    }

    [Fact]
    public async Task AMissingProfileIsNotASelection()
    {
        using var test = await TwoTenantsAsync();
        test.Model.Select(new PanelSelection.Profile(Guid.NewGuid()));

        test.Model.PanelSelection.Should().Be(new PanelSelection.AllTenants());
        test.Model.PanelContent.Should().Be(new PanelContent.Home());
    }

    [Fact]
    public async Task PanelTenantRoundTripsThroughSettingsJson()
    {
        using var test = await TwoTenantsAsync();
        test.Model.Select(new PanelSelection.Tenant(Two));

        File.ReadAllText(test.Settings.FilePath).Should().Contain("\"panelTenant\"");
        new AppSettings(test.Directory, ManagedConfiguration.None).PanelTenant.Should().Be(Two);
    }

    [Fact]
    public async Task SidebarSectionsAreExpandedByDefaultAndRemembered()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.ProfilesCollapsed.Should().BeFalse();
        model.TenantsCollapsed.Should().BeFalse();

        model.ToggleProfilesSection();
        model.ToggleTenantsSection();

        var reloaded = new AppSettings(test.Directory, ManagedConfiguration.None);
        reloaded.CollapsedProfiles.Should().BeTrue();
        reloaded.CollapsedTenants.Should().BeTrue();
        model.ToggleTenantsSection();
        new AppSettings(test.Directory, ManagedConfiguration.None).CollapsedTenants.Should().BeFalse();
    }

    [Fact]
    public async Task TenantsNeedAttentionWhenAnAccountNeedsSignInOrATenantFailed()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.TenantsNeedAttention.Should().BeFalse();

        model.TenantErrors[One] = "boom";
        model.TenantsNeedAttention.Should().BeTrue();

        model.TenantErrors.Clear();
        model.SignInNeeded.Add("id-2");
        model.TenantsNeedAttention.Should().BeTrue();
    }
}
