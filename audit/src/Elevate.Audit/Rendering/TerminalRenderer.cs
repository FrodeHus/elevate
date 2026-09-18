using System.Globalization;
using Elevate.Audit.Model;
using Spectre.Console;

namespace Elevate.Audit.Rendering;

public static class TerminalRenderer
{
    public static string SummaryLine(AuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var s = report.Summary;
        return s.Total == 0
            ? "No standing privileged access found."
            : $"{s.Total} findings: {s.High} high, {s.Medium} medium, {s.Low} low, {s.Info} info.";
    }

    public static void Render(AuditReport report, IAnsiConsole console, bool summaryOnly)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(console);
        if (summaryOnly)
        {
            // --quiet must stay exactly one line; the hidden count still shows up in JSON/HTML.
            console.WriteLine(SummaryLine(report));
            return;
        }

        var header = new Table().Border(TableBorder.None).HideHeaders().AddColumn(string.Empty).AddColumn(string.Empty);
        header.AddRow("[grey]Tenant[/]", Markup.Escape($"{report.Tenant.DisplayName ?? "(unnamed)"} · {report.Tenant.Id}"));
        header.AddRow("[grey]Account[/]", Markup.Escape(report.Account));
        header.AddRow("[grey]Scanned[/]", Markup.Escape(report.ScannedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)));
        header.AddRow("[grey]Tool[/]", Markup.Escape($"{report.Tool.Name} {report.Tool.Version}"));
        if (report.Options.AllRoles)
        {
            header.AddRow("[grey]Roles[/]", "all roles (--all-roles)");
        }

        if (report.Options.Ignored.Count > 0)
        {
            header.AddRow("[grey]Ignored[/]", Markup.Escape(string.Join(", ", report.Options.Ignored)));
        }

        if (report.ActivationWindow is { } window)
        {
            header.AddRow("[grey]History[/]", Markup.Escape($"activations {window.Since:yyyy-MM-dd} to {window.Until:yyyy-MM-dd} ({window.Days} days; audit-log retention may be shorter)"));
        }

        if (report.Skipped.Count > 0)
        {
            header.AddRow("[grey]Skipped[/]", Markup.Escape(string.Join(", ", report.Skipped.Select(s => s.Source))));
        }

        console.Write(new Panel(header).Header("elevate-audit").Border(BoxBorder.Rounded));

        if (report.Skipped.Count > 0)
        {
            var skipped = new Table().Border(TableBorder.Rounded).AddColumn("Source").AddColumn("Reason");
            foreach (var s in report.Skipped)
            {
                skipped.AddRow(Markup.Escape(s.Source), Markup.Escape(s.Reason));
            }

            console.Write(new Panel(skipped).Header("[yellow]Skipped[/]").Border(BoxBorder.Rounded));
        }

        foreach (var group in report.Findings.GroupBy(f => (f.Id, f.Severity)))
        {
            var severity = group.Key.Severity;
            var table = new Table().Border(TableBorder.Rounded).Expand();
            table.AddColumn("Principal");
            table.AddColumn("Role");
            table.AddColumn("Scope");
            table.AddColumn("Via");
            table.AddColumn("Remedy");
            foreach (var f in group)
            {
                var principal = f.Principal.Type == PrincipalType.Group ? $"[bold]{Markup.Escape(f.Principal.DisplayName)}[/] [grey](group)[/]" : Markup.Escape(f.Principal.DisplayName) + (f.Principal.IsGuest ? " [yellow](guest)[/]" : string.Empty);
                table.AddRow(
                    principal,
                    Markup.Escape(f.Role.DisplayName),
                    Markup.Escape(f.Scope.DisplayName),
                    f.Via.Count == 0 ? "[grey]direct[/]" : Markup.Escape(string.Join(" ← ", f.Via.Select(v => v.DisplayName))),
                    Markup.Escape(f.Remedy));
            }

            console.Write(new Panel(table).Header($"{Colour(severity)}{Markup.Escape(group.Key.Id)}[/] · {Markup.Escape(severity.ToString().ToLowerInvariant())} · {group.Count()}").Border(BoxBorder.Rounded));
        }

        console.WriteLine(SummaryLine(report));
        WriteHiddenNote(report, console);
    }

    private static void WriteHiddenNote(AuditReport report, IAnsiConsole console)
    {
        if (report.Hidden > 0)
        {
            console.WriteLine($"({RenderText.Findings(report.Hidden)} below --min-severity {report.Options.MinSeverity} hidden)");
        }
    }

    private static string Colour(Severity severity) => severity switch
    {
        Severity.High => "[red bold]",
        Severity.Medium => "[yellow bold]",
        Severity.Low => "[blue]",
        _ => "[grey]",
    };
}
