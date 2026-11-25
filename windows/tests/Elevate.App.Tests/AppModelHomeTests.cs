using Elevate.App.Services;
using Elevate.App.Tests.Support;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.App.Tests;

public class AppModelHomeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly TenantKey One = new("id-1", "tenant-1");
    private static readonly TenantKey Two = new("id-2", "tenant-2");

    private static async Task<TestModel> TwoTenantsAsync()
    {
        var state = new AppState
        {
            Identities = [Sample.Identity("id-1"), Sample.Identity("id-2")],
            Tenants = [Sample.Tenant("id-1", "tenant-1"), Sample.Tenant("id-2", "tenant-2")],
        };
        return await TestModel.BootstrappedAsync(state);
    }

    private static ActiveAssignment Started(RoleKey key, double endsInMinutes, AssignmentStatus? status = null) =>
        new(key, "a", Now.AddHours(-1), Now.AddMinutes(endsInMinutes), status ?? AssignmentStatus.Active);

    private static RoleKey EntraIn(string identity, string tenant) =>
        Sample.Key(new EntraDirectoryScope("r-" + tenant, "/"), identity, tenant);

    private static ApprovalRequest Request(string id, TenantKey tenant) =>
        new(id, tenant, RoleScopeKind.EntraDirectory, ApprovalAction.Activate, "Reader", "Ann");

    private static void Seed(AppModel model, ApprovalRequest request) =>
        model.Approvals[request.TenantKey] = new() { [RoleScopeKind.EntraDirectory] = [request] };

    [Fact]
    public async Task ActiveNowListsEveryKindWhateverTheTab()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        foreach (var key in new[] { Sample.EntraKey, Sample.AzureKey, Sample.GroupKey })
        {
            model.Active[key] = Sample.Assignment(key);
        }

        foreach (var tab in new[] { PanelTab.Roles, PanelTab.Azure, PanelTab.Groups, PanelTab.All })
        {
            model.PanelTab = tab;
            model.ActiveAssignments(null).Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task ActiveAssignmentsCanBeNarrowedToOneTenant()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        var a = EntraIn("id-1", "tenant-1");
        var b = EntraIn("id-2", "tenant-2");
        model.Active[a] = Sample.Assignment(a);
        model.Active[b] = Sample.Assignment(b);

        model.ActiveAssignments(One).Select(x => x.RoleKey).Should().Equal(a);
        model.ActiveAssignments(Two).Select(x => x.RoleKey).Should().Equal(b);
        model.ActiveAssignments(null).Should().HaveCount(2);
    }

    [Fact]
    public async Task ApprovalsCanBeNarrowedToOneTenant()
    {
        using var test = await TwoTenantsAsync();
        Seed(test.Model, Request("r1", One));
        Seed(test.Model, Request("r2", Two));

        test.Model.ApprovalsIn(One).Select(r => r.Id).Should().Equal("r1");
        test.Model.ApprovalsIn(null).Should().HaveCount(2);
    }

    [Fact]
    public async Task HeroIsTheSoonestExpiryInsideTheExtendWindow()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.Active[Sample.EntraKey] = Started(Sample.EntraKey, 10);

        model.HeroAssignment(Now)!.RoleKey.Should().Be(Sample.EntraKey);
        model.CanExtendNow(Sample.EntraKey, Now).Should().BeTrue();
    }

    [Fact]
    public async Task NoHeroOutsideTheExtendWindow()
    {
        using var test = await TwoTenantsAsync();
        test.Model.Active[Sample.EntraKey] = Started(Sample.EntraKey, 60);

        test.Model.HeroAssignment(Now).Should().BeNull();
    }

    [Fact]
    public async Task NoHeroWhenTheSoonestRowIsPending()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.Active[Sample.EntraKey] = Started(Sample.EntraKey, 100, AssignmentStatus.PendingApproval);

        model.ActiveAssignments(null).First().RoleKey.Should().Be(Sample.EntraKey);
        model.HeroAssignment(Now).Should().BeNull();

        // An active row sorts ahead of the pending one, so it takes the hero slot.
        model.Active[Sample.AzureKey] = Started(Sample.AzureKey, 10);
        model.HeroAssignment(Now)!.RoleKey.Should().Be(Sample.AzureKey);
    }

    [Fact]
    public async Task NoHeroInsideTheDeactivationLock()
    {
        using var test = await TwoTenantsAsync();
        test.Model.Active[Sample.EntraKey] = new(Sample.EntraKey, "a", Now.AddMinutes(-1), Now.AddMinutes(10), AssignmentStatus.Active);

        test.Model.CanExtendNow(Sample.EntraKey, Now).Should().BeFalse();
        test.Model.HeroAssignment(Now).Should().BeNull();
    }

    [Fact]
    public async Task HomeIsEmptyWithNothingActiveAndNoApprovals()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.HomeIsEmpty.Should().BeTrue();
        model.PanelContent.Should().Be(new PanelContent.RoleList());

        model.Active[Sample.EntraKey] = Sample.Assignment(Sample.EntraKey);
        model.HomeIsEmpty.Should().BeFalse();
        model.PanelContent.Should().Be(new PanelContent.Home());
        model.TotalActiveCount.Should().Be(1);
    }

    [Fact]
    public async Task SidebarMarkPrefersActiveCountOverError()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.SidebarMark(One).Should().Be(new TenantMark(TenantMarkKind.None, 0));

        model.TenantErrors[One] = "boom";
        model.SidebarMark(One).Kind.Should().Be(TenantMarkKind.Error);

        var a = EntraIn("id-1", "tenant-1");
        model.Active[a] = Started(a, 100, AssignmentStatus.PendingApproval);
        model.SidebarMark(One).Should().Be(new TenantMark(TenantMarkKind.Active, 1));
        model.SidebarMark(Two).Kind.Should().Be(TenantMarkKind.None);
    }

    [Fact]
    public async Task TheSidebarCountsIgnoreTheFilterAndFailedEntries()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        var a = EntraIn("id-1", "tenant-1");
        var failed = Sample.Key(new EntraDirectoryScope("r-failed", "/"), "id-1", "tenant-1");
        model.Active[a] = Started(a, 100);
        model.Active[failed] = Started(failed, 100, AssignmentStatus.Failed("denied"));
        model.SearchQuery = "zzz-matches-nothing";

        model.TotalActiveCount.Should().Be(1);
        model.SidebarMark(One).Should().Be(new TenantMark(TenantMarkKind.Active, 1));
    }

    [Fact]
    public async Task SelectingAllTenantsLeavesBrowsingAndShowsHome()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.Active[Sample.EntraKey] = Sample.Assignment(Sample.EntraKey);
        model.Browsing = true;
        model.PanelContent.Should().Be(new PanelContent.RoleList());

        model.Select(new PanelSelection.AllTenants());

        model.Browsing.Should().BeFalse();
        model.PanelContent.Should().Be(new PanelContent.Home());
    }
}
