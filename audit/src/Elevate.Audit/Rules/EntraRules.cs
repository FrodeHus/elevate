using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

public sealed class EntraUserPermanentRule : IRule
{
    public string Code => "ENTRA-USER-PERMANENT";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var h in context.PermanentEntraHolders().Where(h => h.Via.Count == 0 && h.Principal.Type == PrincipalType.User))
        {
            var role = context.EntraRole(h.Assignment.RoleDefinitionId);
            yield return new Finding(
                Code,
                Severity.High,
                RuleContext.ToFinding(h.Principal),
                role,
                RuleContext.EntraScope(h.Assignment),
                [],
                $"Remove the permanent {role.DisplayName} assignment and make {h.Principal.DisplayName ?? h.Principal.Id} eligible for it in PIM.",
                PortalLinks.EntraRoles,
                RuleContext.Evidence(h.Assignment));
        }
    }
}

public sealed class EntraGroupPermanentRule : IRule
{
    public string Code => "ENTRA-GROUP-PERMANENT";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var (assignment, group, principal) in context.PermanentEntraGroupAssignments())
        {
            var role = context.EntraRole(assignment.RoleDefinitionId);
            var name = principal.DisplayName ?? principal.Id;
            var remedy = group is { IsDynamic: true }
                ? $"{name} is a dynamic group, which PIM for Groups cannot govern. Move the {role.DisplayName} assignment to a static role-assignable group and make its members eligible."
                : $"Make {name}'s {role.DisplayName} assignment eligible, or keep it active and make the group's members eligible through PIM for Groups.";
            yield return new Finding(Code, Severity.High, RuleContext.ToFinding(principal), role, RuleContext.EntraScope(assignment), [], remedy, PortalLinks.Group(principal.Id), RuleContext.Evidence(assignment));

            foreach (var m in context.Expansion.Expand(assignment.PrincipalId).Where(m => m.Type == PrincipalType.User))
            {
                var member = context.PrincipalRecordOf(m.PrincipalId);
                yield return new Finding(
                    Code,
                    Severity.High,
                    RuleContext.ToFinding(member),
                    role,
                    RuleContext.EntraScope(assignment),
                    m.Via,
                    $"{member.DisplayName ?? member.Id} holds {role.DisplayName} permanently through {string.Join(" ← ", m.Via.Select(v => v.DisplayName))}. Make the membership or the group's assignment eligible.",
                    PortalLinks.Group(assignment.PrincipalId),
                    RuleContext.Evidence(assignment));
            }
        }
    }
}

public sealed class EntraGroupNotPimRule : IRule
{
    public string Code => "ENTRA-GROUP-NOT-PIM";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in context.Snapshot.EntraAssignments.Where(a => !string.Equals(a.MemberType, "Group", StringComparison.OrdinalIgnoreCase) && context.IsPrivilegedEntra(a.RoleDefinitionId)))
        {
            if (!context.Groups.TryGetValue(a.PrincipalId, out var group) || !group.IsAssignableToRole || group.PimStatus != PimStatus.NotOnboarded || !seen.Add(group.Id))
            {
                continue;
            }

            yield return new Finding(
                Code,
                Severity.Medium,
                context.Principal(group.Id),
                context.EntraRole(a.RoleDefinitionId),
                RuleContext.EntraScope(a),
                [],
                $"Onboard {group.DisplayName} to PIM for Groups so its membership can be eligible instead of permanent.",
                PortalLinks.GroupPim(group.Id),
                RuleContext.Evidence(a));
        }
    }
}

public sealed class EntraGroupNotAssignableRule : IRule
{
    public string Code => "ENTRA-GROUP-NOT-ASSIGNABLE";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in context.Snapshot.EntraAssignments.Where(a => !string.Equals(a.MemberType, "Group", StringComparison.OrdinalIgnoreCase) && context.IsPrivilegedEntra(a.RoleDefinitionId)))
        {
            if (!context.Groups.TryGetValue(a.PrincipalId, out var group) || group.IsAssignableToRole || !seen.Add(group.Id))
            {
                continue;
            }

            yield return new Finding(
                Code,
                Severity.Medium,
                context.Principal(group.Id),
                context.EntraRole(a.RoleDefinitionId),
                RuleContext.EntraScope(a),
                [],
                $"{group.DisplayName} is not role-assignable, so PIM for Groups cannot govern it. Recreate it as a role-assignable group and move the assignment.",
                PortalLinks.Group(group.Id),
                RuleContext.Evidence(a));
        }
    }
}
