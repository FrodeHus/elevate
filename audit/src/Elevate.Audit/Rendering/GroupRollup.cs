using System.Globalization;
using System.Net;
using System.Text;
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

    public const int MaxDiagramGroups = 12;
    public const int MaxInlinePeople = 10;

    private const int NodeW = 140;
    private const int NodeH = 46;
    private const int TierX = 180;
    private const int RowY = 66;
    private const int Pad = 10;

    /// <summary>The membership outline: role, assigned group, nested groups with counts; people inlined for groups with at most <see cref="MaxInlinePeople"/> direct people.</summary>
    public static string Outline(RollupCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var b = new StringBuilder();
        b.Append("<ul class=\"tree\"><li class=\"role\"><span class=\"name\">").Append(E(card.Role.DisplayName)).Append("</span><span class=\"cnt\">").Append(E(card.Scope.DisplayName)).Append("</span><ul>");
        Node(b, card.Tree);
        b.Append("</ul></li></ul>");
        return b.ToString();
    }

    private static void Node(StringBuilder b, GroupNode n)
    {
        b.Append("<li class=\"grp\"><span class=\"name\">").Append(E(n.Name)).Append("</span><span class=\"cnt\">")
         .Append(ReportAreas.Plural(n.People.Count, "person", "people")).Append(" direct");
        if (n.Children.Count > 0)
        {
            b.Append(" · ").Append(ReportAreas.Plural(n.Children.Count, "nested group", "nested groups"));
        }

        b.Append("</span>");
        if (n.People.Count > 0 || n.Children.Count > 0)
        {
            b.Append("<ul>");
            if (n.People.Count <= MaxInlinePeople)
            {
                foreach (var p in n.People)
                {
                    b.Append("<li class=\"person\"><span class=\"name\">").Append(E(p.Principal.DisplayName)).Append("</span>")
                     .Append(p.Principal.IsGuest ? "<span class=\"pill\">guest</span>" : string.Empty).Append("</li>");
                }
            }
            else
            {
                b.Append("<li class=\"person muted\">").Append(n.People.Count.ToString(CultureInfo.InvariantCulture)).Append(" people, listed below</li>");
            }

            foreach (var c in n.Children)
            {
                Node(b, c);
            }

            b.Append("</ul>");
        }

        b.Append("</li>");
    }

    /// <summary>
    /// A left-to-right tiered diagram of the role and the groups (never people). Null when the group grants directly
    /// or the trie has more than <see cref="MaxDiagramGroups"/> groups.
    /// </summary>
    public static string? Diagram(RollupCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.NestedGroups == 0 || card.Tree.GroupCount > MaxDiagramGroups)
        {
            return null;
        }

        var placed = new List<(GroupNode Node, int Depth, double Row, GroupNode? Parent)>();
        var nextRow = 0;
        double Place(GroupNode n, int depth, GroupNode? parent)
        {
            double row;
            if (n.Children.Count == 0)
            {
                row = nextRow++;
            }
            else
            {
                var rows = n.Children.Select(c => Place(c, depth + 1, n)).ToList();
                row = (rows[0] + rows[^1]) / 2;
            }

            placed.Add((n, depth, row, parent));
            return row;
        }

        var rootRow = Place(card.Tree, 1, null);
        var maxDepth = placed.Max(p => p.Depth);
        var width = Pad * 2 + maxDepth * TierX + NodeW;
        var height = Pad * 2 + (nextRow - 1) * RowY + NodeH;
        var b = new StringBuilder();
        b.Append("<svg class=\"nesting\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height).Append("\" width=\"").Append(width).Append("\" height=\"").Append(height)
         .Append("\" role=\"img\" aria-label=\"How the groups nest\" font-family=\"-apple-system,BlinkMacSystemFont,Segoe UI,Helvetica,Arial,sans-serif\">");

        // Edges first so nodes paint over them.
        var pos = placed.ToDictionary(p => p.Node, p => (X: Pad + p.Depth * TierX, Y: Pad + (int)Math.Round(p.Row * RowY)));
        var roleX = Pad;
        var roleY = Pad + (int)Math.Round(rootRow * RowY);
        Edge(b, roleX + NodeW, roleY + NodeH / 2, pos[card.Tree].X, pos[card.Tree].Y + NodeH / 2);
        foreach (var p in placed.Where(p => p.Parent is not null))
        {
            var from = pos[p.Parent!];
            var to = pos[p.Node];
            Edge(b, from.X + NodeW, from.Y + NodeH / 2, to.X, to.Y + NodeH / 2);
        }

        Rect(b, roleX, roleY, card.Role.DisplayName, E(card.Role.System == RoleSystem.Azure ? "Azure role" : "Entra role"), "role");
        foreach (var p in placed)
        {
            var (x, y) = pos[p.Node];
            Rect(b, x, y, p.Node.Name, ReportAreas.Plural(p.Node.People.Count, "person", "people"), "group");
        }

        b.Append("</svg>");
        return b.ToString();
    }

    private static void Edge(StringBuilder b, int x1, int y1, int x2, int y2)
    {
        var mx = (x1 + x2) / 2;
        b.Append("<path d=\"M").Append(x1).Append(' ').Append(y1).Append(" C ").Append(mx).Append(' ').Append(y1).Append(", ").Append(mx).Append(' ').Append(y2).Append(", ").Append(x2).Append(' ').Append(y2)
         .Append("\" fill=\"none\" stroke=\"#dce3ec\" stroke-width=\"1.5\"/>");
    }

    private static void Rect(StringBuilder b, int x, int y, string label, string sub, string kind)
    {
        var (fill, stroke) = kind == "role" ? ("#fee4e2", "#b42318") : ("#fff", "#075bd8");
        var shown = label.Length > 18 ? label[..17] + "…" : label;
        b.Append("<g class=\"").Append(kind).Append("\"><title>").Append(E(label)).Append("</title>")
         .Append("<rect x=\"").Append(x).Append("\" y=\"").Append(y).Append("\" width=\"").Append(NodeW).Append("\" height=\"").Append(NodeH).Append("\" rx=\"10\" fill=\"").Append(fill).Append("\" stroke=\"").Append(stroke).Append("\" stroke-width=\"1.5\"/>")
         .Append("<text x=\"").Append(x + 12).Append("\" y=\"").Append(y + 19).Append("\" font-size=\"13\" font-weight=\"600\" fill=\"#101d32\">").Append(E(shown)).Append("</text>")
         .Append("<text x=\"").Append(x + 12).Append("\" y=\"").Append(y + 36).Append("\" font-size=\"11.5\" fill=\"#526176\">").Append(sub).Append("</text></g>");
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
