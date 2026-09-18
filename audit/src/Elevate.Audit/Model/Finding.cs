namespace Elevate.Audit.Model;

public enum Severity { Info, Low, Medium, High }

public static class Severities
{
    /// <summary>Sort key: High first.</summary>
    public static int Rank(Severity severity) => severity switch
    {
        Severity.High => 0,
        Severity.Medium => 1,
        Severity.Low => 2,
        _ => 3,
    };

    /// <summary>
    /// Parses a severity name (case-insensitively); rejects everything else, including a numeric string
    /// like "0" that <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> would otherwise accept
    /// as the underlying ordinal.
    /// </summary>
    public static bool TryParse(string? text, out Severity severity)
    {
        severity = default;
        return text is not null && !int.TryParse(text, out _) && Enum.TryParse(text, ignoreCase: true, out severity) && Enum.IsDefined(severity);
    }

    public static Severity Parse(string text) => TryParse(text, out var s) ? s : throw new ArgumentException($"Unknown severity '{text}'. Use high, medium, low or info.", nameof(text));
}

public enum RoleSystem { Entra, Azure, Group }

public enum ScopeKind { Directory, AdministrativeUnit, Application, ManagementGroup, Subscription, ResourceGroup, Resource, Group }

public sealed record FindingPrincipal(string Id, string DisplayName, string? UserPrincipalName, PrincipalType Type, bool IsGuest, bool? AccountEnabled);

public sealed record FindingRole(string Id, string DisplayName, string? TemplateId, bool IsPrivileged, RoleSystem System);

public sealed record FindingScope(string Id, string DisplayName, ScopeKind Kind);

public sealed record GroupRef(string Id, string DisplayName);

/// <summary>
/// <paramref name="LastActivatedAt"/> and <paramref name="AgeDays"/> are set only by the unused-eligibility
/// rules: when the eligibility was last activated in the window examined (null for "never"), and how many
/// days it has existed. Both are omitted from the JSON when null.
/// </summary>
public sealed record FindingEvidence(
    string AssignmentId,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime,
    AssignmentType? AssignmentType,
    string? MemberType,
    DateTimeOffset? LastActivatedAt = null,
    int? AgeDays = null);

public sealed record Finding(
    string Id,
    Severity Severity,
    FindingPrincipal Principal,
    FindingRole Role,
    FindingScope Scope,
    IReadOnlyList<GroupRef> Via,
    string Remedy,
    string PortalUrl,
    FindingEvidence Evidence);
