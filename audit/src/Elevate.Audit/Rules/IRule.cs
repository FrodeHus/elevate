using Elevate.Audit.Collectors;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

public interface IRule
{
    string Code { get; }

    IEnumerable<Finding> Evaluate(RuleContext context);
}

/// <summary>A permanent privileged assignment and one principal that holds it, directly (<see cref="Via"/> empty) or through groups.</summary>
public sealed record EntraHolder(EntraAssignmentRecord Assignment, PrincipalRecord Principal, IReadOnlyList<GroupRef> Via);

public sealed record AzureHolder(AzureAssignmentRecord Assignment, PrincipalRecord Principal, IReadOnlyList<GroupRef> Via, Severity Severity);

/// <summary>The snapshot with indexes and the shared lookups every rule needs. Built once per run.</summary>
public sealed class RuleContext
{
    public RuleContext(Snapshot snapshot, AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
        Options = options ?? new AuditOptions();
        Principals = snapshot.Principals.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        EntraRoles = snapshot.EntraRoleDefinitions.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Groups = snapshot.Groups.GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        AzureRoles = snapshot.AzureRoleDefinitions.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Expansion = new GroupExpansion(Groups);
    }

    public Snapshot Snapshot { get; }
    public AuditOptions Options { get; }
    public string TenantId => Snapshot.Tenant.Id;
    public IReadOnlyDictionary<string, PrincipalRecord> Principals { get; }
    public IReadOnlyDictionary<string, RoleDefinitionRecord> EntraRoles { get; }
    public IReadOnlyDictionary<string, GroupRecord> Groups { get; }
    /// <summary>Azure role definitions by their GUID name (the last segment of a roleDefinitionId).</summary>
    public IReadOnlyDictionary<string, AzureRoleDefinitionRecord> AzureRoles { get; }
    public GroupExpansion Expansion { get; }

    public bool IsPrivilegedEntra(string roleDefinitionId) =>
        Options.AllRoles || (EntraRoles.TryGetValue(roleDefinitionId, out var role) && role.IsPrivileged);

    /// <summary>Null when the role is not privileged (and <c>--all-roles</c> is off); Medium for an unlisted role under <c>--all-roles</c>.</summary>
    public Severity? AzureSeverity(string roleDefinitionId)
    {
        var role = AzureRole(roleDefinitionId);
        var listed = role is null ? null : Privilege.AzureSeverityFor(role);
        return listed ?? (Options.AllRoles ? Severity.Medium : null);
    }

    public AzureRoleDefinitionRecord? AzureRole(string roleDefinitionId) =>
        AzureRoles.TryGetValue(AzureCollector.ScopeDisplayName(roleDefinitionId), out var role) ? role : null;

    public PrincipalRecord PrincipalRecordOf(string id)
    {
        if (Principals.TryGetValue(id, out var p))
        {
            return p;
        }

        if (Groups.TryGetValue(id, out var g))
        {
            return new PrincipalRecord(g.Id, PrincipalType.Group, g.DisplayName, null, false, null, null);
        }

        return new PrincipalRecord(id, PrincipalType.Unknown, null, null, false, null, null);
    }

    public FindingPrincipal Principal(string id) => ToFinding(PrincipalRecordOf(id));

    public static FindingPrincipal ToFinding(PrincipalRecord p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new FindingPrincipal(p.Id, p.DisplayName ?? p.UserPrincipalName ?? $"<unknown principal {p.Id}>", p.UserPrincipalName, p.Type, p.IsGuest, p.AccountEnabled);
    }

    public FindingRole EntraRole(string roleDefinitionId) =>
        EntraRoles.TryGetValue(roleDefinitionId, out var r)
            ? new FindingRole(r.Id, r.DisplayName, r.TemplateId, r.IsPrivileged, RoleSystem.Entra)
            : new FindingRole(roleDefinitionId, roleDefinitionId, null, Options.AllRoles, RoleSystem.Entra);

    public FindingRole AzureFindingRole(string roleDefinitionId)
    {
        var role = AzureRole(roleDefinitionId);
        return new FindingRole(roleDefinitionId, role?.DisplayName ?? AzureCollector.ScopeDisplayName(roleDefinitionId), null, AzureSeverity(roleDefinitionId) is not null, RoleSystem.Azure);
    }

    public static FindingScope EntraScope(EntraAssignmentRecord a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.AppScopeId is { } app)
        {
            return new FindingScope(app, app, ScopeKind.Application);
        }

        return a.DirectoryScopeId == "/"
            ? new FindingScope("/", "Directory", ScopeKind.Directory)
            : new FindingScope(a.DirectoryScopeId, a.DirectoryScopeId.TrimStart('/').Replace("administrativeUnits/", "AU ", StringComparison.OrdinalIgnoreCase), ScopeKind.AdministrativeUnit);
    }

    public FindingScope AzureScope(string scope)
    {
        if (string.IsNullOrEmpty(scope) || scope == "/")
        {
            return new FindingScope("/", "Tenant root", ScopeKind.ManagementGroup);
        }

        var known = Snapshot.AzureScopes.FirstOrDefault(s => s.Id.Equals(scope, StringComparison.OrdinalIgnoreCase));
        var kind = AzureCollector.ScopeKindOf(scope) switch
        {
            AzureScopeKind.ManagementGroup => ScopeKind.ManagementGroup,
            AzureScopeKind.Subscription => ScopeKind.Subscription,
            AzureScopeKind.ResourceGroup => ScopeKind.ResourceGroup,
            _ => ScopeKind.Resource,
        };
        return new FindingScope(scope, known?.DisplayName ?? AzureCollector.ScopeDisplayName(scope), kind);
    }

    public static FindingEvidence Evidence(EntraAssignmentRecord a) => new(a.Id, a.StartDateTime, a.EndDateTime, a.AssignmentType, a.MemberType);

    public static FindingEvidence Evidence(AzureAssignmentRecord a) => new(a.Id, a.StartDateTime, a.EndDateTime, a.AssignmentType, a.FromSchedule ? "Schedule" : "Classic");

    /// <summary>Renders "" for a direct holder or " through A ← B" for one reached through nested groups.</summary>
    public static string Through(IReadOnlyList<GroupRef> via) => via.Count == 0 ? string.Empty : $" through {string.Join(" ← ", via.Select(v => v.DisplayName))}";

    /// <summary>Permanent privileged Entra assignments (not the per-member echoes of a group assignment) and who holds them.</summary>
    public IEnumerable<EntraHolder> PermanentEntraHolders()
    {
        foreach (var a in Snapshot.EntraAssignments.Where(a => a.IsPermanent && !string.Equals(a.MemberType, "Group", StringComparison.OrdinalIgnoreCase) && IsPrivilegedEntra(a.RoleDefinitionId)))
        {
            var principal = PrincipalRecordOf(a.PrincipalId);
            if (principal.Type == PrincipalType.Group)
            {
                foreach (var m in Expansion.Expand(a.PrincipalId))
                {
                    yield return new EntraHolder(a, PrincipalRecordOf(m.PrincipalId), m.Via);
                }
            }
            else
            {
                yield return new EntraHolder(a, principal, []);
            }
        }
    }

    /// <summary>The group principals of permanent privileged Entra assignments, one per assignment.</summary>
    public IEnumerable<(EntraAssignmentRecord Assignment, GroupRecord? Group, PrincipalRecord Principal)> PermanentEntraGroupAssignments()
    {
        foreach (var a in Snapshot.EntraAssignments.Where(a => a.IsPermanent && !string.Equals(a.MemberType, "Group", StringComparison.OrdinalIgnoreCase) && IsPrivilegedEntra(a.RoleDefinitionId)))
        {
            var principal = PrincipalRecordOf(a.PrincipalId);
            if (principal.Type == PrincipalType.Group)
            {
                yield return (a, Groups.GetValueOrDefault(a.PrincipalId), principal);
            }
        }
    }

    /// <summary>
    /// Permanent Azure assignments: a classic assignment with no <c>Activated</c> schedule instance behind it
    /// (and no time-bound <c>Assigned</c> one), or a schedule-only <c>Assigned</c> instance without an end.
    /// </summary>
    public IEnumerable<AzureAssignmentRecord> PermanentAzureAssignments()
    {
        static (string Scope, string RoleGuid, string PrincipalId) Key(AzureAssignmentRecord a) =>
            (a.Scope.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed.ToUpperInvariant() : "/",
             AzureCollector.ScopeDisplayName(a.RoleDefinitionId).ToUpperInvariant(),
             a.PrincipalId.ToUpperInvariant());
        var schedules = Snapshot.AzureAssignments.Where(a => a.FromSchedule).ToLookup(Key);
        var classicKeys = new HashSet<(string Scope, string RoleGuid, string PrincipalId)>(Snapshot.AzureAssignments.Where(a => !a.FromSchedule).Select(Key));
        foreach (var classic in Snapshot.AzureAssignments.Where(a => !a.FromSchedule))
        {
            var behind = schedules[Key(classic)].ToList();
            if (behind.Any(s => s.AssignmentType == AssignmentType.Activated) || behind.Any(s => s.AssignmentType == AssignmentType.Assigned && s.EndDateTime is not null))
            {
                continue;
            }

            yield return classic;
        }

        foreach (var schedule in Snapshot.AzureAssignments.Where(a => a.FromSchedule && a.AssignmentType == AssignmentType.Assigned && a.EndDateTime is null && !classicKeys.Contains(Key(a))))
        {
            yield return schedule;
        }
    }

    public IEnumerable<AzureHolder> PermanentAzureHolders()
    {
        foreach (var a in PermanentAzureAssignments())
        {
            if (AzureSeverity(a.RoleDefinitionId) is not { } severity)
            {
                continue;
            }

            var principal = PrincipalRecordOf(a.PrincipalId);
            if (principal.Type == PrincipalType.Group)
            {
                foreach (var m in Expansion.Expand(a.PrincipalId))
                {
                    yield return new AzureHolder(a, PrincipalRecordOf(m.PrincipalId), m.Via, severity);
                }
            }
            else
            {
                yield return new AzureHolder(a, principal, [], severity);
            }
        }
    }
}
