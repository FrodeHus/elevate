using Elevate.App.Tests.Support;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.App.Tests;

public class AppModelPanelBlocksTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly TenantKey One = new("id-1", "tenant-1");
    private static readonly TenantKey Two = new("id-2", "tenant-2");

    /// <summary>Two accounts with one tenant each: Global Reader in the first, Billing Admin in the second.</summary>
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
        return test;
    }

    private static RoleKey KeyOf(TestModel test, TenantKey tenant) => test.Model.Roles[tenant].Single().Key;

    private static ActiveAssignment Started(RoleKey key, double endsInMinutes) =>
        new(key, "a", Now.AddHours(-1), Now.AddMinutes(endsInMinutes), AssignmentStatus.Active);

    private static PanelBlockKind[] Kinds(TestModel test) => [.. test.Model.PanelBlocks(Now).Select(b => b.Kind)];

    [Fact]
    public async Task HomeListsApprovalsActiveNowWithTheHeroAndBrowseAll()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.Approvals[One] = new()
        {
            [RoleScopeKind.EntraDirectory] = [new ApprovalRequest("r1", One, RoleScopeKind.EntraDirectory, ApprovalAction.Activate, "Reader", "Ann")],
        };
        var soon = KeyOf(test, One);
        model.Active[soon] = Started(soon, 10);

        var blocks = model.PanelBlocks(Now);

        blocks.Select(b => b.Kind).Should().Equal(PanelBlockKind.Approvals, PanelBlockKind.ActiveNow, PanelBlockKind.BrowseAll);
        blocks[1].Hero.Should().Be(soon);
        blocks[2].Should().Be(new PanelBlock(PanelBlockKind.BrowseAll, 2, 2));
    }

    [Fact]
    public async Task HomeHasNoHeroOutsideTheExtendWindow()
    {
        using var test = await TwoTenantsAsync();
        var key = KeyOf(test, One);
        test.Model.Active[key] = Started(key, 120);

        test.Model.PanelBlocks(Now).Single(b => b.Kind == PanelBlockKind.ActiveNow).Hero.Should().BeNull();
    }

    [Fact]
    public async Task NothingActiveShowsTheCardThenTheListThenTheSelectRolesHint()
    {
        using var test = await TwoTenantsAsync();

        Kinds(test).Should().Equal(PanelBlockKind.EmptyCard, PanelBlockKind.Eligible, PanelBlockKind.SelectRoles);
        test.Model.PanelBlocks(Now)[1].Count.Should().Be(2);

        test.Model.SaveProfile("Ops", [KeyOf(test, One)]);
        Kinds(test).Should().Equal(PanelBlockKind.EmptyCard, PanelBlockKind.Eligible);
    }

    [Fact]
    public async Task BrowsingTheListHasNoCardNoApprovalsAndNoActiveNow()
    {
        using var test = await TwoTenantsAsync();
        var key = KeyOf(test, One);
        test.Model.Active[key] = Started(key, 120);
        test.Model.Browsing = true;

        Kinds(test).Should().Equal(PanelBlockKind.Eligible);
    }

    [Fact]
    public async Task TheAllTenantsListCountsOnlyWhatTheFilterAndTabLeave()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.SearchQuery = "billing";
        model.PanelBlocks(Now).Single(b => b.Kind == PanelBlockKind.Eligible).Count.Should().Be(1);

        model.SearchQuery = "zzz";
        model.PanelBlocks(Now).Single().Should().Be(new PanelBlock(PanelBlockKind.Eligible, 0, NoMatches: true));
    }

    [Fact]
    public async Task ATenantViewHoldsItsOwnApprovalsActiveRowsAndEligibleCount()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        var other = KeyOf(test, Two);
        model.Active[other] = Started(other, 10);
        model.Select(new PanelSelection.Tenant(One));

        // The other tenant's active role stays out, and the soonest row is never a card here.
        Kinds(test).Should().Equal(PanelBlockKind.Eligible);

        var mine = KeyOf(test, One);
        model.Active[mine] = Started(mine, 10);
        var blocks = model.PanelBlocks(Now);
        blocks.Select(b => b.Kind).Should().Equal(PanelBlockKind.ActiveNow, PanelBlockKind.Eligible);
        blocks[0].Should().Be(new PanelBlock(PanelBlockKind.ActiveNow, 1));
        blocks[1].Count.Should().Be(1);
    }

    [Fact]
    public async Task ATenantViewFilterWithNoMatchOffersTheOtherTenants()
    {
        using var test = await TwoTenantsAsync();
        var model = test.Model;
        model.Select(new PanelSelection.Tenant(One));
        model.SearchQuery = "billing";

        var blocks = model.PanelBlocks(Now);

        blocks.Should().Equal(
            new PanelBlock(PanelBlockKind.Eligible, 0, NoMatches: true),
            new PanelBlock(PanelBlockKind.SearchAllTenants, 1));
    }

    [Fact]
    public async Task AProfileHasNoList()
    {
        using var test = await TwoTenantsAsync();
        var profile = test.Model.SaveProfile("Ops", [KeyOf(test, One)])!;
        test.Model.Select(new PanelSelection.Profile(profile.Id));

        test.Model.PanelBlocks(Now).Should().BeEmpty();
    }
}
