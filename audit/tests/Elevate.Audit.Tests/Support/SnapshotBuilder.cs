using Elevate.Audit.Model;

namespace Elevate.Audit.Tests.Support;

/// <summary>Fluent fixture for rule and renderer tests. Ids are short strings; the rules never parse them.</summary>
public sealed class SnapshotBuilder
{
    public const string GlobalAdminTemplate = "62e90394-69f5-4237-9190-012177145e10";
    public const string AzureRolePrefix = "/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/";

    private readonly List<RoleDefinitionRecord> _roles = [];
    private readonly List<EntraAssignmentRecord> _assignments = [];
    private readonly List<EntraAssignmentRecord> _eligibilities = [];
    private readonly Dictionary<string, GroupRecord> _groups = new(StringComparer.Ordinal);
    private readonly List<PrincipalRecord> _principals = [];
    private readonly List<AzureScopeRecord> _azureScopes = [];
    private readonly List<AzureRoleDefinitionRecord> _azureRoles = [];
    private readonly List<AzureAssignmentRecord> _azureAssignments = [];
    private readonly List<AzureAssignmentRecord> _azureEligibilities = [];
    private readonly List<SkippedSource> _skipped = [];
    private TenantInfo _tenant = new("11111111-1111-1111-1111-111111111111", "Contoso");

    public static SnapshotBuilder Contoso() => new SnapshotBuilder()
        .EntraRole("rd-ga", GlobalAdminTemplate, "Global Administrator", privileged: true)
        .EntraRole("rd-pra", "e8611ab8-c189-46e8-94e1-60213ab1f814", "Privileged Role Administrator", privileged: true)
        .EntraRole("rd-reader", "f2ef992c-3afb-46b9-b7cf-a126ee74c451", "Global Reader", privileged: false)
        .AzureRole("8e3af657-a8ff-443c-a75c-2fe8c4bcb635", "Owner", actions: "*")
        .AzureRole("b24988ac-6180-42a0-ab88-20f7382dd24c", "Contributor", actions: "*")
        .AzureRole("acdd72a7-3385-48ef-bd42-f606fba81ae7", "Reader", actions: "*/read")
        .AzureScope("/subscriptions/sub1", AzureScopeKind.Subscription, "Production");

    public SnapshotBuilder Tenant(string id, string? name) { _tenant = new TenantInfo(id, name); return this; }

    public SnapshotBuilder User(string id, string name, string upn, bool guest = false, bool? enabled = true)
    {
        _principals.Add(new PrincipalRecord(id, PrincipalType.User, name, upn, guest, enabled, null));
        return this;
    }

    public SnapshotBuilder ServicePrincipal(string id, string name, string type = "Application")
    {
        _principals.Add(new PrincipalRecord(id, PrincipalType.ServicePrincipal, name, null, false, true, type));
        return this;
    }

    public SnapshotBuilder Group(string id, string name, bool assignable = true, bool dynamic = false, PimStatus pim = PimStatus.Unknown, bool securityEnabled = true, bool mailEnabled = false, string? visibility = null, params (string Id, PrincipalType Type)[] members)
    {
        _groups[id] = new GroupRecord(id, name, assignable, dynamic, pim, members.Select(m => new GroupMemberRecord(m.Id, m.Type)).ToList(), [], [], securityEnabled, mailEnabled, visibility);
        return this;
    }

    public SnapshotBuilder EntraRole(string id, string? template, string name, bool privileged, bool builtIn = true)
    {
        _roles.Add(new RoleDefinitionRecord(id, template, name, privileged, builtIn));
        return this;
    }

    public SnapshotBuilder Assigned(string id, string principalId, string roleId, string scope = "/", DateTimeOffset? end = null, AssignmentType type = AssignmentType.Assigned, string memberType = "Direct")
    {
        _assignments.Add(new EntraAssignmentRecord(id, principalId, roleId, scope, null, type, memberType, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), end));
        return this;
    }

    public SnapshotBuilder Eligible(string id, string principalId, string roleId, DateTimeOffset? end = null)
    {
        _eligibilities.Add(new EntraAssignmentRecord(id, principalId, roleId, "/", null, null, "Direct", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), end));
        return this;
    }

    public SnapshotBuilder GroupPim(string groupId, string id, string principalId, string accessId = "member", AssignmentType? type = AssignmentType.Assigned, DateTimeOffset? end = null, bool eligible = false)
    {
        var group = _groups[groupId];
        var record = new GroupPimRecord(id, principalId, accessId, eligible ? null : type, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), end);
        _groups[groupId] = eligible
            ? group with { PimStatus = PimStatus.Onboarded, PimEligibilities = [.. group.PimEligibilities, record] }
            : group with { PimStatus = PimStatus.Onboarded, PimAssignments = [.. group.PimAssignments, record] };
        return this;
    }

    public SnapshotBuilder AzureScope(string id, AzureScopeKind kind, string name) { _azureScopes.Add(new AzureScopeRecord(id, kind, name)); return this; }

    public SnapshotBuilder AzureRole(string guid, string name, string type = "BuiltInRole", params string[] actions)
    {
        _azureRoles.Add(new AzureRoleDefinitionRecord(AzureRolePrefix + guid, guid, name, type, actions));
        return this;
    }

    public SnapshotBuilder AzureAssigned(string id, string scope, string roleGuid, string principalId, string principalType, AssignmentType? type = null, DateTimeOffset? end = null, bool fromSchedule = false, string? roleDefinitionId = null)
    {
        _azureAssignments.Add(new AzureAssignmentRecord(id, scope, roleDefinitionId ?? AzureRolePrefix + roleGuid, principalId, principalType, type, fromSchedule ? new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) : null, end, fromSchedule));
        return this;
    }

    public SnapshotBuilder AzureEligible(string id, string scope, string roleGuid, string principalId, string principalType, DateTimeOffset? end = null)
    {
        _azureEligibilities.Add(new AzureAssignmentRecord(id, scope, AzureRolePrefix + roleGuid, principalId, principalType, null, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), end, true));
        return this;
    }

    public SnapshotBuilder Skipped(string source, string reason) { _skipped.Add(new SkippedSource(source, reason)); return this; }

    public Snapshot Build() => new(
        Snapshot.KindMarker, "0.0.0-test", _tenant, "alex.rivera@contoso.com", new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero),
        _roles, _assignments, _eligibilities, _groups.Values.ToList(), _principals,
        _azureScopes, _azureRoles, _azureAssignments, _azureEligibilities, _skipped);
}
