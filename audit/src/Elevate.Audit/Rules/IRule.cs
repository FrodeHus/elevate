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

public sealed record AzureHolder(AzureAssignmentRecord Assignment, PrincipalRecord Principal, IReadOnlyList<GroupRef> Via);

/// <summary>
/// One eligibility from any of the three role systems, flattened into what the unused-eligibility rules
/// need: the finding shape, when it was granted, and the keys that match it to the activation history.
/// </summary>
public sealed record EligibilityView(
    string Id,
    string PrincipalId,
    RoleSystem System,
    IReadOnlyList<string> RoleKeys,
    string? Scope,
    FindingRole Role,
    FindingScope FindingScope,
    bool Privileged,
    DateTimeOffset? StartDateTime,
    string PortalUrl,
    FindingEvidence Evidence);

/// <summary>
/// The activation history, indexed by principal. Matching is deliberately loose on the role — the audit
/// log and the eligibility rarely name a role the same way, so any overlapping key counts — and strict on
/// nothing else except scope, which is compared only when both sides carry one.
/// </summary>
public sealed class ActivationLookup
{
    private readonly ILookup<string, ActivationRecord> _byPrincipal;

    public ActivationLookup(ActivationHistory? history)
    {
        History = history;
        _byPrincipal = (history?.Activations ?? []).ToLookup(a => a.PrincipalId, StringComparer.OrdinalIgnoreCase);
    }

    public ActivationHistory? History { get; }

    /// <summary>True when the history covers this role system, so "no activation found" means "not used".</summary>
    public bool Covers(RoleSystem system) => History?.Covers(system) == true;

    /// <summary>When the eligibility was last activated inside the window, or null if never.</summary>
    public DateTimeOffset? LastActivation(EligibilityView eligibility)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        DateTimeOffset? last = null;
        foreach (var a in _byPrincipal[eligibility.PrincipalId])
        {
            if (a.System != eligibility.System || !Matches(a, eligibility))
            {
                continue;
            }

            if (last is not { } current || a.ActivatedAt > current)
            {
                last = a.ActivatedAt;
            }
        }

        return last;
    }

    private static bool Matches(ActivationRecord activation, EligibilityView eligibility) =>
        activation.RoleKeys.Any(k => eligibility.RoleKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
        && (activation.Scope is not { } scope || eligibility.Scope is not { } wanted || string.Equals(scope.TrimEnd('/'), wanted.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
}

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
        Activations = new ActivationLookup(WithCurrentActivations(snapshot));
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
    public ActivationLookup Activations { get; }

    /// <summary>
    /// The history the scan read, plus the activations the snapshot already proves on its own: a schedule
    /// instance that is <c>Activated</c> right now is use of the eligibility, whether or not the audit log
    /// reached back far enough to show it, and it is use as of now rather than of the instance's start date.
    /// Null (no history at all) stays null.
    /// </summary>
    private static ActivationHistory? WithCurrentActivations(Snapshot snapshot)
    {
        if (snapshot.Activations is not { } history)
        {
            return null;
        }

        var current = snapshot.EntraAssignments
            .Where(a => a.AssignmentType == Model.AssignmentType.Activated)
            .Select(a => new ActivationRecord(history.Until, a.PrincipalId, RoleSystem.Entra, [a.RoleDefinitionId], null))
            .Concat(snapshot.Groups.SelectMany(g => g.PimAssignments
                .Where(p => p.AssignmentType == Model.AssignmentType.Activated)
                .Select(p => new ActivationRecord(history.Until, p.PrincipalId, RoleSystem.Group, [g.Id], g.Id))))
            .Concat(snapshot.AzureAssignments
                .Where(a => a.AssignmentType == Model.AssignmentType.Activated)
                .Select(a => new ActivationRecord(history.Until, a.PrincipalId, RoleSystem.Azure, [Collectors.AzureCollector.ScopeDisplayName(a.RoleDefinitionId)], a.Scope)))
            .ToList();
        return current.Count == 0 ? history : history with { Activations = [.. history.Activations, .. current] };
    }

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

    /// <summary>
    /// Every eligibility the rules care about, across all three role systems, already filtered by
    /// privilege (and so by <c>--all-roles</c>). <see cref="EligibilityView.Privileged"/> is the role's own
    /// privilege, which <c>--all-roles</c> does not change, and drives severity.
    /// </summary>
    public IEnumerable<EligibilityView> Eligibilities()
    {
        foreach (var e in Snapshot.EntraEligibilities.Where(e => IsPrivilegedEntra(e.RoleDefinitionId)))
        {
            var role = EntraRole(e.RoleDefinitionId);
            var keys = new[] { e.RoleDefinitionId, role.TemplateId, role.DisplayName }.OfType<string>().Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            yield return new EligibilityView(
                e.Id, e.PrincipalId, RoleSystem.Entra, keys, null, role, EntraScope(e),
                EntraRoles.TryGetValue(e.RoleDefinitionId, out var definition) && definition.IsPrivileged,
                e.StartDateTime, PortalLinks.EntraRoles, Evidence(e));
        }

        foreach (var g in Snapshot.Groups)
        {
            foreach (var e in g.PimEligibilities)
            {
                yield return new EligibilityView(
                    e.Id, e.PrincipalId, RoleSystem.Group, [g.Id], g.Id,
                    new FindingRole(g.Id, $"{g.DisplayName} ({e.AccessId})", null, true, RoleSystem.Group),
                    new FindingScope(g.Id, g.DisplayName, ScopeKind.Group),
                    true, e.StartDateTime, PortalLinks.GroupPim(g.Id),
                    new FindingEvidence(e.Id, e.StartDateTime, e.EndDateTime, null, null));
            }
        }

        foreach (var e in Snapshot.AzureEligibilities.Where(e => AzureSeverity(e.RoleDefinitionId) is not null))
        {
            yield return new EligibilityView(
                e.Id, e.PrincipalId, RoleSystem.Azure, [Collectors.AzureCollector.ScopeDisplayName(e.RoleDefinitionId)], e.Scope,
                AzureFindingRole(e.RoleDefinitionId), AzureScope(e.Scope),
                AzureRole(e.RoleDefinitionId) is { } azureDefinition && Privilege.AzureSeverityFor(azureDefinition) is not null,
                e.StartDateTime, PortalLinks.AzureScope(TenantId, e.Scope), Evidence(e));
        }
    }

    /// <summary>Permanent privileged Entra assignments, excluding the per-member echoes of a group assignment.</summary>
    public IEnumerable<EntraAssignmentRecord> PermanentPrivilegedEntraAssignments() =>
        Snapshot.EntraAssignments.Where(a => a.IsPermanent && !string.Equals(a.MemberType, "Group", StringComparison.OrdinalIgnoreCase) && IsPrivilegedEntra(a.RoleDefinitionId));

    /// <summary>Permanent privileged Entra assignments (not the per-member echoes of a group assignment) and who holds them.</summary>
    public IEnumerable<EntraHolder> PermanentEntraHolders()
    {
        foreach (var a in PermanentPrivilegedEntraAssignments())
        {
            foreach (var holder in ExpandHolder(a.PrincipalId, (p, via) => new EntraHolder(a, p, via)))
            {
                yield return holder;
            }
        }
    }

    /// <summary>The group principals of permanent privileged Entra assignments, one per assignment.</summary>
    public IEnumerable<(EntraAssignmentRecord Assignment, GroupRecord? Group, PrincipalRecord Principal)> PermanentEntraGroupAssignments()
    {
        foreach (var a in PermanentPrivilegedEntraAssignments())
        {
            var principal = PrincipalRecordOf(a.PrincipalId);
            if (principal.Type == PrincipalType.Group)
            {
                yield return (a, Groups.GetValueOrDefault(a.PrincipalId), principal);
            }
        }
    }

    /// <summary>
    /// The holder shape shared by <see cref="PermanentEntraHolders"/> and <see cref="PermanentAzureHolders"/>:
    /// a group principal expands to its members (each with the path that reaches it), anything else holds directly.
    /// </summary>
    private IEnumerable<THolder> ExpandHolder<THolder>(string principalId, Func<PrincipalRecord, IReadOnlyList<GroupRef>, THolder> factory)
    {
        var principal = PrincipalRecordOf(principalId);
        if (principal.Type == PrincipalType.Group)
        {
            foreach (var m in Expansion.Expand(principalId))
            {
                yield return factory(PrincipalRecordOf(m.PrincipalId), m.Via);
            }
        }
        else
        {
            yield return factory(principal, []);
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
            if (AzureSeverity(a.RoleDefinitionId) is null)
            {
                continue;
            }

            foreach (var holder in ExpandHolder(a.PrincipalId, (p, via) => new AzureHolder(a, p, via)))
            {
                yield return holder;
            }
        }
    }
}
