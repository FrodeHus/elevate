using Elevate.Audit.Model;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class EntraRulesTests
{
    private static IReadOnlyList<Finding> Run(Snapshot snapshot, AuditOptions? options = null) =>
        RuleRunner.Run(snapshot, options ?? new AuditOptions(), [new EntraUserPermanentRule(), new EntraGroupPermanentRule(), new EntraGroupNotPimRule(), new EntraGroupNotAssignableRule()]);

    [Fact]
    public void DirectPermanentUser_IsHigh_ActivatedAndTimeBoundAreNot()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .User("u2", "Jordan Lee", "jordan.lee@contoso.com")
            .User("u3", "Priya Natarajan", "priya.natarajan@contoso.com")
            .Assigned("a1", "u1", "rd-ga")
            .Assigned("a2", "u2", "rd-ga", type: AssignmentType.Activated, end: DateTimeOffset.Parse("2026-09-13T16:00:00Z"))
            .Assigned("a3", "u3", "rd-ga", end: DateTimeOffset.Parse("2026-12-31T00:00:00Z"))
            .Assigned("a4", "u1", "rd-reader")
            .Build();

        var findings = Run(snapshot);

        var f = findings.Should().ContainSingle().Subject;
        f.Id.Should().Be("ENTRA-USER-PERMANENT");
        f.Severity.Should().Be(Severity.High);
        f.Principal.DisplayName.Should().Be("Sam Chen");
        f.Role.DisplayName.Should().Be("Global Administrator");
        f.Scope.Kind.Should().Be(ScopeKind.Directory);
        f.Via.Should().BeEmpty();
        f.Remedy.Should().Contain("eligible");
        f.PortalUrl.Should().StartWith("https://entra.microsoft.com/");
        f.Evidence.AssignmentId.Should().Be("a1");
    }

    [Fact]
    public void AllRoles_IncludesNonPrivilegedRoles()
    {
        var snapshot = SnapshotBuilder.Contoso().User("u1", "Sam Chen", "sam.chen@contoso.com").Assigned("a4", "u1", "rd-reader").Build();

        Run(snapshot, new AuditOptions(AllRoles: true)).Should().ContainSingle().Which.Role.DisplayName.Should().Be("Global Reader");
    }

    [Fact]
    public void PermanentGroup_YieldsTheGroupAndEachNestedUserWithItsPath()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .User("u2", "Jordan Lee", "jordan.lee@contoso.com")
            .Group("g1", "Tier 0 Admins", pim: PimStatus.Onboarded, members: [("u1", PrincipalType.User), ("g2", PrincipalType.Group)])
            .Group("g2", "Platform Team", assignable: false, members: [("u2", PrincipalType.User), ("g1", PrincipalType.Group)])
            .Assigned("a1", "g1", "rd-pra")
            .Assigned("a1-u1", "u1", "rd-pra", memberType: "Group")
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "ENTRA-GROUP-PERMANENT").ToList();

        findings.Should().HaveCount(3);
        findings.Should().ContainSingle(f => f.Principal.Type == PrincipalType.Group).Which.Principal.DisplayName.Should().Be("Tier 0 Admins");
        findings.Single(f => f.Principal.Id == "u1").Via.Select(v => v.DisplayName).Should().Equal("Tier 0 Admins");
        findings.Single(f => f.Principal.Id == "u2").Via.Select(v => v.DisplayName).Should().Equal("Tier 0 Admins", "Platform Team");
        Run(snapshot).Should().NotContain(f => f.Id == "ENTRA-USER-PERMANENT", "the per-member echo (memberType Group) is not a direct assignment");
    }

    [Fact]
    public void GroupNotOnboarded_AndNotAssignable_AreMedium()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .Group("g1", "Tier 0 Admins", pim: PimStatus.NotOnboarded)
            .Group("g2", "Legacy Ops", assignable: false, pim: PimStatus.NotOnboarded)
            .Group("g3", "Fine", pim: PimStatus.Onboarded)
            .Assigned("a1", "g1", "rd-ga")
            .Assigned("a2", "g2", "rd-ga")
            .Assigned("a3", "g3", "rd-ga")
            .Build();

        var findings = Run(snapshot);

        findings.Should().ContainSingle(f => f.Id == "ENTRA-GROUP-NOT-PIM").Which.Principal.Id.Should().Be("g1");
        findings.Should().ContainSingle(f => f.Id == "ENTRA-GROUP-NOT-ASSIGNABLE").Which.Principal.Id.Should().Be("g2");
        findings.Where(f => f.Id is "ENTRA-GROUP-NOT-PIM" or "ENTRA-GROUP-NOT-ASSIGNABLE").Should().OnlyContain(f => f.Severity == Severity.Medium);
    }

    [Fact]
    public void GroupWithNoPimAssignmentsAtAll_IsReportedAsNotGovernedByPim()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .Group("g1", "Tier 0 Admins", pim: PimStatus.Unknown)
            .Assigned("a1", "g1", "rd-ga")
            .Build();

        var finding = Run(snapshot).Should().ContainSingle(f => f.Id == "ENTRA-GROUP-NOT-PIM").Subject;

        finding.Severity.Should().Be(Severity.Medium);
        finding.Remedy.Should().Be("Tier 0 Admins carries a role but is not governed by PIM for Groups (no eligible or active PIM assignments found). Onboard it so membership can be eligible instead of permanent.");
    }

    [Fact]
    public void GroupWithPimAssignments_IsNotReportedAsNotPim()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .Group("g1", "Tier 0 Admins", pim: PimStatus.Unknown)
            .GroupPim("g1", "gp1", "u1", eligible: true)
            .Assigned("a1", "g1", "rd-ga")
            .Build();

        Run(snapshot).Should().NotContain(f => f.Id == "ENTRA-GROUP-NOT-PIM", "an empty status with PIM records is an onboarded group Graph reported thinly");
    }

    [Fact]
    public void GroupNotOnboarded_WithOnlyAnActivatedAssignment_IsNotFlagged()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .Group("g1", "Tier 0 Admins", pim: PimStatus.NotOnboarded)
            .Assigned("a1", "g1", "rd-ga", type: AssignmentType.Activated, end: DateTimeOffset.Parse("2026-09-13T16:00:00Z"))
            .Build();

        Run(snapshot).Should().NotContain(f => (f.Id == "ENTRA-GROUP-NOT-PIM" || f.Id == "ENTRA-GROUP-NOT-ASSIGNABLE"), "an Activated instance is a PIM activation, not standing access");
    }

    [Fact]
    public void GroupNotAssignable_WithOnlyAnActivatedAssignment_IsNotFlagged()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .Group("g1", "Legacy Ops", assignable: false, pim: PimStatus.NotOnboarded)
            .Assigned("a1", "g1", "rd-ga", type: AssignmentType.Activated, end: DateTimeOffset.Parse("2026-09-13T16:00:00Z"))
            .Build();

        Run(snapshot).Should().NotContain(f => (f.Id == "ENTRA-GROUP-NOT-PIM" || f.Id == "ENTRA-GROUP-NOT-ASSIGNABLE"));
    }

    [Fact]
    public void DynamicGroup_RemedyMentionsIt()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .Group("g1", "All Engineers", dynamic: true, members: [])
            .Assigned("a1", "g1", "rd-ga")
            .Build();

        Run(snapshot).Single(f => f.Id == "ENTRA-GROUP-PERMANENT").Remedy.Should().Contain("dynamic");
    }
}
