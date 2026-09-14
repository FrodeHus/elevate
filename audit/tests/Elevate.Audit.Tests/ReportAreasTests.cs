using Elevate.Audit.Model;
using Elevate.Audit.Rendering;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class ReportAreasTests
{
    private static Finding F(string code, Severity severity, PrincipalType type = PrincipalType.User, string principalId = "p1", IReadOnlyList<GroupRef>? via = null) =>
        new(code, severity, new FindingPrincipal(principalId, "Name " + principalId, null, type, false, true),
            new FindingRole("r1", "Global Administrator", null, true, RoleSystem.Entra), new FindingScope("/", "Directory", ScopeKind.Directory),
            via ?? [], "remedy", "https://portal.azure.com/", new FindingEvidence("a1", null, null, AssignmentType.Assigned, null));

    [Theory]
    [InlineData("ENTRA-USER-PERMANENT", "entra")]
    [InlineData("ENTRA-GROUP-PERMANENT", "entra")]
    [InlineData("ENTRA-GROUP-NOT-PIM", "entra")]
    [InlineData("ENTRA-GROUP-NOT-ASSIGNABLE", "entra")]
    [InlineData("GROUP-MEMBER-PERMANENT", "pim-groups")]
    [InlineData("AZURE-PERMANENT", "azure")]
    [InlineData("GUEST-PERMANENT", "guests")]
    [InlineData("SP-PERMANENT", "workload")]
    [InlineData("ELIGIBLE-NO-END", "hygiene")]
    [InlineData("GA-COUNT", "hygiene")]
    [InlineData("FUTURE-RULE", "other")]
    public void Of_MapsEveryRuleCode(string code, string areaId)
    {
        ReportAreas.Of(code).Id.Should().Be(areaId);
    }

    [Fact]
    public void EveryShippedRule_HasAnAreaOtherThanOther()
    {
        RuleRunner.All.Select(r => ReportAreas.Of(r.Code)).Should().NotContain(ReportAreas.Other);
    }

    [Fact]
    public void Ordered_ListsFindingAreasInReportOrder_WithoutCoverage()
    {
        ReportAreas.Ordered.Select(a => a.Id).Should().Equal("entra", "pim-groups", "azure", "guests", "workload", "hygiene", "other");
    }

    [Fact]
    public void StateOf_HighWins_ThenMediumOrLow_ThenInfo_ThenClean()
    {
        ReportAreas.StateOf(ReportAreas.Entra, [F("X", Severity.High), F("X", Severity.Info)], []).Should().Be(AreaState.Critical);
        ReportAreas.StateOf(ReportAreas.Entra, [F("X", Severity.Medium)], []).Should().Be(AreaState.Attention);
        ReportAreas.StateOf(ReportAreas.Entra, [F("X", Severity.Low)], []).Should().Be(AreaState.Attention);
        ReportAreas.StateOf(ReportAreas.Entra, [F("X", Severity.Info)], []).Should().Be(AreaState.Review);
        ReportAreas.StateOf(ReportAreas.Entra, [], []).Should().Be(AreaState.Clean);
    }

    [Fact]
    public void StateOf_NotScanned_WinsOverFindings()
    {
        var skipped = new[] { new SkippedSource("azure", "skipped with --skip-azure") };
        ReportAreas.StateOf(ReportAreas.Azure, [F("AZURE-PERMANENT", Severity.High)], skipped).Should().Be(AreaState.NotScanned);
        ReportAreas.NotScannedReason(ReportAreas.Azure, skipped).Should().Be("skipped with --skip-azure");
        ReportAreas.StateOf(ReportAreas.PimGroups, [], [new SkippedSource("pim-for-groups", "403")]).Should().Be(AreaState.NotScanned);
        ReportAreas.StateOf(ReportAreas.Entra, [], skipped).Should().Be(AreaState.Clean);
    }

    [Fact]
    public void UnderCounts_ManagementGroupsHitAzure_GroupsHitEntraAndGuests()
    {
        var mg = new[] { new SkippedSource("azure-management-groups", "not readable") };
        var groups = new[] { new SkippedSource("groups", "2 nested group(s) could not be read; their members are not included.") };
        ReportAreas.UnderCounts(ReportAreas.Azure, mg).Should().BeTrue();
        ReportAreas.UnderCounts(ReportAreas.Entra, mg).Should().BeFalse();
        ReportAreas.UnderCounts(ReportAreas.Entra, groups).Should().BeTrue();
        ReportAreas.UnderCounts(ReportAreas.Guests, groups).Should().BeTrue();
        ReportAreas.UnderCounts(ReportAreas.Azure, groups).Should().BeFalse();
        ReportAreas.UnderCounts(ReportAreas.Entra, [new SkippedSource("principals", "x")]).Should().BeFalse();
    }

    [Fact]
    public void StateLabel_And_Plural()
    {
        ReportAreas.StateLabel(AreaState.NotScanned).Should().Be("Not scanned");
        ReportAreas.StateLabel(AreaState.Critical).Should().Be("Critical");
        ReportAreas.Plural(1, "person", "people").Should().Be("1 person");
        ReportAreas.Plural(3, "person", "people").Should().Be("3 people");
    }

    private static readonly GroupRef Top = new("g1", "Tier 0 Admins");

    [Fact]
    public void Sentence_Entra_CountsDirectPeopleAndGroupGrants()
    {
        var f = new[]
        {
            F("ENTRA-USER-PERMANENT", Severity.High, principalId: "u1"),
            F("ENTRA-USER-PERMANENT", Severity.High, principalId: "u1"), // same person, two roles
            F("ENTRA-USER-PERMANENT", Severity.High, principalId: "u2"),
            F("ENTRA-GROUP-PERMANENT", Severity.High, PrincipalType.Group, "g1"),
            F("ENTRA-GROUP-PERMANENT", Severity.High, principalId: "u3", via: [Top]),
            F("ENTRA-GROUP-PERMANENT", Severity.High, principalId: "u4", via: [Top]),
            F("ENTRA-GROUP-PERMANENT", Severity.High, principalId: "u4", via: [Top]),
        };
        ReportAreas.Sentence(ReportAreas.Entra, f, []).Should().Be("2 people hold a permanent role directly. 1 group grants roles to 2 more people.");
        ReportAreas.Sentence(ReportAreas.Entra, f.Take(3).ToList(), []).Should().Be("2 people hold a permanent role directly.");
        ReportAreas.Sentence(ReportAreas.Entra, f.Skip(3).ToList(), []).Should().Be("1 group grants roles to 2 people.");
        ReportAreas.Sentence(ReportAreas.Entra, [], []).Should().Be("No permanent Entra role assignments.");
        ReportAreas.Sentence(ReportAreas.Entra, [F("ENTRA-GROUP-NOT-ASSIGNABLE", Severity.Medium, PrincipalType.Group, "g9")], []).Should().Be("1 finding to review.");
    }

    [Fact]
    public void Sentence_OtherAreas()
    {
        ReportAreas.Sentence(ReportAreas.PimGroups, [F("GROUP-MEMBER-PERMANENT", Severity.High, principalId: "u1"), F("GROUP-MEMBER-PERMANENT", Severity.High, principalId: "u2")], [])
            .Should().Be("2 permanent members remain in groups PIM already manages.");
        ReportAreas.Sentence(ReportAreas.PimGroups, [], []).Should().Be("Every PIM-managed group has only eligible members.");

        var azure = new[]
        {
            F("AZURE-PERMANENT", Severity.High, principalId: "u1") with { Scope = new FindingScope("/subscriptions/a", "Prod", ScopeKind.Subscription) },
            F("AZURE-PERMANENT", Severity.High, principalId: "u2") with { Scope = new FindingScope("/subscriptions/a", "Prod", ScopeKind.Subscription) },
            F("AZURE-PERMANENT", Severity.Medium, principalId: "u3") with { Scope = new FindingScope("/subscriptions/b", "Dev", ScopeKind.Subscription) },
            F("AZURE-PERMANENT", Severity.High, principalId: "u4", via: [Top]) with { Scope = new FindingScope("/subscriptions/b", "Dev", ScopeKind.Subscription) },
        };
        ReportAreas.Sentence(ReportAreas.Azure, azure, []).Should().Be("3 permanent privileged assignments across 2 scopes.");
        ReportAreas.Sentence(ReportAreas.Azure, [], []).Should().Be("No permanent privileged Azure assignments.");

        ReportAreas.Sentence(ReportAreas.Guests, [F("GUEST-PERMANENT", Severity.High, principalId: "u1")], []).Should().Be("1 guest holds a permanent privileged role.");
        ReportAreas.Sentence(ReportAreas.Guests, [], []).Should().Be("No guest holds a permanent privileged role.");

        ReportAreas.Sentence(ReportAreas.Workload, [F("SP-PERMANENT", Severity.Info, PrincipalType.ServicePrincipal, "sp1"), F("SP-PERMANENT", Severity.Info, PrincipalType.ServicePrincipal, "sp2")], [])
            .Should().Be("2 service principals hold permanent roles. Review whether they need them.");
        ReportAreas.Sentence(ReportAreas.Workload, [], []).Should().Be("No service principal holds a permanent privileged role.");

        var ga = F("GA-COUNT", Severity.Medium, PrincipalType.Unknown, "tenant") with { Remedy = "7 principals can become Global Administrator (permanent or eligible). Microsoft recommends at most five; move the rest to narrower roles." };
        ReportAreas.Sentence(ReportAreas.Hygiene, [F("ELIGIBLE-NO-END", Severity.Low), F("ELIGIBLE-NO-END", Severity.Low), ga], [])
            .Should().Be("2 eligibilities never expire. 7 principals can become Global Administrator (permanent or eligible).");
        ReportAreas.Sentence(ReportAreas.Hygiene, [ga], []).Should().Be("7 principals can become Global Administrator (permanent or eligible).");
        ReportAreas.Sentence(ReportAreas.Hygiene, [], []).Should().Be("Eligibilities expire and the Global Administrator count is within range.");

        ReportAreas.Sentence(ReportAreas.Other, [F("FUTURE", Severity.Low)], []).Should().Be("1 finding to review.");
    }

    [Fact]
    public void Sentence_Coverage_AndNotScanned()
    {
        ReportAreas.Sentence(ReportAreas.Coverage, [], []).Should().Be("Every source was read.");
        ReportAreas.Sentence(ReportAreas.Coverage, [], [new SkippedSource("azure-management-groups", "Not readable."), new SkippedSource("groups", "2 nested group(s) could not be read.")])
            .Should().Be("Not readable. 2 nested group(s) could not be read.");
        ReportAreas.Sentence(ReportAreas.Azure, [], [new SkippedSource("azure", "skipped with --skip-azure")]).Should().Be("skipped with --skip-azure");
    }

    [Fact]
    public void Verdict_CountsDistinctPeopleAndWorkloadIdentities_AcrossStandingRulesOnly()
    {
        var f = new[]
        {
            F("ENTRA-USER-PERMANENT", Severity.High, principalId: "u1"),
            F("AZURE-PERMANENT", Severity.High, principalId: "u1"),
            F("ENTRA-GROUP-PERMANENT", Severity.High, PrincipalType.Group, "g1"),
            F("ENTRA-GROUP-PERMANENT", Severity.High, principalId: "u2", via: [Top]),
            F("GUEST-PERMANENT", Severity.High, principalId: "u2", via: [Top]),
            F("SP-PERMANENT", Severity.Info, PrincipalType.ServicePrincipal, "sp1"),
            F("AZURE-PERMANENT", Severity.High, PrincipalType.ServicePrincipal, "sp1"),
            F("ELIGIBLE-NO-END", Severity.Low, principalId: "u9"),
            F("GA-COUNT", Severity.Medium, PrincipalType.Unknown, "tenant"),
        };
        var v = ReportAreas.VerdictFor("Contoso", f, []);
        v.Head.Should().Be("2 people and 1 workload identity hold standing privileged access in Contoso.");
        v.Tail.Should().BeNull();

        ReportAreas.VerdictFor("Contoso", f.Take(4).ToList(), []).Head.Should().Be("2 people hold standing privileged access in Contoso.");
        ReportAreas.VerdictFor("Contoso", [f[0]], []).Head.Should().Be("1 person holds standing privileged access in Contoso.");
        ReportAreas.VerdictFor("Contoso", [f[5]], []).Head.Should().Be("1 workload identity holds standing privileged access in Contoso.");
        ReportAreas.VerdictFor("Contoso", [f[7], f[8]], []).Head.Should().Be("No standing privileged access was found in Contoso.");
    }

    [Fact]
    public void Verdict_Tail_OneClausePerDistinctSkippedSource()
    {
        var skipped = new[]
        {
            new SkippedSource("azure-management-groups", "a"),
            new SkippedSource("groups", "b"),
            new SkippedSource("groups", "c"),
            new SkippedSource("weird", "d"),
        };
        ReportAreas.VerdictFor("Contoso", [], skipped).Tail.Should()
            .Be("Azure management groups were not scanned, so the Azure section under-counts; some groups could not be read, so the Entra and Guests sections under-count; the weird source was skipped.");
        ReportAreas.VerdictFor("Contoso", [], [new SkippedSource("azure", "x")]).Tail.Should().Be("Azure was not scanned.");
        ReportAreas.SkippedClause("pim-for-groups").Should().Be("PIM for Groups was not scanned");
        ReportAreas.SkippedClause("principals").Should().Be("some principals could not be resolved to names");
    }
}
