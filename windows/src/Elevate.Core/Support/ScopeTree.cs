using Elevate.Core.Models;

namespace Elevate.Core.Support;

/// <summary>
/// One scope in the Azure tab's tree: the roles held directly on it, and the scopes below it that
/// hold roles of their own. Built from the eligibilities alone — see <see cref="ArmScope"/>.
/// </summary>
public sealed class ScopeNode
{
    internal ScopeNode(ArmScopeKind kind, string name, string scope)
    {
        Kind = kind;
        Name = name;
        Scope = scope;
    }

    public ArmScopeKind Kind { get; }

    /// <summary>The ARM name: a subscription id, a resource group name.</summary>
    public string Name { get; }

    /// <summary>The whole scope string this node stands for.</summary>
    public string Scope { get; }

    /// <summary>
    /// The scope's friendly name, when some eligibility sits on this exact scope and the service
    /// told us one. A subscription that is only an ancestor here has no caption to borrow, so it
    /// shows its id.
    /// </summary>
    public string? DisplayName { get; internal set; }

    /// <summary>What to show: the friendly name when there is one, otherwise the ARM name.</summary>
    public string Title => DisplayName ?? Name;

    /// <summary>
    /// Scopes above this one that hold no role and lead nowhere else, outermost first. A
    /// subscription whose only content is one resource group is not worth a row and a level of
    /// indent of its own in a panel this narrow, so it is folded into the node below and named
    /// here instead — the view draws it as a dimmed "Alpha /" ahead of the title.
    /// </summary>
    public IReadOnlyList<string> Ancestors { get; internal set; } = [];

    /// <summary>The folded scopes and this one, as one line: "Alpha / prod".</summary>
    public string Path => Ancestors.Count == 0 ? Title : string.Join(" / ", Ancestors.Append(Title));

    /// <summary>Roles held on this exact scope.</summary>
    public IReadOnlyList<EligibleRole> Roles { get; internal set; } = [];

    public IReadOnlyList<ScopeNode> Children { get; internal set; } = [];

    /// <summary>Every role at or below this node.</summary>
    public int RoleCount => Roles.Count + Children.Sum(c => c.RoleCount);

    /// <summary>Every role at or below this node, in the order the tree shows them.</summary>
    public IEnumerable<EligibleRole> AllRoles => Roles.Concat(Children.SelectMany(c => c.AllRoles));
}

/// <summary>One line of the flattened tree: a scope header, or a role beneath one.</summary>
public sealed record ScopeTreeEntry(int Depth, ScopeNode? Node, EligibleRole? Role)
{
    public bool IsScope => Node is not null && Role is null;
}

/// <summary>
/// Turns a tenant's Azure eligibilities into the management group / subscription / resource group /
/// resource tree the panel draws, and flattens it back into rows. Kept out of the view so both the
/// row builder and the tests can reach it.
/// </summary>
public static class ScopeTree
{
    /// <summary>
    /// The tree for <paramref name="roles"/>, roots first. Roles that are not Azure resource roles
    /// are ignored; the caller has already narrowed to the Azure tab.
    /// </summary>
    public static IReadOnlyList<ScopeNode> Build(IEnumerable<EligibleRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var byScope = new Dictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<Builder>();

        foreach (var role in roles)
        {
            if (role.Key.Scope is not AzureResourceScope azure)
            {
                continue;
            }

            var segments = ArmScope.Segments(azure.Scope);
            if (segments.Count == 0)
            {
                continue;
            }

            Builder? parent = null;
            foreach (var segment in segments)
            {
                if (!byScope.TryGetValue(segment.Scope, out var node))
                {
                    node = new Builder(segment.Kind, segment.Name, segment.Scope);
                    byScope[segment.Scope] = node;
                    if (parent is null)
                    {
                        roots.Add(node);
                    }
                    else
                    {
                        parent.Children.Add(node);
                    }
                }

                parent = node;
            }

            // The caption the service gave names this role's own scope, not its ancestors.
            parent!.Roles.Add(role);
            parent.DisplayName ??= ArmScope.DisplayName(role.Detail);
        }

        return [.. roots.Select(Freeze).OrderBy(n => n.Kind).ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The tree as rows, outermost first, skipping the children of a collapsed node.
    /// <paramref name="isCollapsed"/> is asked once per node; while the panel is filtering the
    /// caller passes a predicate that is always false, so every match stays visible in place.
    /// <para>
    /// A scope that only leads to one role and branches nowhere is left out and its role row takes
    /// its place: sixty subscriptions with one eligibility each should not become a hundred and
    /// twenty rows. The role row still carries the scope's caption, as it does today.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ScopeTreeEntry> Flatten(
        IEnumerable<ScopeNode> nodes, Func<ScopeNode, bool>? isCollapsed = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var rows = new List<ScopeTreeEntry>();
        Walk(nodes, 0, isCollapsed ?? (_ => false), rows);
        return rows;
    }

    /// <summary>Whether the tree would draw a header for <paramref name="node"/> at all.</summary>
    public static bool IsRendered(ScopeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Children.Count > 0 || node.Roles.Count > 1;
    }

    private static void Walk(IEnumerable<ScopeNode> nodes, int depth, Func<ScopeNode, bool> isCollapsed, List<ScopeTreeEntry> rows)
    {
        foreach (var node in nodes)
        {
            if (!IsRendered(node))
            {
                // Elided: one role, nowhere to branch. Its row stands where the header would have.
                foreach (var role in node.Roles)
                {
                    rows.Add(new ScopeTreeEntry(depth, node, role));
                }

                continue;
            }

            rows.Add(new ScopeTreeEntry(depth, node, null));
            if (isCollapsed(node))
            {
                continue;
            }

            foreach (var role in node.Roles)
            {
                rows.Add(new ScopeTreeEntry(depth + 1, node, role));
            }

            Walk(node.Children, depth + 1, isCollapsed, rows);
        }
    }

    private sealed class Builder(ArmScopeKind kind, string name, string scope)
    {
        public ArmScopeKind Kind { get; } = kind;

        public string Name { get; } = name;

        public string Scope { get; } = scope;

        public string? DisplayName { get; set; }

        public List<EligibleRole> Roles { get; } = [];

        public List<Builder> Children { get; } = [];
    }

    private static string Title(Builder builder) => builder.DisplayName ?? builder.Name;

    private static ScopeNode Freeze(Builder builder)
    {
        // Fold a chain that only passes through — no roles of its own, one way down — into the
        // node where the chain ends. Selecting that node still reaches everything the folded
        // scopes reached, because the chain is a straight line.
        var ancestors = new List<string>();
        var deepest = builder;
        while (deepest.Roles.Count == 0 && deepest.Children.Count == 1)
        {
            ancestors.Add(Title(deepest));
            deepest = deepest.Children[0];
        }

        var node = new ScopeNode(deepest.Kind, deepest.Name, deepest.Scope)
        {
            DisplayName = deepest.DisplayName,
            Ancestors = ancestors,
            Roles = [.. deepest.Roles.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)],
        };
        node.Children =
        [
            .. deepest.Children
                .Select(Freeze)
                .OrderBy(c => c.Kind)
                .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase),
        ];
        return node;
    }
}
