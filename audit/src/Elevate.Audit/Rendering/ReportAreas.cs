using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

public enum AreaState { Critical, Attention, Review, Clean, NotScanned }

public sealed record ReportArea(string Id, string Name, string Description);

/// <summary>
/// The HTML report's areas: which area a rule belongs to, how skipped sources affect each area, and how a
/// summary tile reads. Pure functions over findings; the renderer only formats what these return.
/// </summary>
public static class ReportAreas
{
    public static readonly ReportArea Entra = new("entra", "Entra roles", "Permanent active directory role assignments, held directly or through groups.");
    public static readonly ReportArea PimGroups = new("pim-groups", "PIM for Groups", "Groups PIM already manages that still have permanent members or owners.");
    public static readonly ReportArea Azure = new("azure", "Azure RBAC", "Permanent privileged Azure role assignments at any scope.");
    public static readonly ReportArea Guests = new("guests", "Guests", "Guests holding a permanent privileged role, directly or through a group.");
    public static readonly ReportArea Workload = new("workload", "Workload identities", "Service principals and managed identities holding permanent privileged roles; PIM eligibility does not apply.");
    public static readonly ReportArea Hygiene = new("hygiene", "Hygiene", "Eligibilities without an end date and the Global Administrator count.");
    public static readonly ReportArea Other = new("other", "Other", "Findings from rules this renderer does not know.");
    public static readonly ReportArea Coverage = new("coverage", "Coverage", "Which sources were read and which were skipped.");

    /// <summary>Finding areas in report order. Coverage is rendered after them, from the skipped list.</summary>
    public static IReadOnlyList<ReportArea> Ordered { get; } = [Entra, PimGroups, Azure, Guests, Workload, Hygiene, Other];

    private static readonly Dictionary<string, ReportArea> ByRule = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTRA-USER-PERMANENT"] = Entra,
        ["ENTRA-GROUP-PERMANENT"] = Entra,
        ["ENTRA-GROUP-NOT-PIM"] = Entra,
        ["ENTRA-GROUP-NOT-ASSIGNABLE"] = Entra,
        ["GROUP-MEMBER-PERMANENT"] = PimGroups,
        ["AZURE-PERMANENT"] = Azure,
        ["GUEST-PERMANENT"] = Guests,
        ["SP-PERMANENT"] = Workload,
        ["ELIGIBLE-NO-END"] = Hygiene,
        ["GA-COUNT"] = Hygiene,
    };

    /// <summary>The area a rule code renders under; an unknown code lands in <see cref="Other"/> so a new rule never disappears.</summary>
    public static ReportArea Of(string ruleCode)
    {
        ArgumentNullException.ThrowIfNull(ruleCode);
        return ByRule.TryGetValue(ruleCode, out var area) ? area : Other;
    }

    /// <summary>The skipped reason when the area's whole source was skipped (Azure with <c>azure</c>, PIM for Groups with <c>pim-for-groups</c>); otherwise null.</summary>
    public static string? NotScannedReason(ReportArea area, IReadOnlyList<SkippedSource> skipped)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(skipped);
        var source = area == Azure ? "azure" : area == PimGroups ? "pim-for-groups" : null;
        return source is null ? null : skipped.FirstOrDefault(s => string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase))?.Reason;
    }

    /// <summary>True when a partially skipped source means the area under-reports: management groups for Azure, unreadable groups for Entra and Guests.</summary>
    public static bool UnderCounts(ReportArea area, IReadOnlyList<SkippedSource> skipped)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(skipped);
        return (area == Azure && Has(skipped, "azure-management-groups")) || ((area == Entra || area == Guests) && Has(skipped, "groups"));
    }

    public static AreaState StateOf(ReportArea area, IReadOnlyList<Finding> areaFindings, IReadOnlyList<SkippedSource> skipped)
    {
        ArgumentNullException.ThrowIfNull(areaFindings);
        if (NotScannedReason(area, skipped) is not null)
        {
            return AreaState.NotScanned;
        }

        if (areaFindings.Any(f => f.Severity == Severity.High))
        {
            return AreaState.Critical;
        }

        if (areaFindings.Any(f => f.Severity is Severity.Medium or Severity.Low))
        {
            return AreaState.Attention;
        }

        return areaFindings.Count > 0 ? AreaState.Review : AreaState.Clean;
    }

    public static string StateLabel(AreaState state) => state switch
    {
        AreaState.Critical => "Critical",
        AreaState.Attention => "Attention",
        AreaState.Review => "Review",
        AreaState.Clean => "Clean",
        _ => "Not scanned",
    };

    /// <summary>"1 person" / "3 people".</summary>
    public static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    internal static bool Has(IReadOnlyList<SkippedSource> skipped, string source) =>
        skipped.Any(s => string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase));
}
