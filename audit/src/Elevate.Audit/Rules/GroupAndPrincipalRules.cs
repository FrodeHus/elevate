using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

public sealed class GroupMemberPermanentRule : IRule
{
    public string Code => "GROUP-MEMBER-PERMANENT";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var group in context.Snapshot.Groups.Where(g => g.PimStatus == PimStatus.Onboarded))
        {
            foreach (var p in group.PimAssignments.Where(p => p.IsPermanent))
            {
                var principal = context.PrincipalRecordOf(p.PrincipalId);
                yield return new Finding(
                    Code,
                    Severity.High,
                    RuleContext.ToFinding(principal),
                    new FindingRole(group.Id, $"{group.DisplayName} ({p.AccessId})", null, true, RoleSystem.Group),
                    new FindingScope(group.Id, group.DisplayName, ScopeKind.Group),
                    [],
                    $"{group.DisplayName} is managed by PIM for Groups, but {principal.DisplayName ?? principal.Id} is a permanent {p.AccessId}. Convert the assignment to eligible.",
                    PortalLinks.GroupPim(group.Id),
                    new FindingEvidence(p.Id, p.StartDateTime, p.EndDateTime, p.AssignmentType, p.AccessId));
            }
        }
    }
}

public sealed class GuestPermanentRule : IRule
{
    public string Code => "GUEST-PERMANENT";

    private const string Tail = "Guests should hold privileged roles only as eligible, if at all.";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var h in context.PermanentEntraHolders().Where(h => h.Principal.IsGuest))
        {
            var role = context.EntraRole(h.Assignment.RoleDefinitionId);
            yield return new Finding(Code, Severity.High, RuleContext.ToFinding(h.Principal), role, RuleContext.EntraScope(h.Assignment), h.Via,
                Remedy(h.Principal.DisplayName ?? h.Principal.Id, role.DisplayName, h.Via, Tail),
                h.Via.Count == 0 ? PortalLinks.EntraRoles : PortalLinks.GroupPim(h.Via[0].Id), RuleContext.Evidence(h.Assignment));
        }

        foreach (var h in context.PermanentAzureHolders().Where(h => h.Principal.IsGuest))
        {
            var role = context.AzureFindingRole(h.Assignment.RoleDefinitionId);
            yield return new Finding(Code, Severity.High, RuleContext.ToFinding(h.Principal), role, context.AzureScope(h.Assignment.Scope), h.Via,
                Remedy(h.Principal.DisplayName ?? h.Principal.Id, role.DisplayName, h.Via, Tail),
                h.Via.Count == 0 ? PortalLinks.AzureScope(context.TenantId, h.Assignment.Scope) : PortalLinks.GroupPim(h.Via[0].Id), RuleContext.Evidence(h.Assignment));
        }
    }

    private static string Remedy(string name, string roleName, IReadOnlyList<GroupRef> via, string tail) =>
        $"Guest {name} holds {roleName} permanently{RuleContext.Through(via)}. {tail}";
}

public sealed class ServicePrincipalPermanentRule : IRule
{
    public string Code => "SP-PERMANENT";

    private const string RemedyTail = "PIM eligibility does not apply to workload identities; review whether the workload identity needs the role at all, scope it down, or replace it with a narrower custom role.";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var h in context.PermanentEntraHolders().Where(h => h.Principal.Type == PrincipalType.ServicePrincipal))
        {
            var role = context.EntraRole(h.Assignment.RoleDefinitionId);
            yield return new Finding(Code, Severity.Info, RuleContext.ToFinding(h.Principal), role, RuleContext.EntraScope(h.Assignment), h.Via,
                Remedy(h.Principal.DisplayName ?? h.Principal.Id, role.DisplayName, h.Via, RemedyTail),
                h.Via.Count == 0 ? PortalLinks.EntraRoles : PortalLinks.GroupPim(h.Via[0].Id), RuleContext.Evidence(h.Assignment));
        }

        foreach (var h in context.PermanentAzureHolders().Where(h => h.Principal.Type == PrincipalType.ServicePrincipal))
        {
            var role = context.AzureFindingRole(h.Assignment.RoleDefinitionId);
            yield return new Finding(Code, Severity.Info, RuleContext.ToFinding(h.Principal), role, context.AzureScope(h.Assignment.Scope), h.Via,
                Remedy(h.Principal.DisplayName ?? h.Principal.Id, role.DisplayName, h.Via, RemedyTail),
                h.Via.Count == 0 ? PortalLinks.AzureScope(context.TenantId, h.Assignment.Scope) : PortalLinks.GroupPim(h.Via[0].Id), RuleContext.Evidence(h.Assignment));
        }
    }

    private static string Remedy(string name, string roleName, IReadOnlyList<GroupRef> via, string tail) =>
        $"{name} holds {roleName} permanently{RuleContext.Through(via)}. {tail}";
}
