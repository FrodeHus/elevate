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
        });
        console.Profile.Width = 200;
        return (console, writer);
    }

    private static AuditReport Sample()
    {
        var snapshot = SampleSnapshot.Build();
        return AuditReport.From(snapshot, RuleRunner.Run(snapshot, new AuditOptions()), new AuditOptions(), "sample", ClientIds.GraphReadScopeNames);
    }

    [Fact]
    public void Render_ShowsHeaderSkippedRulesAndPaths()
    {
        var (console, writer) = PlainConsole();

        TerminalRenderer.Render(Sample(), console, summaryOnly: false);
        var text = writer.ToString();

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

        writer.ToString().Trim().Split('\n').Should().ContainSingle().Which.Should().Contain("12 high").And.Contain("5 medium");
    }

    [Fact]
    public void Render_WithNoFindings_SaysSo()
    {
        var (console, writer) = PlainConsole();
        var snapshot = SnapshotBuilder.Contoso().Build();

        TerminalRenderer.Render(AuditReport.From(snapshot, [], new AuditOptions(), "x", []), console, summaryOnly: false);

        writer.ToString().Should().Contain("No standing privileged access found");
    }
}
