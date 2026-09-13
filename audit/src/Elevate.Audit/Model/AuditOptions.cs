namespace Elevate.Audit.Model;

/// <summary>The command-line choices the rules and renderers need to know about.</summary>
public sealed record AuditOptions(
    bool AllRoles = false,
    IReadOnlyList<string>? Ignored = null,
    Severity MinSeverity = Severity.Info,
    bool SkipAzure = false)
{
    public IReadOnlyList<string> IgnoredRules => Ignored ?? [];
}
