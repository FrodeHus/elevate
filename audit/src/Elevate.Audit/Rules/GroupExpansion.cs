using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

/// <summary>Breadth-first walk of direct members with a visited set: every user or service principal reachable from a group, with the path that reaches it first.</summary>
public sealed class GroupExpansion(IReadOnlyDictionary<string, GroupRecord> groups)
{
    public sealed record Member(string PrincipalId, PrincipalType Type, IReadOnlyList<GroupRef> Via);

    private readonly Dictionary<string, IReadOnlyList<Member>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Member> Expand(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        if (_cache.TryGetValue(groupId, out var cached))
        {
            return cached;
        }

        var result = new List<Member>();
        var seenPrincipals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, IReadOnlyList<GroupRef> Via)>();
        queue.Enqueue((groupId, []));
        while (queue.TryDequeue(out var current))
        {
            if (!visited.Add(current.Id) || !groups.TryGetValue(current.Id, out var group))
            {
                continue;
            }

            var via = new List<GroupRef>(current.Via) { new(group.Id, group.DisplayName) };
            foreach (var member in group.DirectMembers)
            {
                switch (member.Type)
                {
                    case PrincipalType.Group:
                        queue.Enqueue((member.Id, via));
                        break;
                    case PrincipalType.User or PrincipalType.ServicePrincipal:
                        if (seenPrincipals.Add(member.Id))
                        {
                            result.Add(new Member(member.Id, member.Type, via));
                        }

                        break;
                }
            }
        }

        _cache[groupId] = result;
        return result;
    }

    /// <summary>The group itself and every group nested under it.</summary>
    public IReadOnlyList<GroupRecord> NestedGroups(string groupId)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<GroupRecord>();
        var queue = new Queue<string>([groupId]);
        while (queue.TryDequeue(out var id))
        {
            if (!visited.Add(id) || !groups.TryGetValue(id, out var group))
            {
                continue;
            }

            result.Add(group);
            foreach (var m in group.DirectMembers.Where(m => m.Type == PrincipalType.Group))
            {
                queue.Enqueue(m.Id);
            }
        }

        return result;
    }
}
