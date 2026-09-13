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

    private static string RootBlock(string css) => RootBlockPattern().Match(css).Value;

    [GeneratedRegex(@":root\s*\{[^}]*\}")]
    private static partial Regex RootBlockPattern();
}
