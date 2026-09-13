using Elevate.Audit.Model;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class GroupAndPrincipalRulesTests
{
    private static IReadOnlyList<Finding> Run(Snapshot snapshot) =>
        RuleRunner.Run(snapshot, new AuditOptions(), [new GroupMemberPermanentRule(), new GuestPermanentRule(), new ServicePrincipalPermanentRule()]);

    [Fact]
    public void OnboardedGroupWithPermanentMemberOrOwner_IsHigh()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .User("u2", "Jordan Lee", "jordan.lee@contoso.com")
            .User("u3", "Priya Natarajan", "priya.natarajan@contoso.com")
            .Group("g1", "Tier 0 Admins")
            .GroupPim("g1", "p1", "u1", "member")
            .GroupPim("g1", "p2", "u2", "owner")
            .GroupPim("g1", "p3", "u3", "member", type: AssignmentType.Activated, end: DateTimeOffset.Parse("2026-09-13T18:00:00Z"))
            .GroupPim("g1", "p4", "u3", "member", eligible: true)
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "GROUP-MEMBER-PERMANENT").ToList();

        findings.Should().HaveCount(2);
        findings.Should().OnlyContain(f => f.Severity == Severity.High && f.Role.System == RoleSystem.Group && f.Scope.Kind == ScopeKind.Group);
        findings.Single(f => f.Principal.Id == "u2").Role.DisplayName.Should().Be("Tier 0 Admins (owner)");
        findings.Single(f => f.Principal.Id == "u1").PortalUrl.Should().Contain("aadgroup");
    }

    [Fact]
    public void Guests_AreFlaggedDirectlyAndThroughGroups_ForEntraAndAzure()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("guest1", "Priya Natarajan", "priya_fabrikam.com#EXT#@contoso.com", guest: true)
            .User("guest2", "Casey Wong", "casey_fabrikam.com#EXT#@contoso.com", guest: true)
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .Group("g1", "Tier 0 Admins", members: [("guest2", PrincipalType.User), ("u1", PrincipalType.User)])
            .Assigned("a1", "guest1", "rd-ga")
            .Assigned("a2", "g1", "rd-ga")
            .AzureAssigned("ra1", "/subscriptions/sub1", "8e3af657-a8ff-443c-a75c-2fe8c4bcb635", "guest1", "User")
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "GUEST-PERMANENT").ToList();

        findings.Should().HaveCount(3);
        findings.Where(f => f.Principal.Id == "guest1").Select(f => f.Role.System).Should().BeEquivalentTo([RoleSystem.Entra, RoleSystem.Azure]);
        findings.Single(f => f.Principal.Id == "guest2").Via.Should().ContainSingle().Which.DisplayName.Should().Be("Tier 0 Admins");
        findings.Should().OnlyContain(f => f.Severity == Severity.High && f.Principal.IsGuest);
    }

    [Fact]
    public void ServicePrincipals_AreInformational()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .ServicePrincipal("sp1", "Deploy Bot")
            .ServicePrincipal("mi1", "Backup Job", "ManagedIdentity")
            .Assigned("a1", "sp1", "rd-ga")
            .AzureAssigned("ra1", "/subscriptions/sub1", "8e3af657-a8ff-443c-a75c-2fe8c4bcb635", "mi1", "ServicePrincipal")
            .AzureAssigned("ra2", "/subscriptions/sub1", "acdd72a7-3385-48ef-bd42-f606fba81ae7", "mi1", "ServicePrincipal")
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "SP-PERMANENT").ToList();

        findings.Should().HaveCount(2, "Reader is not privileged");
        findings.Should().OnlyContain(f => f.Severity == Severity.Info && f.Principal.Type == PrincipalType.ServicePrincipal);
        findings.Single(f => f.Principal.Id == "sp1").Remedy.Should().Contain("workload identity");
    }
}
