using System.Text.RegularExpressions;
using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Audit.Rendering;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;
using Spectre.Console;

namespace Elevate.Audit.Tests;

public class TerminalRendererTests
{
    private static (IAnsiConsole Console, StringWriter Writer) PlainConsole()
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        console.Profile.Width = 200;
        console.Profile.Capabilities.Ansi = false;
        return (console, writer);
    }

    /// <summary>Strips ANSI CSI escape sequences, belt-and-braces against Spectre emitting them despite a plain profile.</summary>
    private static string Plain(string text) => Regex.Replace(text, "\\u001B\\[[0-9;?]*[ -/]*[@-~]", string.Empty);

    private static AuditReport Sample()
    {
        var snapshot = SampleSnapshot.Build();
        return AuditReport.From(snapshot, RuleRunner.Run(snapshot, new AuditOptions()), RuleRunner.Run(snapshot, new AuditOptions()), new AuditOptions(), "sample", ClientIds.GraphReadScopeNames);
    }

    [Fact]
    public void Render_ShowsHeaderSkippedRulesAndPaths()
    {
        var (console, writer) = PlainConsole();

        TerminalRenderer.Render(Sample(), console, summaryOnly: false);
        var text = Plain(writer.ToString());

        text.Should().Contain("Contoso").And.Contain("alex.rivera@contoso.com");
        text.Should().Contain("Skipped").And.Contain("management groups");
        text.Should().Contain("ENTRA-GROUP-PERMANENT").And.Contain("Tier 0 Admins ← Platform Team");
        text.Should().Contain("GA-COUNT");
        text.Should().Contain(TerminalRenderer.SummaryLine(Sample()));
    }

    [Fact]
    public void SummaryOnly_PrintsOneLine()
    {
        var (console, writer) = PlainConsole();

        TerminalRenderer.Render(Sample(), console, summaryOnly: true);

        Plain(writer.ToString()).Trim().Split('\n').Should().ContainSingle().Which.Should().Contain("13 high").And.Contain("5 medium");
    }

    [Fact]
    public void SummaryOnly_StaysOneLine_EvenWithHiddenFindings()
    {
        var (console, writer) = PlainConsole();
        var snapshot = SampleSnapshot.Build();
        var options = new AuditOptions(MinSeverity: Severity.Medium);
        var findings = RuleRunner.Run(snapshot, options);
        var report = AuditReport.From(snapshot, findings, RuleRunner.Visible(findings, options), options, "sample", ClientIds.GraphReadScopeNames);

        TerminalRenderer.Render(report, console, summaryOnly: true);

        Plain(writer.ToString()).Trim().Split('\n').Should().ContainSingle();
    }

    [Fact]
    public void Render_SplitsPanelsBySeverityWithinSameRuleCode()
    {
        var (console, writer) = PlainConsole();
        var snapshot = SnapshotBuilder.Contoso()
            .User("u-owner", "Owner User", "owner@contoso.com")
            .User("u-contrib", "Contrib User", "contrib@contoso.com")
            .AzureAssigned("ra-owner", "/subscriptions/sub1", "8e3af657-a8ff-443c-a75c-2fe8c4bcb635", "u-owner", "User")
            .AzureAssigned("ra-contrib", "/subscriptions/sub1", "b24988ac-6180-42a0-ab88-20f7382dd24c", "u-contrib", "User")
            .Build();

        TerminalRenderer.Render(AuditReport.From(snapshot, RuleRunner.Run(snapshot, new AuditOptions()), RuleRunner.Run(snapshot, new AuditOptions()), new AuditOptions(), "x", []), console, summaryOnly: false);
        var text = Plain(writer.ToString());

        text.Should().Contain("AZURE-PERMANENT · high · 1");
        text.Should().Contain("AZURE-PERMANENT · medium · 1");
    }

    [Fact]
    public void Render_WithNoColor_EmitsNoAnsiEscapes()
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        console.Profile.Width = 200;

        TerminalRenderer.Render(Sample(), console, summaryOnly: false);
        var text = writer.ToString();

        text.Should().NotContain("\u001B");
    }

    [Fact]
    public void Render_WhenMinSeverityHidesFindings_PrintsTheHiddenCount()
    {
        var (console, writer) = PlainConsole();
        var snapshot = SampleSnapshot.Build();
        var options = new AuditOptions(MinSeverity: Severity.Medium);
        var findings = RuleRunner.Run(snapshot, options);
        var report = AuditReport.From(snapshot, findings, RuleRunner.Visible(findings, options), options, "sample", ClientIds.GraphReadScopeNames);

        TerminalRenderer.Render(report, console, summaryOnly: false);

        Plain(writer.ToString()).Should().Contain("(7 findings below --min-severity medium hidden)");
    }

    [Fact]
    public void Render_WithNoHiddenFindings_PrintsNoHiddenNote()
    {
        var (console, writer) = PlainConsole();

        TerminalRenderer.Render(Sample(), console, summaryOnly: false);

        Plain(writer.ToString()).Should().NotContain("hidden)");
    }

    [Fact]
    public void Render_WithNoFindings_SaysSo()
    {
        var (console, writer) = PlainConsole();
        var snapshot = SnapshotBuilder.Contoso().Build();

        TerminalRenderer.Render(AuditReport.From(snapshot, [], [], new AuditOptions(), "x", []), console, summaryOnly: false);

        Plain(writer.ToString()).Should().Contain("No standing privileged access found");
    }
}
