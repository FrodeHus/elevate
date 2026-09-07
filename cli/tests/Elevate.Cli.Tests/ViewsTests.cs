using System.Text.Json;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Rendering;
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
}
