using Elevate.Audit.Model;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class AzureAndHygieneRulesTests
{
    private const string Owner = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
    private const string Contributor = "b24988ac-6180-42a0-ab88-20f7382dd24c";
    private const string Reader = "acdd72a7-3385-48ef-bd42-f606fba81ae7";

    private static IReadOnlyList<Finding> Run(Snapshot snapshot, AuditOptions? options = null) =>
        RuleRunner.Run(snapshot, options ?? new AuditOptions(), [new AzurePermanentRule(), new EligibleNoEndRule(), new GlobalAdminCountRule()]);

    [Fact]
    public void AzurePermanent_SeverityFollowsTheRole_ActivationsAreExcluded_GroupsExpand()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .User("u2", "Jordan Lee", "jordan.lee@contoso.com")
            .User("u3", "Priya Natarajan", "priya.natarajan@contoso.com")
            .Group("g1", "Cloud Ops", members: [("u3", PrincipalType.User)])
            .AzureAssigned("ra-owner", "/subscriptions/sub1", Owner, "u1", "User")
            .AzureAssigned("ra-contrib", "/subscriptions/sub1/resourceGroups/rg-app", Contributor, "g1", "Group")
            .AzureAssigned("ra-reader", "/subscriptions/sub1", Reader, "u1", "User")
            .AzureAssigned("ra-activated", "/subscriptions/sub1", Owner, "u2", "User")
            .AzureAssigned("si-activated", "/subscriptions/sub1", Owner, "u2", "User", type: AssignmentType.Activated, end: DateTimeOffset.Parse("2026-09-13T20:00:00Z"), fromSchedule: true)
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "AZURE-PERMANENT").ToList();

        findings.Should().HaveCount(3);
        findings.Single(f => f.Principal.Id == "u1").Should().BeEquivalentTo(new { Severity = Severity.High, Role = new { DisplayName = "Owner" }, Scope = new { DisplayName = "Production", Kind = ScopeKind.Subscription } });
        findings.Single(f => f.Principal.Id == "g1").Should().BeEquivalentTo(new { Severity = Severity.Medium, Scope = new { DisplayName = "rg-app", Kind = ScopeKind.ResourceGroup } });
        findings.Single(f => f.Principal.Id == "u3").Via.Select(v => v.DisplayName).Should().Equal("Cloud Ops");
        findings.Should().NotContain(f => f.Principal.Id == "u2", "an Activated schedule instance behind the classic assignment means it is a PIM activation");
        findings[0].PortalUrl.Should().StartWith("https://portal.azure.com/#@/resource/subscriptions/sub1");
    }

    [Fact]
    public void AzurePermanent_StillReportsAPrincipalThatCouldNotBeResolved()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .AzureAssigned("ra-ghost", "/subscriptions/sub1", Owner, "00000000-0000-0000-0000-0000000000ff", "Unknown")
            .Build();

        var finding = Run(snapshot).Should().ContainSingle(f => f.Id == "AZURE-PERMANENT").Subject;

        finding.Severity.Should().Be(Severity.High);
        finding.Principal.DisplayName.Should().StartWith("<unknown principal");
        finding.Remedy.Should().Contain("PIM eligibility does not apply to workload identities");
    }

    [Fact]
    public void AzurePermanent_ClassicAndScheduleForSameRole_AreOneFinding_WhenRoleDefinitionIdPrefixesDiffer()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .AzureAssigned("ra-owner", "/", Owner, "u1", "User", roleDefinitionId: "/providers/Microsoft.Authorization/roleDefinitions/" + Owner)
            .AzureAssigned("si-owner", "/", Owner, "u1", "User", type: AssignmentType.Assigned, fromSchedule: true, roleDefinitionId: "/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/" + Owner)
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "AZURE-PERMANENT").ToList();

        findings.Should().ContainSingle();
    }

    [Fact]
    public void EligibilitiesWithoutEnd_AreLow_AcrossAllThreeSystems()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .Group("g1", "Tier 0 Admins")
            .Eligible("e1", "u1", "rd-ga")
            .Eligible("e2", "u1", "rd-ga", end: DateTimeOffset.Parse("2027-01-01T00:00:00Z"))
            .Eligible("e3", "u1", "rd-reader")
            .GroupPim("g1", "ge1", "u1", eligible: true)
            .AzureEligible("ae1", "/subscriptions/sub1", Owner, "u1", "User")
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "ELIGIBLE-NO-END").ToList();

        findings.Should().HaveCount(3);
        findings.Select(f => f.Role.System).Should().BeEquivalentTo([RoleSystem.Entra, RoleSystem.Group, RoleSystem.Azure]);
        findings.Should().OnlyContain(f => f.Severity == Severity.Low);
    }

    [Fact]
    public void GlobalAdminCount_CountsDistinctPeopleThroughGroups()
    {
        var one = SnapshotBuilder.Contoso().User("u1", "Sam Chen", "sam.chen@contoso.com").Assigned("a1", "u1", "rd-ga").Build();
        var six = SnapshotBuilder.Contoso()
            .User("u1", "A", "a@contoso.com").User("u2", "B", "b@contoso.com").User("u3", "C", "c@contoso.com")
            .User("u4", "D", "d@contoso.com").User("u5", "E", "e@contoso.com").User("u6", "F", "f@contoso.com")
            .Group("g1", "GA Group", members: [("u5", PrincipalType.User), ("u6", PrincipalType.User), ("u1", PrincipalType.User)])
            .Assigned("a1", "u1", "rd-ga").Assigned("a2", "u2", "rd-ga").Eligible("e3", "u3", "rd-ga").Eligible("e4", "u4", "rd-ga")
            .Assigned("a5", "g1", "rd-ga")
            .Build();
        var three = SnapshotBuilder.Contoso()
            .User("u1", "A", "a@contoso.com").User("u2", "B", "b@contoso.com").User("u3", "C", "c@contoso.com")
            .Assigned("a1", "u1", "rd-ga").Eligible("e2", "u2", "rd-ga").Eligible("e3", "u3", "rd-ga")
            .Build();

        Run(one).Should().ContainSingle(f => f.Id == "GA-COUNT").Which.Remedy.Should().Contain("1 ");
        Run(six).Should().ContainSingle(f => f.Id == "GA-COUNT").Which.Remedy.Should().Contain("6 ");
        Run(three).Should().NotContain(f => f.Id == "GA-COUNT");
        Run(one).Single(f => f.Id == "GA-COUNT").Should().BeEquivalentTo(new { Severity = Severity.Medium, Principal = new { Id = "11111111-1111-1111-1111-111111111111", DisplayName = "Contoso" } });
    }

    [Fact]
    public void All_HasTenRulesInReportOrder()
    {
        RuleRunner.All.Select(r => r.Code).Should().Equal(
            "ENTRA-USER-PERMANENT", "ENTRA-GROUP-PERMANENT", "ENTRA-GROUP-NOT-PIM", "ENTRA-GROUP-NOT-ASSIGNABLE",
            "GROUP-MEMBER-PERMANENT", "AZURE-PERMANENT", "SP-PERMANENT", "GUEST-PERMANENT", "ELIGIBLE-NO-END", "GA-COUNT");
    }
}
