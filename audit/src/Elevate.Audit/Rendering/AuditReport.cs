using System.Text.Json.Serialization;
using Elevate.Audit.Infrastructure;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

public sealed record ReportTool(string Name, string Version);

public sealed record ReportSummary(int High, int Medium, int Low, int Info)
{
    public int Total => High + Medium + Low + Info;
}

public sealed record ReportOptions(bool AllRoles, IReadOnlyList<string> Ignored, string MinSeverity, bool SkipAzure, int DormantAfterDays = AuditDefaults.DormantAfterDays);

/// <summary>
/// The activation history the unused-eligibility rules actually saw. The report states this rather than
/// the lookback that was asked for, because a tenant keeps directory audit logs for 30 days by default and
/// a longer window would imply history that is not there.
/// </summary>
public sealed record ReportWindow(DateTimeOffset Since, DateTimeOffset Until, int Days, IReadOnlyList<string> Systems);

/// <summary>Shared pluralisation for renderer text (the terminal and HTML hidden-count notes).</summary>
internal static class RenderText
{
    public static string Findings(int count) => count == 1 ? "1 finding" : $"{count} findings";
}

/// <summary>The JSON document `--json` writes. Field names are a stability contract (golden-tested).</summary>
public sealed record AuditReport(
    ReportTool Tool,
    TenantInfo Tenant,
    string Account,
    DateTimeOffset ScannedAt,
    ReportOptions Options,
    IReadOnlyList<string> ScopesRequested,
    IReadOnlyList<SkippedSource> Skipped,
    ReportSummary Summary,
    int Hidden,
    IReadOnlyList<Finding> Findings,
    ReportWindow? ActivationWindow = null)
{
    /// <summary>Every finding, before <c>--min-severity</c>; the HTML summary reads this. Never serialised.</summary>
    [JsonIgnore]
    public IReadOnlyList<Finding> AllFindings { get; init; } = Findings;
    /// <summary>
    /// <paramref name="findings"/> is every finding (the summary is never filtered by <c>--min-severity</c>);
    /// <paramref name="visible"/> is what <c>--min-severity</c> lets through and becomes <see cref="Findings"/>.
    /// </summary>
    public static AuditReport From(Snapshot snapshot, IReadOnlyList<Finding> findings, IReadOnlyList<Finding> visible, AuditOptions options, string toolVersion, IReadOnlyList<string> scopesRequested)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(options);
        return new AuditReport(
            new ReportTool(AppInfo.Name, toolVersion),
            snapshot.Tenant,
            snapshot.Account,
            snapshot.ScannedAt,
            new ReportOptions(options.AllRoles, options.IgnoredRules, options.MinSeverity.ToString().ToLowerInvariant(), options.SkipAzure, options.DormantAfterDays),
            scopesRequested,
            snapshot.Skipped,
            new ReportSummary(
                findings.Count(f => f.Severity == Severity.High),
                findings.Count(f => f.Severity == Severity.Medium),
                findings.Count(f => f.Severity == Severity.Low),
                findings.Count(f => f.Severity == Severity.Info)),
            findings.Count - visible.Count,
            visible,
            snapshot.Activations is { } history
                ? new ReportWindow(history.Since, history.Until, history.Days, history.Systems.Select(s => s.ToString()).ToList())
                : null)
        { AllFindings = findings };
    }
}
