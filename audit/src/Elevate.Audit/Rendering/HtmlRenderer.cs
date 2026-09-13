using System.Globalization;
using System.Net;
using System.Text;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

/// <summary>One self-contained HTML file: inline CSS from report.css, no external requests, one h1, https links only.</summary>
public static class HtmlRenderer
{
    internal const string StylesheetResource = "Elevate.Audit.Resources.report.css";

    private static readonly Lazy<string> StylesheetText = new(() =>
    {
        using var stream = typeof(HtmlRenderer).Assembly.GetManifestResourceStream(StylesheetResource) ?? throw new InvalidOperationException("report.css missing from the bundle");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    public static string Stylesheet => StylesheetText.Value;

    public static string Render(AuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var b = new StringBuilder(64 * 1024);
        var tenantName = report.Tenant.DisplayName ?? report.Tenant.Id;
        b.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\" />\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n<meta name=\"robots\" content=\"noindex\" />\n");
        b.Append("<title>").Append(E($"Standing access report — {tenantName}")).Append("</title>\n<style>\n").Append(Stylesheet).Append("\n</style>\n</head>\n<body>\n");

        b.Append("<header class=\"report-header\"><div class=\"wrap\">\n<p class=\"eyebrow\">elevate-audit · standing privileged access</p>\n");
        b.Append("<h1>").Append(E(tenantName)).Append("</h1>\n<ul class=\"meta\">\n");
        Meta(b, "Tenant id", report.Tenant.Id);
        Meta(b, "Scanned by", report.Account);
        Meta(b, "Scanned at", report.ScannedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        Meta(b, "Tool", $"{report.Tool.Name} {report.Tool.Version}");
        b.Append("</ul>\n<div class=\"cards\">\n");
        Card(b, "high", report.Summary.High);
        Card(b, "medium", report.Summary.Medium);
        Card(b, "low", report.Summary.Low);
        Card(b, "info", report.Summary.Info);
        b.Append("</div>\n");
        if (report.Hidden > 0)
        {
            b.Append("<p class=\"muted\">").Append(E($"{report.Hidden} findings below --min-severity {report.Options.MinSeverity} hidden")).Append("</p>\n");
        }

        if (report.Skipped.Count > 0)
        {
            b.Append("<div class=\"notice\"><strong>Some sources were skipped.</strong> The report under-counts standing access in those areas.<ul>\n");
            foreach (var s in report.Skipped)
            {
                b.Append("<li><strong>").Append(E(s.Source)).Append(":</strong> ").Append(E(s.Reason)).Append("</li>\n");
            }

            b.Append("</ul></div>\n");
        }

        b.Append("</div></header>\n<main class=\"wrap\">\n");

        var high = report.Findings.Where(f => f.Severity == Severity.High).ToList();
        b.Append("<h2 id=\"start-here\">Start here</h2>\n");
        if (high.Count == 0)
        {
            b.Append("<p class=\"muted\">No high-severity findings. ").Append(E(report.Findings.Count == 0 ? "No standing privileged access was found." : "Review the medium and low findings below.")).Append("</p>\n");
        }
        else
        {
            b.Append("<ol class=\"start\">\n");
            foreach (var f in high)
            {
                b.Append("<li><span class=\"who\">").Append(E(f.Principal.DisplayName)).Append(f.Principal.IsGuest ? " <span class=\"pill\">guest</span>" : string.Empty)
                 .Append(" · ").Append(E(f.Role.DisplayName)).Append(" on ").Append(E(f.Scope.DisplayName)).Append("</span>");
                if (f.Via.Count > 0)
                {
                    b.Append("<span class=\"path\">via ").Append(E(string.Join(" ← ", f.Via.Select(v => v.DisplayName)))).Append("</span>");
                }

                b.Append("<span class=\"what\">").Append(E(f.Remedy)).Append(" <a href=\"").Append(E(f.PortalUrl)).Append("\">Open in portal</a></span></li>\n");
            }

            b.Append("</ol>\n");
        }

        b.Append("<h2 id=\"findings\">All findings</h2>\n");
        foreach (var group in report.Findings.GroupBy(f => (f.Id, f.Severity)))
        {
            var severity = group.Key.Severity.ToString().ToLowerInvariant();
            b.Append("<section class=\"rule\" id=\"").Append(E(group.Key.Id.ToLowerInvariant())).Append('-').Append(severity).Append("\"><header><h3>").Append(E(group.Key.Id)).Append("</h3><span class=\"pill ").Append(severity).Append("\">").Append(severity).Append("</span><span class=\"count-pill\">").Append(group.Count().ToString(CultureInfo.InvariantCulture)).Append(group.Count() == 1 ? " finding" : " findings").Append("</span></header>\n");
            b.Append("<div class=\"table-scroll\"><table><thead><tr><th>Principal</th><th>Role</th><th>Scope</th><th>How</th><th>Remedy</th></tr></thead><tbody>\n");
            foreach (var f in group)
            {
                b.Append("<tr><td>").Append(E(f.Principal.DisplayName));
                if (f.Principal.UserPrincipalName is { } upn)
                {
                    b.Append("<br /><span class=\"muted\">").Append(E(upn)).Append("</span>");
                }

                if (f.Principal.Type == PrincipalType.Group)
                {
                    b.Append(" <span class=\"pill\">group</span>");
                }

                if (f.Principal.IsGuest)
                {
                    b.Append(" <span class=\"pill\">guest</span>");
                }

                b.Append("</td><td>").Append(E(f.Role.DisplayName)).Append("<br /><span class=\"muted\">").Append(E(f.Role.System.ToString())).Append("</span></td>");
                b.Append("<td>").Append(E(f.Scope.DisplayName)).Append("<br /><span class=\"muted\">").Append(E(Humanize(f.Scope.Kind))).Append("</span></td>");
                b.Append("<td>");
                if (f.Via.Count == 0)
                {
                    b.Append("<span class=\"muted\">direct</span>");
                }
                else
                {
                    b.Append("<details><summary>through ").Append(f.Via.Count.ToString(CultureInfo.InvariantCulture)).Append(f.Via.Count == 1 ? " group" : " groups").Append("</summary><span class=\"path\">").Append(E(string.Join(" ← ", f.Via.Select(v => v.DisplayName)))).Append("</span></details>");
                }

                b.Append("</td><td>").Append(E(f.Remedy)).Append(" <a href=\"").Append(E(f.PortalUrl)).Append("\">Open in portal</a></td></tr>\n");
            }

            b.Append("</tbody></table></div></section>\n");
        }

        b.Append("<section class=\"appendix\" id=\"appendix\"><h2>Appendix</h2>\n<p>Options: all roles ").Append(report.Options.AllRoles ? "on" : "off").Append("; minimum severity ").Append(E(report.Options.MinSeverity)).Append("; ignored rules: ").Append(report.Options.Ignored.Count == 0 ? "none" : E(string.Join(", ", report.Options.Ignored))).Append(".</p>\n");
        b.Append("<p>Read-only Microsoft Graph scopes requested: ").Append(report.ScopesRequested.Count == 0 ? "none recorded" : string.Join(", ", report.ScopesRequested.Select(s => "<code>" + E(s) + "</code>"))).Append(". Nothing was written to the tenant.</p>\n");
        b.Append("<p>This report contains personal data (names and sign-in names of people who hold roles). Handle it as you would any access review.</p>\n<details><summary>Evidence ids</summary><div class=\"table-scroll\"><table><thead><tr><th>Rule</th><th>Principal id</th><th>Assignment id</th><th>Start</th><th>End</th><th>Type</th></tr></thead><tbody>\n");
        foreach (var f in report.Findings)
        {
            b.Append("<tr><td>").Append(E(f.Id)).Append("</td><td><code>").Append(E(f.Principal.Id)).Append("</code></td><td><code>").Append(E(f.Evidence.AssignmentId)).Append("</code></td><td>").Append(E(Date(f.Evidence.StartDateTime))).Append("</td><td>").Append(E(Date(f.Evidence.EndDateTime))).Append("</td><td>").Append(E(f.Evidence.AssignmentType?.ToString() ?? f.Evidence.MemberType ?? "—")).Append("</td></tr>\n");
        }

        b.Append("</tbody></table></div></details></section>\n</main>\n<footer class=\"wrap\">Generated by <a href=\"https://elevate.reothor.no/audit.html\">elevate-audit</a>, the standing-access companion to Elevate.")
         .Append(report.Tool.Version == "sample" ? " Sample data is fictional." : string.Empty)
         .Append("</footer>\n");
        b.Append("<script>addEventListener('beforeprint',()=>document.querySelectorAll('details').forEach(d=>d.open=true));</script>\n</body>\n</html>\n");
        return b.ToString();
    }

    private static void Meta(StringBuilder b, string label, string value) =>
        b.Append("<li>").Append(E(label)).Append("<strong>").Append(E(value)).Append("</strong></li>\n");

    private static void Card(StringBuilder b, string severity, int count) =>
        b.Append("<div class=\"card ").Append(severity).Append("\"><div class=\"count\">").Append(count.ToString(CultureInfo.InvariantCulture)).Append("</div><div class=\"label\">").Append(severity).Append("</div></div>\n");

    private static string Humanize(ScopeKind kind) => kind switch
    {
        ScopeKind.AdministrativeUnit => "administrative unit",
        ScopeKind.ManagementGroup => "management group",
        ScopeKind.ResourceGroup => "resource group",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string Date(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—";

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
