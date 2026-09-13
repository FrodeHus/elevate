using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

public sealed class AzurePermanentRule : IRule
{
    public string Code => "AZURE-PERMANENT";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var a in context.PermanentAzureAssignments())
        {
            if (context.AzureSeverity(a.RoleDefinitionId) is not { } severity)
            {
                continue;
            }

            var role = context.AzureFindingRole(a.RoleDefinitionId);
            var scope = context.AzureScope(a.Scope);
            var principal = context.PrincipalRecordOf(a.PrincipalId);
            if (principal.Type == PrincipalType.Group || string.Equals(a.PrincipalType, "Group", StringComparison.OrdinalIgnoreCase))
            {
                yield return new Finding(Code, severity, RuleContext.ToFinding(principal), role, scope, [],
                    $"Make the group's {role.DisplayName} assignment on {scope.DisplayName} eligible in PIM for Azure resources, or govern the group's membership with PIM for Groups.",
                    PortalLinks.AzureScope(context.TenantId, a.Scope), RuleContext.Evidence(a));
                foreach (var m in context.Expansion.Expand(a.PrincipalId).Where(m => m.Type == PrincipalType.User))
                {
                    var member = context.PrincipalRecordOf(m.PrincipalId);
                    yield return new Finding(Code, severity, RuleContext.ToFinding(member), role, scope, m.Via,
                        $"{member.DisplayName ?? member.Id} holds {role.DisplayName} on {scope.DisplayName} permanently through {string.Join(" ← ", m.Via.Select(v => v.DisplayName))}.",
                        PortalLinks.AzureScope(context.TenantId, a.Scope), RuleContext.Evidence(a));
                }
            }
            else
            {
                // Unknown, ServicePrincipal, Device: still standing access, and reporting it is the
                // whole promise of the tool. SP-PERMANENT adds its own Info finding on top for a
                // resolved service principal.
                var name = principal.DisplayName ?? principal.Id;
                var remedy = principal.Type == PrincipalType.User
                    ? $"Remove the permanent {role.DisplayName} assignment on {scope.DisplayName} and make {name} eligible for it in PIM."
                    : $"Remove the permanent {role.DisplayName} assignment on {scope.DisplayName} or make {name} eligible for it in PIM; PIM eligibility does not apply to workload identities.";
                yield return new Finding(Code, severity, RuleContext.ToFinding(principal), role, scope, [], remedy,
                    PortalLinks.AzureScope(context.TenantId, a.Scope), RuleContext.Evidence(a));
            }
        }
    }
}

public sealed class EligibleNoEndRule : IRule
{
    public string Code => "ELIGIBLE-NO-END";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        const string remedy = "Give the eligibility an end date so it is reviewed, or cover it with an access review.";
        foreach (var e in context.Snapshot.EntraEligibilities.Where(e => e.EndDateTime is null && context.IsPrivilegedEntra(e.RoleDefinitionId)))
        {
            yield return new Finding(Code, Severity.Low, context.Principal(e.PrincipalId), context.EntraRole(e.RoleDefinitionId), RuleContext.EntraScope(e), [], remedy, PortalLinks.EntraRoles, RuleContext.Evidence(e));
        }

        foreach (var g in context.Snapshot.Groups)
        {
            foreach (var e in g.PimEligibilities.Where(e => e.EndDateTime is null))
            {
                yield return new Finding(Code, Severity.Low, context.Principal(e.PrincipalId), new FindingRole(g.Id, $"{g.DisplayName} ({e.AccessId})", null, true, RoleSystem.Group),
                    new FindingScope(g.Id, g.DisplayName, ScopeKind.Group), [], remedy, PortalLinks.GroupPim(g.Id), new FindingEvidence(e.Id, e.StartDateTime, e.EndDateTime, null, null));
            }
        }

        foreach (var e in context.Snapshot.AzureEligibilities.Where(e => e.EndDateTime is null && context.AzureSeverity(e.RoleDefinitionId) is not null))
        {
            yield return new Finding(Code, Severity.Low, context.Principal(e.PrincipalId), context.AzureFindingRole(e.RoleDefinitionId), context.AzureScope(e.Scope), [], remedy, PortalLinks.AzureScope(context.TenantId, e.Scope), RuleContext.Evidence(e));
        }
    }
}

public sealed class GlobalAdminCountRule : IRule
{
    public const string GlobalAdministratorTemplate = "62e90394-69f5-4237-9190-012177145e10";

    public string Code => "GA-COUNT";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var gaRoleIds = context.EntraRoles.Values.Where(r => string.Equals(r.TemplateId, GlobalAdministratorTemplate, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (gaRoleIds.Count == 0)
        {
            yield break;
        }

        var principals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var records = context.Snapshot.EntraAssignments.Where(a => a.IsPermanent && !string.Equals(a.MemberType, "Group", StringComparison.OrdinalIgnoreCase))
            .Concat(context.Snapshot.EntraEligibilities)
            .Where(a => gaRoleIds.Contains(a.RoleDefinitionId) && a.DirectoryScopeId == "/");
        foreach (var a in records)
        {
            var principal = context.PrincipalRecordOf(a.PrincipalId);
            if (principal.Type == PrincipalType.Group)
            {
                foreach (var m in context.Expansion.Expand(a.PrincipalId).Where(m => m.Type is PrincipalType.User or PrincipalType.ServicePrincipal))
                {
                    principals.Add(m.PrincipalId);
                }
            }
            else if (principal.Type is PrincipalType.User or PrincipalType.ServicePrincipal)
            {
                principals.Add(principal.Id);
            }
        }

        if (principals.Count is >= 2 and <= 5)
        {
            yield break;
        }

        var tenant = context.Snapshot.Tenant;
        var roleId = gaRoleIds.First();
        yield return new Finding(
            Code,
            Severity.Medium,
            new FindingPrincipal(tenant.Id, tenant.DisplayName ?? tenant.Id, null, PrincipalType.Unknown, false, null),
            context.EntraRole(roleId),
            new FindingScope("/", "Directory", ScopeKind.Directory),
            [],
            principals.Count < 2
                ? $"{principals.Count} principal can become Global Administrator. Microsoft recommends at least two (for break-glass) and at most five."
                : $"{principals.Count} principals can become Global Administrator (permanent or eligible). Microsoft recommends at most five; move the rest to narrower roles.",
            PortalLinks.EntraRoles,
            new FindingEvidence("global-administrator-count", null, null, null, null));
    }
}
