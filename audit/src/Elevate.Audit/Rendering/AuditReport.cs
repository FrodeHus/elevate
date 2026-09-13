using Elevate.Audit.Infrastructure;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

public sealed record ReportTool(string Name, string Version);

public sealed record ReportSummary(int High, int Medium, int Low, int Info)
{
    public int Total => High + Medium + Low + Info;
}

public sealed record ReportOptions(bool AllRoles, IReadOnlyList<string> Ignored, string MinSeverity, bool SkipAzure);

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
    IReadOnlyList<Finding> Findings)
{
    public static AuditReport From(Snapshot snapshot, IReadOnlyList<Finding> findings, AuditOptions options, string toolVersion, IReadOnlyList<string> scopesRequested)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(options);
        return new AuditReport(
            new ReportTool(AppInfo.Name, toolVersion),
            snapshot.Tenant,
            snapshot.Account,
            snapshot.ScannedAt,
            new ReportOptions(options.AllRoles, options.IgnoredRules, options.MinSeverity.ToString().ToLowerInvariant(), options.SkipAzure),
            scopesRequested,
            snapshot.Skipped,
            new ReportSummary(
                findings.Count(f => f.Severity == Severity.High),
                findings.Count(f => f.Severity == Severity.Medium),
                findings.Count(f => f.Severity == Severity.Low),
                findings.Count(f => f.Severity == Severity.Info)),
            findings);
    }
}
