using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

/// <summary>One group in a card's membership trie: the people reached at exactly this group, and the groups nested under it.</summary>
public sealed class GroupNode(string id, string name)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public List<GroupNode> Children { get; } = [];
    public List<Finding> People { get; } = [];
    public int TotalPeople => People.Count + Children.Sum(c => c.TotalPeople);
    public int GroupCount => 1 + Children.Sum(c => c.GroupCount);
}

/// <summary>
/// One group that grants <see cref="Role"/> on <see cref="Scope"/>: the group-level finding when the rule emitted one,
/// every per-person finding reached through the group, and the trie of nested groups.
/// </summary>
public sealed record RollupCard(
    FindingPrincipal Group,
    FindingRole Role,
    FindingScope Scope,
    string Remedy,
    string PortalUrl,
    Finding? GroupFinding,
    IReadOnlyList<Finding> Members,
    GroupNode Tree)
{
    public int NestedGroups => Tree.GroupCount - 1;
    public int People => Members.Count;
}

/// <summary>Folds per-person findings under the group that grants the access. A rendering concern only; findings and JSON are untouched.</summary>
public static class GroupRollup
{
    private const string PimGroupRule = "GROUP-MEMBER-PERMANENT";

    public static bool RollsUp(string ruleCode) =>
        ruleCode is not null && (Is(ruleCode, "ENTRA-GROUP-PERMANENT") || Is(ruleCode, "AZURE-PERMANENT") || Is(ruleCode, PimGroupRule));

    /// <summary>Cards in first-seen order, plus the findings that stay as plain rows (direct, non-group principals with no path).</summary>
    public static (IReadOnlyList<RollupCard> Cards, IReadOnlyList<Finding> Rows) Build(string ruleCode, IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(ruleCode);
        ArgumentNullException.ThrowIfNull(findings);
        var pimGroup = Is(ruleCode, PimGroupRule);
        var order = new List<string>();
        var groupFindings = new Dictionary<string, Finding>(StringComparer.OrdinalIgnoreCase);
        var members = new Dictionary<string, List<Finding>>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<Finding>();
        foreach (var f in findings)
        {
            string key;
            var isGroupFinding = false;
            if (pimGroup)
            {
                // GROUP-MEMBER-PERMANENT gives the member and owner roles the same id (the group id); the display name tells them apart.
                key = Key(f.Scope.Id, f.Role.DisplayName, f.Scope.Id);
            }
            else if (f.Via.Count > 0)
            {
                key = Key(f.Via[0].Id, f.Role.Id, f.Scope.Id);
            }
            else if (f.Principal.Type == PrincipalType.Group)
            {
                key = Key(f.Principal.Id, f.Role.Id, f.Scope.Id);
                isGroupFinding = true;
            }
            else
            {
                rows.Add(f);
                continue;
            }

            if (!members.ContainsKey(key))
            {
                order.Add(key);
                members[key] = [];
            }

            if (isGroupFinding)
            {
                groupFindings.TryAdd(key, f);
            }
            else
            {
                members[key].Add(f);
            }
        }

        var cards = new List<RollupCard>(order.Count);
        foreach (var key in order)
        {
            var ms = members[key];
            var groupFinding = groupFindings.GetValueOrDefault(key);
            var first = groupFinding ?? ms[0];
            var group = groupFinding?.Principal ?? (pimGroup
                ? new FindingPrincipal(first.Scope.Id, first.Scope.DisplayName, null, PrincipalType.Group, false, null)
                : new FindingPrincipal(first.Via[0].Id, first.Via[0].DisplayName, null, PrincipalType.Group, false, null));
            cards.Add(new RollupCard(group, first.Role, first.Scope, first.Remedy, first.PortalUrl, groupFinding, ms, Tree(group, ms)));
        }

        return (cards, rows);
    }

    /// <summary>A trie of the members' <c>Via</c> paths below the assigned group; each person hangs off the last group in their path.</summary>
    public static GroupNode Tree(FindingPrincipal group, IReadOnlyList<Finding> members)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(members);
        var root = new GroupNode(group.Id, group.DisplayName);
        foreach (var m in members)
        {
            var node = root;
            foreach (var g in m.Via.Skip(1))
            {
                var child = node.Children.FirstOrDefault(c => string.Equals(c.Id, g.Id, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    child = new GroupNode(g.Id, g.DisplayName);
                    node.Children.Add(child);
                }

                node = child;
            }

            node.People.Add(m);
        }

        return root;
    }

    private static string Key(string group, string role, string scope) => $"{group}|{role}|{scope}";

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
