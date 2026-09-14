using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

public enum AreaState { Critical, Attention, Review, Clean, NotScanned }

public sealed record ReportArea(string Id, string Name, string Description);

public sealed record Verdict(string Head, string? Tail);

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

    private static readonly HashSet<string> StandingRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "ENTRA-USER-PERMANENT", "ENTRA-GROUP-PERMANENT", "GROUP-MEMBER-PERMANENT", "AZURE-PERMANENT", "GUEST-PERMANENT", "SP-PERMANENT",
    };

    /// <summary>The one-line sentence under a tile. <paramref name="areaFindings"/> is every finding of the area (unfiltered).</summary>
    public static string Sentence(ReportArea area, IReadOnlyList<Finding> areaFindings, IReadOnlyList<SkippedSource> skipped)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(areaFindings);
        ArgumentNullException.ThrowIfNull(skipped);
        if (area == Coverage)
        {
            return skipped.Count == 0 ? "Every source was read." : string.Join(" ", skipped.Select(s => s.Reason));
        }

        if (NotScannedReason(area, skipped) is { } reason)
        {
            return reason;
        }

        if (area == Entra)
        {
            var direct = Distinct(areaFindings.Where(f => Is(f, "ENTRA-USER-PERMANENT")));
            var groups = Distinct(areaFindings.Where(f => Is(f, "ENTRA-GROUP-PERMANENT") && f.Principal.Type == PrincipalType.Group));
            var through = Distinct(areaFindings.Where(f => Is(f, "ENTRA-GROUP-PERMANENT") && f.Via.Count > 0));
            var parts = new List<string>();
            if (direct > 0)
            {
                parts.Add($"{Plural(direct, "person holds", "people hold")} a permanent role directly.");
            }

            if (groups > 0)
            {
                parts.Add($"{Plural(groups, "group grants", "groups grant")} roles to {(direct > 0 ? Plural(through, "more person", "more people") : Plural(through, "person", "people"))}.");
            }

            return parts.Count > 0 ? string.Join(" ", parts) : areaFindings.Count == 0 ? "No permanent Entra role assignments." : Review(areaFindings.Count);
        }

        if (area == PimGroups)
        {
            var n = Distinct(areaFindings);
            return n == 0 ? "Every PIM-managed group has only eligible members." : $"{Plural(n, "permanent member remains", "permanent members remain")} in groups PIM already manages.";
        }

        if (area == Azure)
        {
            var assignments = areaFindings.Where(f => f.Via.Count == 0).ToList();
            if (assignments.Count == 0)
            {
                return "No permanent privileged Azure assignments.";
            }

            var scopes = assignments.Select(f => f.Scope.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return $"{Plural(assignments.Count, "permanent privileged assignment", "permanent privileged assignments")} across {Plural(scopes, "scope", "scopes")}.";
        }

        if (area == Guests)
        {
            var n = Distinct(areaFindings);
            return n == 0 ? "No guest holds a permanent privileged role." : $"{Plural(n, "guest holds", "guests hold")} a permanent privileged role.";
        }

        if (area == Workload)
        {
            var n = Distinct(areaFindings);
            return n == 0 ? "No service principal holds a permanent privileged role." : $"{Plural(n, "service principal holds", "service principals hold")} permanent roles. Review whether they need them.";
        }

        if (area == Hygiene)
        {
            var noEnd = areaFindings.Count(f => Is(f, "ELIGIBLE-NO-END"));
            var ga = areaFindings.FirstOrDefault(f => Is(f, "GA-COUNT"));
            var parts = new List<string>();
            if (noEnd > 0)
            {
                parts.Add($"{Plural(noEnd, "eligibility never expires", "eligibilities never expire")}.");
            }

            if (ga is not null)
            {
                parts.Add(FirstSentence(ga.Remedy));
            }

            return parts.Count > 0 ? string.Join(" ", parts) : "Eligibilities expire and the Global Administrator count is within range.";
        }

        return areaFindings.Count == 0 ? "No findings." : Review(areaFindings.Count);
    }

    /// <summary>The header verdict: distinct people and workload identities across the standing-access rules, then one clause per skipped source.</summary>
    public static Verdict VerdictFor(string tenantName, IReadOnlyList<Finding> allFindings, IReadOnlyList<SkippedSource> skipped)
    {
        ArgumentNullException.ThrowIfNull(tenantName);
        ArgumentNullException.ThrowIfNull(allFindings);
        ArgumentNullException.ThrowIfNull(skipped);
        var standing = allFindings.Where(f => StandingRules.Contains(f.Id)).ToList();
        var people = Distinct(standing.Where(f => f.Principal.Type == PrincipalType.User));
        var workload = Distinct(standing.Where(f => f.Principal.Type == PrincipalType.ServicePrincipal));
        string head;
        if (people == 0 && workload == 0)
        {
            head = $"No standing privileged access was found in {tenantName}.";
        }
        else
        {
            var who = people > 0 && workload > 0
                ? $"{Plural(people, "person", "people")} and {Plural(workload, "workload identity", "workload identities")} hold"
                : people > 0 ? Plural(people, "person holds", "people hold") : Plural(workload, "workload identity holds", "workload identities hold");
            head = $"{who} standing privileged access in {tenantName}.";
        }

        var clauses = skipped.Select(s => s.Source).Distinct(StringComparer.OrdinalIgnoreCase).Select(SkippedClause).ToList();
        if (clauses.Count == 0)
        {
            return new Verdict(head, null);
        }

        var tail = string.Join("; ", clauses);
        return new Verdict(head, char.ToUpperInvariant(tail[0]) + tail[1..] + ".");
    }

    public static string SkippedClause(string source) => source.ToLowerInvariant() switch
    {
        "azure" => "Azure was not scanned",
        "azure-management-groups" => "Azure management groups were not scanned, so the Azure section under-counts",
        "pim-for-groups" => "PIM for Groups was not scanned",
        "groups" => "some groups could not be read, so the Entra and Guests sections under-count",
        "principals" => "some principals could not be resolved to names",
        _ => $"the {source} source was skipped",
    };

    private static bool Is(Finding f, string code) => string.Equals(f.Id, code, StringComparison.OrdinalIgnoreCase);

    private static int Distinct(IEnumerable<Finding> findings) => findings.Select(f => f.Principal.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static string Review(int count) => $"{Plural(count, "finding", "findings")} to review.";

    private static string FirstSentence(string text)
    {
        var i = text.IndexOf(". ", StringComparison.Ordinal);
        return i < 0 ? text : text[..(i + 1)];
    }
}
