using System.Text.Json;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Rendering;
using Elevate.Cli.Selection;
using Elevate.Cli.Session;
using Elevate.Cli.Tests.Support;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Cli.Tests;

public class ViewsTests
{
    [Fact]
    public async Task RoleDtoCarriesStatusAndStableFields()
    {
        using var t = new TestSession();
        var policy = RolePolicy.ManualDefault with { RequiresApproval = true, AuthenticationContext = "c1" };
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader", policy));
        var now = DateTimeOffset.UtcNow;
        t.Entra.Assignments.Add(new ActiveAssignment(TestSession.EntraKey("r1"), "a", now, now.AddMinutes(90), AssignmentStatus.Active));
        await t.Session.RefreshAllAsync();

        var dto = Views.Role(t.Session, t.Session.Roles[TestSession.Tenant.Key][0], now);
        dto.Kind.Should().Be("entra");
        dto.Tenant.Should().Be("Contoso");
        dto.Account.Should().Be("alex@contoso.com");
        dto.Policy.RequiresApproval.Should().BeTrue();
        dto.Policy.AuthenticationContext.Should().Be("c1");
        dto.Policy.DefaultDuration.Should().Be("PT1H");
        dto.Assignment!.Status.Should().Be("active");
        dto.Assignment.TimeLeft.Should().Be("1 h 30 min");
        dto.CanActivate.Should().BeTrue();

        var json = JsonSerializer.Serialize(dto, Output.JsonOptions);
        json.Should().Contain("\"kind\": \"entra\"").And.Contain("\"key\"").And.Contain("entraDirectory");
    }

    [Fact]
    public async Task FirstPartyAccountIsViewOnlyForEntra()
    {
        using var t = new TestSession();
        var cli = TestSession.Account with { SignInMethod = SignInMethod.AzureCLI };
        t.Session.State.Identities[0] = cli;
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        await t.Session.RefreshAllAsync();

        // A first-party sign-in never reads Entra at all.
        t.Session.Roles[TestSession.Tenant.Key].Should().BeEmpty();
        t.Session.CanActivate(TestSession.EntraKey("r1")).Should().BeFalse();
        Views.TenantFlags(t.Session, TestSession.Tenant).Should().Contain("azure roles only");
    }

    [Fact]
    public void StatusMarkupWarnsNearExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var soon = new ActiveAssignment(TestSession.EntraKey("r1"), "a", now, now.AddMinutes(3), AssignmentStatus.Active);
        Views.StatusMarkup(soon, now).Should().StartWith("[yellow]active[/]");
        var later = soon with { EndDateTime = now.AddHours(2) };
        Views.StatusMarkup(later, now).Should().StartWith("[green]active[/]");
        Views.StatusMarkup(null, now).Should().Contain("eligible");
        Views.StatusMarkup(soon with { Status = AssignmentStatus.PendingApproval }, now).Should().Contain("awaiting approval");
    }

    private static readonly TenantKey Home = TestSession.Tenant.Key;

    [Fact]
    public void ShortIdForATenantScopedIdIsStableAndDistinct()
    {
        var a = ShortId.For(Home, "req-1");
        a.Should().HaveLength(8);
        a.Should().Be(ShortId.For(Home, "req-1"));
        a.Should().NotBe(ShortId.For(new TenantKey("id1", "t2"), "req-1"));
        ShortId.LooksLikeId(a).Should().BeTrue();
    }

    [Fact]
    public void PackageStateComesFromAssignmentsThenOpenRequests()
    {
        var read = new TenantPackages(Home,
            [new AccessPackage("p1", "One"), new AccessPackage("p2", "Two"), new AccessPackage("p3", "Three"), new AccessPackage("p4", "Four")],
            [
                new AccessPackageRequest("r1", "p1", "One", "userAdd", AccessPackageRequestState.PendingApproval, CreatedAt: DateTimeOffset.UtcNow),
                new AccessPackageRequest("r2", "p2", "Two", "userAdd", AccessPackageRequestState.Denied),
                new AccessPackageRequest("r3", "p3", "Three", "userAdd", AccessPackageRequestState.PendingApproval),
            ],
            [new AccessPackageAssignment("a3", "p3", "Three", AccessPackageAssignmentState.Delivered)]);

        Views.PackageState(read, "p1").Should().Be(("pendingApproval", "r1", null));
        Views.PackageState(read, "p2").Should().Be((null, null, null));
        Views.PackageState(read, "p3").Should().Be(("delivered", null, "a3"));
        Views.PackageState(read, "p4").Should().Be((null, null, null));
    }

    [Fact]
    public void PackageDtosCarryTenantAccountAndStableIds()
    {
        using var t = new TestSession();
        var now = DateTimeOffset.UtcNow;
        var request = new AccessPackageRequest("req-1", "pkg-sandbox", "Azure Sandbox Contributor", "userAdd", AccessPackageRequestState.PendingApproval,
            "PendingApproval", "Need it", now.AddHours(-1), null, "pol-eng");
        var assignment = new AccessPackageAssignment("asg-1", "pkg-exchange", "Exchange Operations", AccessPackageAssignmentState.Delivered, "On-call staff", now.AddDays(3));
        var read = new TenantPackages(Home, [new AccessPackage("pkg-sandbox", "Azure Sandbox Contributor", "30 days.", IsHidden: true)], [request], [assignment]);

        var package = Views.Package(t.Session, read, read.Packages[0]);
        package.Id.Should().Be(ShortId.For(Home, "pkg-sandbox"));
        package.Tenant.Should().Be("Contoso");
        package.Account.Should().Be("alex@contoso.com");
        package.Hidden.Should().BeTrue();
        package.State.Should().Be("pendingApproval");
        package.RequestId.Should().Be("req-1");

        var dto = Views.PackageRequest(t.Session, Home, request);
        dto.Id.Should().Be(ShortId.For(Home, "req-1"));
        dto.RequestId.Should().Be("req-1");
        dto.State.Should().Be("pendingApproval");
        dto.Cancellable.Should().BeTrue();
        dto.PolicyId.Should().Be("pol-eng");

        var asg = Views.PackageAssignment(t.Session, Home, assignment, now);
        asg.Id.Should().Be(ShortId.For(Home, "asg-1"));
        asg.State.Should().Be("delivered");
        asg.Policy.Should().Be("On-call staff");
        asg.ExpiresIn.Should().Be("72 h");

        var json = JsonSerializer.Serialize(dto, Output.JsonOptions);
        json.Should().Contain("\"state\": \"pendingApproval\"").And.Contain("\"cancellable\": true");
    }

    [Fact]
    public void PackageTablesRenderWithoutThrowing()
    {
        using var t = new TestSession();
        var now = DateTimeOffset.UtcNow;
        var request = new AccessPackageRequest("req-1", "pkg-sandbox", "Azure Sandbox Contributor", "userAdd", AccessPackageRequestState.Canceled, "Canceled by requestor", "Need it", now.AddDays(-1), now);
        var assignment = new AccessPackageAssignment("asg-1", "pkg-exchange", "Exchange Operations", AccessPackageAssignmentState.Delivered, null, null);
        var read = new TenantPackages(Home, [new AccessPackage("pkg-sandbox", "Azure Sandbox Contributor", null, IsHidden: true)], [request], [assignment]);

        Views.PackagesTable(t.Session, [read], showAccount: true).Rows.Count.Should().Be(1);
        Views.PackageRequestsTable(t.Session, [(Home, request)], now, all: true, showAccount: false).Rows.Count.Should().Be(1);
        Views.PackageAssignmentsTable(t.Session, [(Home, assignment)], now, showAccount: false).Rows.Count.Should().Be(1);
        Views.RequestStateMarkup(AccessPackageRequestState.Denied).Should().StartWith("[red]");
        Views.RequestStateMarkup(AccessPackageRequestState.Delivered).Should().StartWith("[green]");
        Views.RequestStateMarkup(AccessPackageRequestState.Submitted).Should().StartWith("[yellow]");
        Views.RequestStateMarkup(AccessPackageRequestState.Canceled).Should().StartWith("[grey]");
    }
}
