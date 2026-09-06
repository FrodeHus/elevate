using Elevate.Core.Models;
using Elevate.Core.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class ActiveSummaryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    private static RoleKey Key(string n) => new("i", "t", new EntraDirectoryScope(n, "/"));

    private static ActiveAssignment A(string n, AssignmentStatus status, double start = 0, double? end = null) =>
        new(Key(n), n, Now.AddSeconds(start), end is { } e ? Now.AddSeconds(e) : null, status);

    [Fact]
    public void ActiveFirstBySoonestExpiryThenPendingByStart()
    {
        var input = new[]
        {
            A("prov", AssignmentStatus.PendingProvisioning),
            A("late", AssignmentStatus.Active, end: 7200),
            A("pend2", AssignmentStatus.PendingApproval, start: 20),
            A("noend", AssignmentStatus.Active, end: null),
            A("soon", AssignmentStatus.Active, end: 600),
            A("pend1", AssignmentStatus.PendingApproval, start: 10),
            A("failed", AssignmentStatus.Failed("x")),
        };
        ActiveSummary.Order(input).Select(a => a.AssignmentId).Should().Equal("soon", "late", "noend", "pend1", "pend2", "prov");
    }

    [Fact]
    public void ScheduledSortsAfterActiveAndBeforePending()
    {
        var input = new[]
        {
            A("pend", AssignmentStatus.PendingApproval, start: 5),
            A("sched2", AssignmentStatus.Scheduled, start: 200),
            A("active", AssignmentStatus.Active, end: 600),
            A("sched1", AssignmentStatus.Scheduled, start: 100),
            A("prov", AssignmentStatus.PendingProvisioning, start: 7),
        };
        ActiveSummary.Order(input).Select(a => a.AssignmentId).Should().Equal("active", "sched1", "sched2", "pend", "prov");
    }
}

public class ExtendWindowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    private static ActiveAssignment A(AssignmentStatus status, double? end) =>
        new(new RoleKey("i", "t", new EntraDirectoryScope("r", "/")), "x", Now.AddSeconds(-3600),
            end is { } e ? Now.AddSeconds(e) : null, status);

    [Fact]
    public void OfferedOnlyInsideTheWindowWhileActive()
    {
        ExtendWindow.CanExtend(A(AssignmentStatus.Active, 900), RolePolicy.ManualDefault, Now).Should().BeTrue();
        ExtendWindow.CanExtend(A(AssignmentStatus.Active, 1), RolePolicy.ManualDefault, Now).Should().BeTrue();
        ExtendWindow.CanExtend(A(AssignmentStatus.Active, 901), RolePolicy.ManualDefault, Now).Should().BeFalse();
        ExtendWindow.CanExtend(A(AssignmentStatus.Active, 0), RolePolicy.ManualDefault, Now).Should().BeFalse();
        ExtendWindow.CanExtend(A(AssignmentStatus.Active, null), RolePolicy.ManualDefault, Now).Should().BeFalse();
        ExtendWindow.CanExtend(A(AssignmentStatus.PendingApproval, 100), RolePolicy.ManualDefault, Now).Should().BeFalse();
    }

    [Fact]
    public void NeverOfferedWhenTheRoleNeedsApproval()
    {
        var policy = RolePolicy.ManualDefault with { RequiresApproval = true };
        ExtendWindow.CanExtend(A(AssignmentStatus.Active, 300), policy, Now).Should().BeFalse();
    }

    [Fact]
    public void ScheduledNeverExtends()
    {
        ExtendWindow.CanExtend(A(AssignmentStatus.Scheduled, 300), RolePolicy.ManualDefault, Now).Should().BeFalse();
        ExtendWindow.CanExtend(A(AssignmentStatus.Scheduled, 900), RolePolicy.ManualDefault, Now).Should().BeFalse();
    }
}

public class PanelFilterTests
{
    private static readonly EligibleRole Role = new(
        new RoleKey("i", "t", new AzureResourceScope("/subscriptions/s1", "r")),
        "Contributor", RoleSource.Discovered, RolePolicy.ManualDefault,
        Detail: "Pay-As-You-Go · subscription", ViaGroup: "Platform Team");

    [Fact]
    public void EmptyQueryMatchesEverything()
    {
        PanelFilter.Matches("", Role, "Contoso", "u@contoso.com").Should().BeTrue();
        PanelFilter.Matches("   ", Role, "Contoso", "u@contoso.com").Should().BeTrue();
        PanelFilter.IsActive("  ").Should().BeFalse();
        PanelFilter.IsActive(" x").Should().BeTrue();
    }

    [Theory]
    [InlineData("contrib")]
    [InlineData("CONTRIBUTOR")]
    [InlineData("pay-as")]
    [InlineData("platform")]
    [InlineData("contoso")]
    [InlineData("U@CONTOSO")]
    public void MatchesEachFieldCaseInsensitively(string query)
    {
        PanelFilter.Matches(query, Role, "Contoso", "u@contoso.com").Should().BeTrue();
    }

    [Fact]
    public void DoesNotMatchUnrelatedText()
    {
        PanelFilter.Matches("reader", Role, "Contoso", "u@contoso.com").Should().BeFalse();
    }

    [Fact]
    public void TextOverloadTrimsWhitespaceAndNewlines()
    {
        PanelFilter.Matches("owner\n", "Group owner").Should().BeTrue();
        PanelFilter.Matches(" \n ", "anything").Should().BeTrue();
        PanelFilter.Matches("reader\n", "Group owner").Should().BeFalse();
    }

    [Fact]
    public void IgnoresDiacritics()
    {
        var role = Role with { DisplayName = "Sécurité" };
        PanelFilter.Matches("securite", role, "", "").Should().BeTrue();
    }
}

public class PanelStatusTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    private static RoleKey Key(string n) => new("i", "t", new EntraDirectoryScope(n, "/"));

    private static ActiveAssignment Assignment(string n, AssignmentStatus status, double? endsIn = 3600) =>
        new(Key(n), n, Now.AddSeconds(-600), endsIn is { } e ? Now.AddSeconds(e) : null, status);

    [Fact]
    public void EmptyIsIdle()
    {
        PanelStatus.Compute([], Now).Should().Be(new PanelStatus(0, false, false));
    }

    [Fact]
    public void CountsActiveOnly()
    {
        var s = PanelStatus.Compute(
        [
            Assignment("a", AssignmentStatus.Active),
            Assignment("b", AssignmentStatus.PendingApproval),
            Assignment("c", AssignmentStatus.PendingProvisioning),
            Assignment("d", AssignmentStatus.Failed("x")),
        ], Now);
        s.ActiveCount.Should().Be(1);
        s.PendingApproval.Should().BeTrue();
        s.ExpiringSoon.Should().BeFalse();
    }

    [Fact]
    public void ExpiringSoonBoundary()
    {
        PanelStatus.Compute([Assignment("a", AssignmentStatus.Active, 300)], Now).ExpiringSoon.Should().BeTrue();
        PanelStatus.Compute([Assignment("a", AssignmentStatus.Active, 301)], Now).ExpiringSoon.Should().BeFalse();
        PanelStatus.Compute([Assignment("a", AssignmentStatus.Active, -1)], Now).ExpiringSoon.Should().BeFalse();
        PanelStatus.Compute([Assignment("a", AssignmentStatus.Active, null)], Now).ExpiringSoon.Should().BeFalse();
        PanelStatus.Compute([Assignment("a", AssignmentStatus.PendingApproval, 10)], Now).ExpiringSoon.Should().BeFalse();
    }

    [Fact]
    public void ScheduledIsPendingAndNotActive()
    {
        var s = PanelStatus.Compute([Assignment("a", AssignmentStatus.Scheduled, 60)], Now);
        s.ActiveCount.Should().Be(0);
        s.PendingApproval.Should().BeTrue();
        s.ExpiringSoon.Should().BeFalse();
    }
}
