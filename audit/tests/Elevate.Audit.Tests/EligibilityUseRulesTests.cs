using Elevate.Audit.Model;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class EligibilityUseRulesTests
{
    private const string Owner = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
    private const string Since = "2026-03-17T12:00:00Z";
    private const string Until = "2026-09-13T12:00:00Z";

    private static IReadOnlyList<Finding> Run(Snapshot snapshot, AuditOptions? options = null) =>
        RuleRunner.Run(snapshot, options ?? new AuditOptions(), [new EligibleNeverActivatedRule(), new EligibleDormantRule(), new EligibleOrphanedRule()]);

    private static SnapshotBuilder Tenant() => SnapshotBuilder.Contoso()
        .User("u1", "Sam Chen", "sam.chen@contoso.com")
        .User("u2", "Jordan Lee", "jordan.lee@contoso.com")
        .Group("g1", "Tier 0 Admins");

    [Fact]
    public void NeverActivated_FiresForAnOldEligibility_AcrossAllThreeSystems()
    {
        var snapshot = Tenant()
            .Eligible("e1", "u1", "rd-ga")
            .GroupPim("g1", "ge1", "u1", eligible: true)
            .AzureEligible("ae1", "/subscriptions/sub1", Owner, "u1", "User")
            .History(Since, Until)
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "ELIGIBLE-NEVER-ACTIVATED").ToList();

        findings.Should().HaveCount(3);
        findings.Select(f => f.Role.System).Should().BeEquivalentTo([RoleSystem.Entra, RoleSystem.Group, RoleSystem.Azure]);
        findings.Should().OnlyContain(f => f.Severity == Severity.Medium);
        findings[0].Remedy.Should().Contain("never activated in the 180 days examined").And.Contain("access review");
        findings[0].Evidence.LastActivatedAt.Should().BeNull();
        findings[0].Evidence.AgeDays.Should().Be(620);
    }

    [Fact]
    public void NeverActivated_IsSilentForAnEligibilityGrantedInsideTheWindow_BecauseRetentionCannotProveDisuse()
    {
        var snapshot = Tenant()
            .Eligible("e1", "u1", "rd-ga")
            .History("2024-06-15T12:00:00Z", Until)
            .Build();

        // The fixture grants every eligibility on 2025-01-01, which is inside this longer window.
        Run(snapshot).Should().NotContain(f => f.Id == "ELIGIBLE-NEVER-ACTIVATED");
    }

    [Fact]
    public void NeverActivated_IsSilentWhenTheHistoryForThatSystemWasNotReadable()
    {
        var snapshot = Tenant()
            .Eligible("e1", "u1", "rd-ga")
            .AzureEligible("ae1", "/subscriptions/sub1", Owner, "u1", "User")
            .History(Since, Until, RoleSystem.Entra, RoleSystem.Group)
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "ELIGIBLE-NEVER-ACTIVATED").ToList();

        findings.Should().ContainSingle().Which.Role.System.Should().Be(RoleSystem.Entra);
    }

    [Fact]
    public void NoHistoryAtAll_LeavesOnlyTheOrphanRule()
    {
        var snapshot = Tenant()
            .User("u3", "Taylor Kim", "taylor.kim@contoso.com", enabled: false)
            .Eligible("e1", "u1", "rd-ga")
            .Eligible("e2", "u3", "rd-ga")
            .Build();

        Run(snapshot).Select(f => f.Id).Should().Equal("ELIGIBLE-ORPHANED");
    }

    [Fact]
    public void Dormant_FiresPastTheThreshold_AndIsSilentBeforeIt()
    {
        var snapshot = Tenant()
            .Eligible("e1", "u1", "rd-ga")
            .Eligible("e2", "u2", "rd-ga")
            .History(Since, Until)
            .Activated("2026-04-20T08:00:00Z", "u1", RoleSystem.Entra, null, "rd-ga")
            .Activated("2026-09-01T08:00:00Z", "u2", RoleSystem.Entra, null, "rd-ga")
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "ELIGIBLE-DORMANT").ToList();

        var dormant = findings.Should().ContainSingle().Subject;
        dormant.Principal.Id.Should().Be("u1");
        dormant.Severity.Should().Be(Severity.Medium);
        dormant.Remedy.Should().Contain("Last activated 2026-04-20, 146 days ago");
        dormant.Evidence.LastActivatedAt.Should().Be(DateTimeOffset.Parse("2026-04-20T08:00:00Z"));
    }

    [Fact]
    public void Dormant_HonoursDormantAfterDays()
    {
        var snapshot = Tenant()
            .Eligible("e1", "u1", "rd-ga")
            .History(Since, Until)
            .Activated("2026-08-01T08:00:00Z", "u1", RoleSystem.Entra, null, "rd-ga")
            .Build();

        Run(snapshot).Should().NotContain(f => f.Id == "ELIGIBLE-DORMANT");
        Run(snapshot, new AuditOptions(DormantAfterDays: 30)).Should().ContainSingle(f => f.Id == "ELIGIBLE-DORMANT");
    }

    [Fact]
    public void ActivationMatches_OnAnyRoleKey_AndOnScopeWhenBothCarryOne()
    {
        var snapshot = Tenant()
            .Eligible("e1", "u1", "rd-ga")
            .AzureEligible("ae1", "/subscriptions/sub1", Owner, "u1", "User")
            .History(Since, Until)
            // The audit log named the role by display name, not by definition id.
            .Activated("2026-09-10T08:00:00Z", "u1", RoleSystem.Entra, null, "Global Administrator")
            // An Owner activation at a different scope is not use of the subscription-scoped eligibility.
            .Activated("2026-09-10T08:00:00Z", "u1", RoleSystem.Azure, "/subscriptions/sub2", Owner)
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "ELIGIBLE-NEVER-ACTIVATED").ToList();

        findings.Should().ContainSingle().Which.Role.System.Should().Be(RoleSystem.Azure);
    }

    [Fact]
    public void AnEligibilityActivatedRightNow_CountsAsUsed_EvenWithNoAuditEntry()
    {
        var snapshot = Tenant()
            .Eligible("e1", "u1", "rd-ga")
            .Assigned("a1", "u1", "rd-ga", type: AssignmentType.Activated, end: DateTimeOffset.Parse("2026-09-13T20:00:00Z"))
            .History(Since, Until)
            .Build();

        Run(snapshot).Should().BeEmpty();
    }

    [Fact]
    public void Orphaned_CoversDisabled_BlockedGuest_AndDeleted_AndSuppressesTheOtherTwoRules()
    {
        var snapshot = Tenant()
            .User("u-off", "Taylor Kim", "taylor.kim@contoso.com", enabled: false)
            .User("u-guest", "Priya Natarajan", "priya@fabrikam.com", guest: true, enabled: false)
            .Eligible("e1", "u-off", "rd-ga")
            .Eligible("e2", "u-guest", "rd-ga")
            .Eligible("e3", "u-gone", "rd-ga")
            .History(Since, Until)
            .Build();

        var findings = Run(snapshot);

        findings.Should().HaveCount(3, "each orphaned eligibility is reported once, not also as never activated");
        findings.Should().OnlyContain(f => f.Id == "ELIGIBLE-ORPHANED" && f.Severity == Severity.High);
        findings.Single(f => f.Principal.Id == "u-off").Remedy.Should().Contain("account is disabled");
        findings.Single(f => f.Principal.Id == "u-guest").Remedy.Should().Contain("guest whose account is blocked");
        findings.Single(f => f.Principal.Id == "u-gone").Remedy.Should().Contain("no longer resolves in the directory");
    }

    [Fact]
    public void Orphaned_DoesNotCallAnUnresolvedPrincipalDeleted_WhenPrincipalResolutionItselfWasSkipped()
    {
        var snapshot = Tenant()
            .Eligible("e1", "u-gone", "rd-ga")
            .Skipped("principals", "Some principals could not be resolved to names.")
            .History(Since, Until)
            .Build();

        Run(snapshot).Should().NotContain(f => f.Id == "ELIGIBLE-ORPHANED");
    }

    [Fact]
    public void AllRoles_BringsInUnprivilegedRoles_AtLowSeverity()
    {
        var snapshot = Tenant()
            .Eligible("e1", "u1", "rd-reader")
            .History(Since, Until)
            .Build();

        Run(snapshot).Should().BeEmpty("Global Reader is not privileged");
        Run(snapshot, new AuditOptions(AllRoles: true)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Id = "ELIGIBLE-NEVER-ACTIVATED", Severity = Severity.Low });
    }
}
