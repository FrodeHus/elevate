using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

public static class RuleRunner
{
    /// <summary>Every rule, in report order. Tasks 11–13 add theirs here.</summary>
    public static IReadOnlyList<IRule> All { get; } =
    [
    ];

    public static IReadOnlyList<Finding> Run(Snapshot snapshot, AuditOptions options, IEnumerable<IRule>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var context = new RuleContext(snapshot, options);
        var ignored = new HashSet<string>(options.IgnoredRules, StringComparer.OrdinalIgnoreCase);
        return (rules ?? All)
            .Where(r => !ignored.Contains(r.Code))
            .SelectMany(r => r.Evaluate(context))
            .OrderBy(f => Severities.Rank(f.Severity))
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ThenBy(f => f.Principal.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Scope.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<Finding> Visible(IReadOnlyList<Finding> findings, AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(options);
        return findings.Where(f => Severities.Rank(f.Severity) <= Severities.Rank(options.MinSeverity)).ToList();
    }

    public static bool HasHigh(IEnumerable<Finding> findings) => findings.Any(f => f.Severity == Severity.High);
}
