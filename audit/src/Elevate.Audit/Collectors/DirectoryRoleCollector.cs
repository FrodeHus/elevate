using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Core.Catalogue;
using Elevate.Core.Models;
using Elevate.Core.Providers;

namespace Elevate.Audit.Collectors;

public sealed record DirectoryRoleData(
    IReadOnlyList<RoleDefinitionRecord> Definitions,
    IReadOnlyList<EntraAssignmentRecord> Assignments,
    IReadOnlyList<EntraAssignmentRecord> Eligibilities,
    IReadOnlyList<PrincipalRecord> Principals);

/// <summary>Entra directory roles: definitions, every active assignment instance, every eligibility instance.</summary>
public sealed class DirectoryRoleCollector(GraphTransport graph, Identity identity, string tenantId)
{
    internal sealed record WireDefinition(string Id, string? TemplateId, string? DisplayName, bool? IsPrivileged, bool? IsBuiltIn);

    internal sealed record WireInstance(
        string Id,
        string PrincipalId,
        string RoleDefinitionId,
        string? DirectoryScopeId,
        string? AppScopeId,
        string? AssignmentType,
        string? MemberType,
        DateTimeOffset? StartDateTime,
        DateTimeOffset? EndDateTime,
        Wire.WirePrincipal? Principal);

    public async Task<DirectoryRoleData> CollectAsync(CancellationToken ct)
    {
        var scopes = ClientIds.GraphReadScopes;
        var definitions = await graph.ListAllAsync<WireDefinition>(identity, tenantId, GraphUrls.RoleDefinitions, scopes, ct).ConfigureAwait(false);
        var assignments = await graph.ListAllAsync<WireInstance>(identity, tenantId, GraphUrls.RoleAssignmentInstances, scopes, ct).ConfigureAwait(false);
        var eligibilities = await graph.ListAllAsync<WireInstance>(identity, tenantId, GraphUrls.RoleEligibilityInstances, scopes, ct).ConfigureAwait(false);

        var catalogue = RoleCatalogue.EntraBuiltInRoles().ToDictionary(r => r.TemplateId, r => r.IsPrivileged, StringComparer.OrdinalIgnoreCase);
        var principals = new Dictionary<string, PrincipalRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in assignments.Concat(eligibilities))
        {
            if (instance.Principal is { } p)
            {
                principals.TryAdd(p.Id, Wire.ToRecord(p));
            }
        }

        return new DirectoryRoleData(
            definitions.Select(d => new RoleDefinitionRecord(
                d.Id,
                d.TemplateId,
                d.DisplayName ?? d.Id,
                d.IsPrivileged ?? (d.TemplateId is { } t && catalogue.TryGetValue(t, out var flagged) && flagged),
                d.IsBuiltIn ?? false)).ToList(),
            assignments.Select(Map).ToList(),
            eligibilities.Select(Map).ToList(),
            principals.Values.ToList());
    }

    private static EntraAssignmentRecord Map(WireInstance i) => new(
        i.Id,
        i.PrincipalId,
        i.RoleDefinitionId,
        i.DirectoryScopeId ?? "/",
        i.AppScopeId,
        Wire.AssignmentTypeOf(i.AssignmentType),
        i.MemberType,
        i.StartDateTime,
        i.EndDateTime);
}
