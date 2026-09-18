namespace Elevate.Audit.Model;

public enum PrincipalType { User, Group, ServicePrincipal, Device, Contact, Unknown }

/// <summary>Graph's <c>assignmentType</c>: a standing assignment or a PIM activation of an eligibility.</summary>
public enum AssignmentType { Assigned, Activated }

public enum PimStatus { Onboarded, NotOnboarded, Unknown }

public enum AzureScopeKind { ManagementGroup, Subscription, ResourceGroup, Resource }

public sealed record TenantInfo(string Id, string? DisplayName);

public sealed record PrincipalRecord(
    string Id,
    PrincipalType Type,
    string? DisplayName,
    string? UserPrincipalName,
    bool IsGuest,
    bool? AccountEnabled,
    string? ServicePrincipalType);

public sealed record RoleDefinitionRecord(string Id, string? TemplateId, string DisplayName, bool IsPrivileged, bool IsBuiltIn);

/// <summary>One Entra role assignment or eligibility schedule instance. <see cref="AssignmentType"/> is null for eligibilities.</summary>
public sealed record EntraAssignmentRecord(
    string Id,
    string PrincipalId,
    string RoleDefinitionId,
    string DirectoryScopeId,
    string? AppScopeId,
    AssignmentType? AssignmentType,
    string? MemberType,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime)
{
    public bool IsPermanent => AssignmentType == Model.AssignmentType.Assigned && EndDateTime is null;
}

public sealed record GroupMemberRecord(string Id, PrincipalType Type);

/// <summary>One PIM for Groups schedule instance; <see cref="AccessId"/> is "member" or "owner".</summary>
public sealed record GroupPimRecord(
    string Id,
    string PrincipalId,
    string AccessId,
    AssignmentType? AssignmentType,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime)
{
    public bool IsPermanent => AssignmentType == Model.AssignmentType.Assigned && EndDateTime is null;
}

public sealed record GroupRecord(
    string Id,
    string DisplayName,
    bool IsAssignableToRole,
    bool IsDynamic,
    PimStatus PimStatus,
    IReadOnlyList<GroupMemberRecord> DirectMembers,
    IReadOnlyList<GroupPimRecord> PimAssignments,
    IReadOnlyList<GroupPimRecord> PimEligibilities,
    bool SecurityEnabled,
    bool MailEnabled,
    string? Visibility);

public sealed record AzureScopeRecord(string Id, AzureScopeKind Kind, string DisplayName);

public sealed record AzureRoleDefinitionRecord(string Id, string Name, string DisplayName, string Type, IReadOnlyList<string> Actions);

/// <summary>
/// One Azure role assignment. <see cref="FromSchedule"/> is true for PIM schedule instances (which carry
/// <see cref="AssignmentType"/>) and false for classic <c>roleAssignments</c> entries.
/// </summary>
public sealed record AzureAssignmentRecord(
    string Id,
    string Scope,
    string RoleDefinitionId,
    string PrincipalId,
    string? PrincipalType,
    AssignmentType? AssignmentType,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime,
    bool FromSchedule);

public sealed record SkippedSource(string Source, string Reason);

/// <summary>
/// One PIM activation seen in the lookback window. <see cref="RoleKeys"/> holds every identifier the
/// source gave for the role (definition id, template id, display name) because the activation history
/// and the eligibility rarely name a role the same way; a match on any one of them is a match.
/// </summary>
public sealed record ActivationRecord(
    DateTimeOffset ActivatedAt,
    string PrincipalId,
    RoleSystem System,
    IReadOnlyList<string> RoleKeys,
    string? Scope);

/// <summary>
/// The activation history a scan could read. <see cref="Since"/>..<see cref="Until"/> is the window that
/// was asked for; the tenant's own audit-log retention (30 days by default) may be shorter, which is why
/// the rules only call an eligibility unused when it was granted before <see cref="Since"/>.
/// <see cref="Systems"/> lists the role systems whose history was readable — a system missing from it has
/// no history, and its eligibilities are skipped rather than reported as never used.
/// </summary>
public sealed record ActivationHistory(
    DateTimeOffset Since,
    DateTimeOffset Until,
    IReadOnlyList<RoleSystem> Systems,
    IReadOnlyList<ActivationRecord> Activations)
{
    public int Days => (int)Math.Round((Until - Since).TotalDays, MidpointRounding.AwayFromZero);

    public bool Covers(RoleSystem system) => Systems.Contains(system);
}

/// <summary>Everything one scan read. Immutable; the rules see nothing else.</summary>
public sealed record Snapshot(
    string Kind,
    string ToolVersion,
    TenantInfo Tenant,
    string Account,
    DateTimeOffset ScannedAt,
    IReadOnlyList<RoleDefinitionRecord> EntraRoleDefinitions,
    IReadOnlyList<EntraAssignmentRecord> EntraAssignments,
    IReadOnlyList<EntraAssignmentRecord> EntraEligibilities,
    IReadOnlyList<GroupRecord> Groups,
    IReadOnlyList<PrincipalRecord> Principals,
    IReadOnlyList<AzureScopeRecord> AzureScopes,
    IReadOnlyList<AzureRoleDefinitionRecord> AzureRoleDefinitions,
    IReadOnlyList<AzureAssignmentRecord> AzureAssignments,
    IReadOnlyList<AzureAssignmentRecord> AzureEligibilities,
    IReadOnlyList<SkippedSource> Skipped)
{
    public const string KindMarker = "elevate-audit-snapshot";

    /// <summary>
    /// What the activation lookback read, or null when no history was readable at all. An init-only
    /// property rather than a constructor parameter so a snapshot saved by an older build still loads.
    /// </summary>
    public ActivationHistory? Activations { get; init; }

    public static Snapshot Empty(TenantInfo tenant, string account, DateTimeOffset scannedAt, string toolVersion = "0.0.0") =>
        new(KindMarker, toolVersion, tenant, account, scannedAt, [], [], [], [], [], [], [], [], [], []);
}
