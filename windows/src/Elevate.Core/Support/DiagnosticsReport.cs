using System.Globalization;

namespace Elevate.Core.Support;

/// <summary>
/// One signed-in account, as shown in a diagnostics report. Never carries a token, client id, or
/// other secret: there is no field for one.
/// </summary>
public sealed record DiagnosticsAccount(string Upn, string Method, int TenantCount);

/// <summary>One configured tenant, as shown in a diagnostics report.</summary>
public sealed record DiagnosticsTenant(string Name, string Id, string Mode, IReadOnlyList<string> Flags);

/// <summary>
/// The input to <see cref="DiagnosticsReport.Render"/>. Deliberately has no field for a client id,
/// token, or other secret, so none can appear in the rendered text; callers must not pass secrets
/// in <see cref="Errors"/> either, since error messages are rendered verbatim.
/// </summary>
public sealed record DiagnosticsInput(
    string AppVersion,
    string Build,
    string Signing,
    string Os,
    IReadOnlyList<DiagnosticsAccount> Accounts,
    IReadOnlyList<DiagnosticsTenant> Tenants,
    IReadOnlyList<string> Profiles,
    string? HotKey,
    IReadOnlyList<DiagnosticsError> Errors);

/// <summary>
/// Renders a plain-text diagnostics report for "Copy diagnostics" in Settings. Pure formatting: it
/// does not filter or redact <see cref="DiagnosticsInput.Errors"/>.
/// </summary>
public static class DiagnosticsReport
{
    public static string Render(DiagnosticsInput input, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var lines = new List<string>
        {
            "Elevate Diagnostics",
            $"Generated: {Iso(now ?? DateTimeOffset.UtcNow)}",
            "",
            $"App version: {input.AppVersion} ({input.Build})",
            $"Signing: {input.Signing}",
            $"Windows: {input.Os}",
            "",
            "Accounts:",
        };

        if (input.Accounts.Count == 0)
        {
            lines.Add("  None");
        }
        else
        {
            foreach (var account in input.Accounts)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"  {account.Upn} — {account.Method} — {account.TenantCount} tenant(s)"));
            }
        }

        lines.Add("");
        lines.Add("Tenants:");
        if (input.Tenants.Count == 0)
        {
            lines.Add("  None");
        }
        else
        {
            foreach (var tenant in input.Tenants)
            {
                var flags = tenant.Flags.Count == 0 ? "none" : string.Join(", ", tenant.Flags);
                lines.Add($"  {tenant.Name} ({tenant.Id}) — mode: {tenant.Mode} — flags: {flags}");
            }
        }

        lines.Add("");
        lines.Add("Profiles:");
        if (input.Profiles.Count == 0)
        {
            lines.Add("  None");
        }
        else
        {
            lines.AddRange(input.Profiles.Select(p => $"  {p}"));
        }

        lines.Add("");
        lines.Add($"Hot key: {input.HotKey ?? "None"}");
        lines.Add("");
        lines.Add("Recent errors:");
        if (input.Errors.Count == 0)
        {
            lines.Add("  None");
        }
        else
        {
            lines.AddRange(input.Errors.Select(e => $"  [{Iso(e.Date)}] {e.Message}"));
        }

        return string.Join("\n", lines);
    }

    private static string Iso(DateTimeOffset date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
