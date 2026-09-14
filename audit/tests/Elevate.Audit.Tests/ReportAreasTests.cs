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
}
