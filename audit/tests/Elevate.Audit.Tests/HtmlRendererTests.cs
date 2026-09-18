using System.Text.RegularExpressions;
using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Audit.Rendering;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public partial class HtmlRendererTests
{
    private static AuditReport Sample()
    {
        var snapshot = SampleSnapshot.Build();
        return AuditReport.From(snapshot, RuleRunner.Run(snapshot, new AuditOptions()), RuleRunner.Run(snapshot, new AuditOptions()), new AuditOptions(), "sample", ClientIds.GraphReadScopeNames);
    }

    [Fact]
    public void Render_IsSelfContained_HasOneH1_AndOnlyHttpsLinks()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().StartWith("<!doctype html>");
        Regex.Matches(html, "<h1[ >]").Should().HaveCount(1);
        html.Should().NotContain("<img").And.NotContain("<link ").And.NotContain("src=\"http");
        Regex.Matches(html, "href=\"([^\"]+)\"").Select(m => m.Groups[1].Value).Should().OnlyContain(h => h.StartsWith("https://") || h.StartsWith("#"));
        html.Should().Contain("Tier 0 Admins").And.NotContain("<script src");
        html.Should().Contain("<details");
        html.Should().Contain("<script>\n" + HtmlRenderer.Script.TrimEnd());
        Regex.IsMatch(html, "<[a-z]+[^>]*\\shidden[\\s>]").Should().BeFalse("the no-script page must not hide anything");
    }

    [Fact]
    public void Render_Header_HasVerdictAndOneTilePerArea_LinkingToSections()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().Contain("<p class=\"verdict\">");
        html.Should().Contain("hold standing privileged access in Contoso.");
        html.Should().Contain("Azure management groups were not scanned, so the Azure section under-counts.");
        var hrefs = Regex.Matches(html, "<a class=\"tile[^\"]*\" href=\"#([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        hrefs.Should().Equal("entra", "pim-groups", "azure", "guests", "workload", "eligibility", "hygiene", "coverage");
        foreach (var id in hrefs)
        {
            html.Should().Contain($"id=\"{id}\"", $"tile #{id} must resolve");
        }

        html.Should().NotContain("<div class=\"cards\">", "the severity cards are replaced by tiles");
    }

    [Fact]
    public void Render_Tiles_ShowStateAndUnderCounts()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().Contain("<span class=\"pill critical\">Critical</span>");
        Regex.Matches(html, "<span class=\"pill skipped\">under-counts</span>").Count.Should().BeGreaterThan(0, "management groups were skipped");
        html.Should().Contain("<span class=\"pill skipped\">1 skipped</span>");
    }

    [Fact]
    public void Render_TilesUseUnfilteredFindings_WhenMinSeverityHidesSome()
    {
        var snapshot = SampleSnapshot.Build();
        var options = new AuditOptions(MinSeverity: Severity.High);
        var findings = RuleRunner.Run(snapshot, options);
        var report = AuditReport.From(snapshot, findings, RuleRunner.Visible(findings, options), options, "sample", ClientIds.GraphReadScopeNames);

        var html = HtmlRenderer.Render(report);

        html.Should().Contain("eligibilities never expire", "hygiene findings are Low and hidden from the body, but the tile still counts them");
        html.Should().NotContain("id=\"eligible-no-end-low\"");
    }

    [Fact]
    public void Render_FilteredEmptyArea_ShowsHiddenCount_NotTheCleanSentence()
    {
        var snapshot = SampleSnapshot.Build();
        var options = new AuditOptions(MinSeverity: Severity.High);
        var findings = RuleRunner.Run(snapshot, options);
        var report = AuditReport.From(snapshot, findings, RuleRunner.Visible(findings, options), options, "sample", ClientIds.GraphReadScopeNames);

        var html = HtmlRenderer.Render(report);

        html.Should().Contain("6 findings hidden by --min-severity high or --ignore.");
        html.Should().NotContain("Eligibilities expire and the Global Administrator count is within range.");
    }

    [Fact]
    public void Render_Areas_AreDetailsWithSummaryIds_HighAreasOpen()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().Contain("<details class=\"area\" open><summary id=\"entra\">");
        html.Should().Contain("<details class=\"area\"><summary id=\"hygiene\">", "no High findings in hygiene");
        html.Should().Contain("<details class=\"rule\" id=\"azure-permanent-high\" data-severity=\"high\" open>");
        html.Should().Contain("<details class=\"area\" open><summary id=\"coverage\">");
    }

    [Fact]
    public void Render_RollsUpGroupFindings_WithOutlineDiagramAndMemberTable()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().Contain("<div class=\"group\" data-search=\"");
        html.Should().Contain("<span class=\"who\">Tier 0 Admins</span>");
        html.Should().Contain("<ul class=\"tree\">");
        html.Should().Contain("<svg class=\"nesting\"");
        html.Should().Contain("<details class=\"members\" open><summary>");
        html.Should().Contain("Casey Wong");
    }

    [Fact]
    public void Render_DataSearch_IsLowerCase_AndCoversPrincipalRoleScopeAndPath()
    {
        var html = HtmlRenderer.Render(Sample());

        var values = Regex.Matches(html, "data-search=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
        values.Should().NotBeEmpty();
        values.Should().OnlyContain(v => v == v.ToLowerInvariant());
        values.Should().Contain(v => v.Contains("casey.wong@contoso.com") && v.Contains("global administrator") && v.Contains("tier 0 admins"));
    }

    [Fact]
    public void Render_Coverage_ListsEverySourceWithState()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().Contain("<td>Azure management groups</td><td><span class=\"pill skipped\">skipped</span></td>");
        html.Should().Contain("<td>Entra role assignments</td><td><span class=\"pill read\">read</span></td>");
        html.Should().NotContain("Some sources were skipped.", "the notice box is replaced by the Coverage tile and section");
    }

    [Fact]
    public void Render_NotScannedArea_ShowsTheReason()
    {
        var snapshot = SnapshotBuilder.Contoso().User("u1", "A", "a@contoso.com").Assigned("a1", "u1", "rd-ga").Skipped("azure", "skipped with --skip-azure").Build();
        var findings = RuleRunner.Run(snapshot, new AuditOptions());

        var html = HtmlRenderer.Render(AuditReport.From(snapshot, findings, findings, new AuditOptions(), "x", []));

        html.Should().Contain("<span class=\"pill\">Not scanned</span>");
        html.Should().Contain("skipped with --skip-azure");
    }

    [Fact]
    public void Render_EmptyReport_ReadsClean()
    {
        var snapshot = SnapshotBuilder.Contoso().Build();

        var html = HtmlRenderer.Render(AuditReport.From(snapshot, [], [], new AuditOptions(), "x", []));

        html.Should().Contain("No standing privileged access was found in Contoso.");
        html.Should().Contain("<span class=\"pill clean\">Clean</span>");
        html.Should().Contain("Every source was read.");
    }

    [Fact]
    public void Render_SectionIdsAreUnique()
    {
        var html = HtmlRenderer.Render(Sample());

        var ids = Regex.Matches(html, "id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Render_ARuleWithTwoSeverities_YieldsTwoSections()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().Contain("id=\"azure-permanent-high\"");
        html.Should().Contain("id=\"azure-permanent-medium\"");
    }

    [Fact]
    public void Render_WhenMinSeverityHidesFindings_MentionsItUnderTheCards()
    {
        var snapshot = SampleSnapshot.Build();
        var options = new AuditOptions(MinSeverity: Severity.Medium);
        var findings = RuleRunner.Run(snapshot, options);
        var report = AuditReport.From(snapshot, findings, RuleRunner.Visible(findings, options), options, "sample", ClientIds.GraphReadScopeNames);

        var html = HtmlRenderer.Render(report);

        html.Should().Contain("7 findings below --min-severity medium hidden");
    }

    [Fact]
    public void Render_EscapesUntrustedText()
    {
        var snapshot = SnapshotBuilder.Contoso().User("u1", "<b>Evil</b>", "evil@contoso.com").Assigned("a1", "u1", "rd-ga").Build();

        var html = HtmlRenderer.Render(AuditReport.From(snapshot, RuleRunner.Run(snapshot, new AuditOptions()), RuleRunner.Run(snapshot, new AuditOptions()), new AuditOptions(), "x", []));

        html.Should().Contain("&lt;b&gt;Evil&lt;/b&gt;").And.NotContain("<b>Evil</b>");
    }

    [Fact]
    public void Stylesheet_HiddenAttribute_AlwaysWins()
    {
        HtmlRenderer.Stylesheet.Should().Contain("[hidden] { display: none !important; }");
    }

    [Fact]
    public void Render_StartHere_WrapsTheListSoTheCapNoteLandsInsideIt()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().Contain("<div class=\"start-wrap\"><ol class=\"start\">");
    }

    [Fact]
    public void Render_StartHere_ItemsCarryAnAreaPill_LinkingToTheirSection()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().Contain("Owner on Production</span><a class=\"pill area\" href=\"#azure\">Azure RBAC</a>");
        html.Should().Contain("· 2 people through 2 nested groups</span></span><a class=\"pill area\" href=\"#entra\">Entra roles</a>");
        var items = System.Text.RegularExpressions.Regex.Matches(html, "<li data-search=\"[^\"]*\" data-severity=\"high\">").Count;
        System.Text.RegularExpressions.Regex.Matches(html, "<a class=\"pill area\" href=\"#[a-z-]+\">").Count.Should().Be(items, "every Start here item names its area");
    }

    [Fact]
    public void Stylesheet_TokensMatchTheProductPage()
    {
        var site = File.ReadAllText(Path.Combine(Repo.Root, "site", "styles.css"));

        RootBlock(HtmlRenderer.Stylesheet).Should().Be(RootBlock(site), "audit/src/Elevate.Audit/Rendering/report.css must carry the same :root tokens as site/styles.css");
    }

    [Fact]
    public void SampleReport_MatchesTheCommittedSitePage()
    {
        Golden.Check("site/audit-sample.html", HtmlRenderer.Render(Sample()));
    }

    [Fact]
    public void Script_IsEmbedded_AndSelfContained()
    {
        HtmlRenderer.Script.Should().Contain("'use strict'").And.NotContain("fetch(").And.NotContain("import ");
    }

    [Fact]
    public void Render_LargeTenant_KeepsEveryRowInMarkup_AndCapsNothingServerSide()
    {
        var builder = SnapshotBuilder.Contoso();
        var groupMembers = new List<(string Id, PrincipalType Type)>();
        for (var i = 0; i < 60; i++)
        {
            var id = $"u{i}";
            builder.User(id, $"Person {i}", $"p{i}@contoso.com");
            groupMembers.Add((id, PrincipalType.User));
        }

        builder.Group("g-big", "Big Group", members: groupMembers.ToArray());
        builder.Assigned("a-big", "g-big", "rd-ga");
        for (var i = 0; i < 12; i++)
        {
            var id = $"d{i}";
            builder.User(id, $"Direct {i}", $"d{i}@contoso.com");
            builder.Assigned($"a{i}", id, "rd-ga");
        }

        var snapshot = builder.Build();
        var findings = RuleRunner.Run(snapshot, new AuditOptions());
        var report = AuditReport.From(snapshot, findings, findings, new AuditOptions(), "x", []);

        var html = HtmlRenderer.Render(report);

        Regex.Matches(html, "<tr data-search=").Count.Should().BeGreaterThanOrEqualTo(60, "every member row must stay in the markup, not be capped server-side");
        Regex.Matches(html, "Person ").Count.Should().BeGreaterThanOrEqualTo(60);
        html.Should().Contain("60 people, listed below");
        groupMembers.Count.Should().BeGreaterThan(GroupRollup.MaxInlinePeople, "the outline only rolls up to a count once the group has more direct people than it inlines");
        Regex.Matches(html, "<li data-search=").Count.Should().BeGreaterThan(10, "the 12 direct high findings plus the rolled-up group card exceed the Start-here cap");
        Regex.IsMatch(html, "<[a-z]+[^>]*\\shidden[\\s>]").Should().BeFalse("capping is a script-only concern; the server never emits hidden");
    }

    private static string RootBlock(string css) => RootBlockPattern().Match(css).Value;

    [GeneratedRegex(@":root\s*\{[^}]*\}")]
    private static partial Regex RootBlockPattern();
}
