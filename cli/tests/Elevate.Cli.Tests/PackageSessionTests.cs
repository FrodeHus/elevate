using Elevate.Cli.Commands;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Selection;
using Elevate.Cli.Session;
using Elevate.Cli.Tests.Support;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.Cli.Tests;

public class PackageSessionTests
{
    private static readonly TenantKey Home = TestSession.Tenant.Key;

    [Fact]
    public async Task ReadMarksTheTenantAvailableAndReturnsAllThreeLists()
    {
        using var t = new TestSession();
        t.Packages.Packages.Add(new AccessPackage("pkg-sandbox", "Azure Sandbox Contributor", "30 days."));
        t.Packages.Requests.Add(new AccessPackageRequest("req-1", "pkg-sandbox", "Azure Sandbox Contributor", "userAdd", AccessPackageRequestState.PendingApproval));
        t.Packages.Assignments.Add(new AccessPackageAssignment("asg-1", "pkg-exchange", "Exchange Operations", AccessPackageAssignmentState.Delivered, "On-call staff"));

        var read = await t.Session.ReadPackagesAsync(Home, includePackages: true, CancellationToken.None);

        read.Packages.Should().ContainSingle();
        read.Requests.Should().ContainSingle();
        read.Assignments.Should().ContainSingle();
        t.Session.Tenant(Home)!.AccessPackagesAvailable.Should().BeTrue();
        new AppStateStore(t.Directory).Load().Tenants.Single().AccessPackagesAvailable.Should().BeTrue("the flag is persisted");
    }

    [Fact]
    public async Task ReadWithoutPackagesSkipsThePackageList()
    {
        using var t = new TestSession();
        t.Packages.Packages.Add(new AccessPackage("pkg-sandbox", "Azure Sandbox Contributor"));

        var read = await t.Session.ReadPackagesAsync(Home, includePackages: false, CancellationToken.None);

        read.Packages.Should().BeEmpty();
    }

    [Fact]
    public async Task ConsentRefusalMarksTheTenantUnavailableAndThrows()
    {
        using var t = new TestSession();
        t.Packages.ReadError = new PimException(PimErrorKind.ConsentRequired);

        var act = () => t.Session.ReadPackagesAsync(Home, includePackages: true, CancellationToken.None);

        await act.Should().ThrowAsync<PimException>();
        t.Session.Tenant(Home)!.AccessPackagesAvailable.Should().BeFalse();
        t.Session.AccessPackagesUnavailableReason(Home).Should().NotBeNull();
        t.Session.AccessPackageTenants(null, null).Should().BeEmpty();
    }

    [Fact]
    public void FirstPartyAccountsAreNeverEligible()
    {
        using var t = new TestSession();
        t.Session.State.Identities[0] = TestSession.Account with { SignInMethod = SignInMethod.AzureCLI };

        t.Session.AccessPackagesUnavailableReason(Home).Should().Contain("Azure CLI");
        t.Session.AccessPackageTenants(null, null).Should().BeEmpty();
    }

    [Fact]
    public void TenantFiltersNarrowTheCandidates()
    {
        using var t = new TestSession();
        t.Session.State.UpsertTenant(new TenantContext("id1", "t2", "Fabrikam", TenantSource.Manual));

        t.Session.AccessPackageTenants(null, null).Select(k => k.TenantId).Should().Equal("t1", "t2");
        t.Session.AccessPackageTenants(null, "fabrikam").Select(k => k.TenantId).Should().Equal("t2");
        t.Session.AccessPackageTenants("alex", "t1").Select(k => k.TenantId).Should().Equal("t1");
        t.Session.AccessPackageTenants("nobody", null).Should().BeEmpty();
    }

    [Fact]
    public async Task RequestAndCancelGoThroughTheProvider()
    {
        using var t = new TestSession();
        t.Packages.Packages.Add(new AccessPackage("pkg-sandbox", "Azure Sandbox Contributor"));
        t.Packages.Requirements["pkg-sandbox"] = [new PolicyRequirement("pol-eng", "Engineers", null, true, false)];

        var requirements = await t.Session.PackageRequirementsAsync(Home, "pkg-sandbox", CancellationToken.None);
        requirements.Should().ContainSingle().Which.Id.Should().Be("pol-eng");

        var created = await t.Session.RequestPackageAsync(Home, "pkg-sandbox", "pol-eng", "Need it", CancellationToken.None);
        created.State.Should().Be(AccessPackageRequestState.Submitted);
        t.Packages.Requested.Should().Equal(("pkg-sandbox", "pol-eng", "Need it"));

        await t.Session.CancelPackageRequestAsync(Home, created.Id, CancellationToken.None);
        t.Packages.Cancelled.Should().Equal("req-new");
    }

    [Fact]
    public async Task ProbeIsFalseForFirstPartyAndNullForOpaqueTokens()
    {
        using var t = new TestSession();
        var cli = TestSession.Account with { SignInMethod = SignInMethod.AzureCLI };
        (await t.Session.ProbeAccessPackagesAsync(cli, "t1")).Should().BeFalse();
        // The fake token provider hands out the opaque string "token".
        (await t.Session.ProbeAccessPackagesAsync(TestSession.Account, "t1")).Should().BeNull();
    }

    [Fact]
    public void ResolvePackageMatchesShortIdThenNameThenSubstring()
    {
        using var t = new TestSession();
        var read = new TenantPackages(Home, [new AccessPackage("pkg-sandbox", "Azure Sandbox Contributor"), new AccessPackage("pkg-exchange", "Exchange Operations")], [], []);

        PackageCommands.ResolvePackage(t.Session, [read], ShortId.For(Home, "pkg-exchange")).Package.Id.Should().Be("pkg-exchange");
        PackageCommands.ResolvePackage(t.Session, [read], "pkg-sandbox").Package.Id.Should().Be("pkg-sandbox");
        PackageCommands.ResolvePackage(t.Session, [read], "exchange operations").Package.Id.Should().Be("pkg-exchange");
        PackageCommands.ResolvePackage(t.Session, [read], "sandbox").Package.Id.Should().Be("pkg-sandbox");
        var none = () => PackageCommands.ResolvePackage(t.Session, [read], "nothing");
        none.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.NotFound);
        var many = () => PackageCommands.ResolvePackage(t.Session, [read], "e");
        many.Should().Throw<CliException>().Which.Message.Should().Contain("several");
    }

    [Fact]
    public void ChoosePolicyHandlesNoneOneAndSeveral()
    {
        var eng = new PolicyRequirement("pol-eng", "Engineers", null, true, false);
        var lead = new PolicyRequirement("pol-lead", "Team leads", null, false, false);

        var none = () => PackageCommands.ChoosePolicy([], null, "P");
        none.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.Failure);
        PackageCommands.ChoosePolicy([eng], null, "P").Should().Be(eng);
        var several = () => PackageCommands.ChoosePolicy([eng, lead], null, "P");
        several.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.Usage);
        PackageCommands.ChoosePolicy([eng, lead], "pol-lead", "P").Should().Be(lead);
        PackageCommands.ChoosePolicy([eng, lead], "team", "P").Should().Be(lead);
        var missing = () => PackageCommands.ChoosePolicy([eng, lead], "nope", "P");
        missing.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.NotFound);
    }

    [Fact]
    public async Task TenantsRetryClearsTheAccessPackagesLatch()
    {
        using var t = new TestSession();
        t.Packages.ReadError = new PimException(PimErrorKind.ConsentRequired);
        var act = () => t.Session.ReadPackagesAsync(Home, includePackages: false, CancellationToken.None);
        await act.Should().ThrowAsync<PimException>();
        t.Session.Tenant(Home)!.AccessPackagesAvailable.Should().BeFalse();

        t.Session.ResetTenant(Home);

        t.Session.Tenant(Home)!.AccessPackagesAvailable.Should().BeNull();
        t.Session.AccessPackageTenants(null, null).Should().Equal(Home);
    }

    [Fact]
    public void TenantMatchesFilterHonoursAccountAndTenantFilters()
    {
        using var t = new TestSession();

        t.Session.TenantMatchesFilter(TestSession.Tenant, null, "fabrikam").Should().BeFalse();
        t.Session.TenantMatchesFilter(TestSession.Tenant, "alex", "t1").Should().BeTrue();
    }
}
