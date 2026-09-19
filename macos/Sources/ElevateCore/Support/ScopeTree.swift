import Foundation

/// One scope in the Azure tab's tree: the roles held directly on it, and the scopes below it that
/// hold roles of their own. Built from the eligibilities alone — see `ArmScope`.
public final class ScopeNode: Identifiable, @unchecked Sendable {
    public let kind: ArmScopeKind
    /// The ARM name: a subscription id, a resource group name.
    public let name: String
    /// The whole scope string this node stands for.
    public let scope: String

    /// The scope's friendly name, when some eligibility sits on this exact scope and the service
    /// told us one. A subscription that is only an ancestor here has no caption to borrow, so it
    /// shows its id.
    public internal(set) var displayName: String?

    /// Scopes above this one that hold no role and lead nowhere else, outermost first. A
    /// subscription whose only content is one resource group is not worth a row and a level of
    /// indent of its own in a panel this narrow, so it is folded into the node below and named
    /// here instead — the view draws it as a dimmed "Alpha /" ahead of the title.
    public internal(set) var ancestors: [String] = []

    /// Roles held on this exact scope.
    public internal(set) var roles: [EligibleRole] = []

    public internal(set) var children: [ScopeNode] = []

    init(kind: ArmScopeKind, name: String, scope: String) {
        self.kind = kind
        self.name = name
        self.scope = scope
    }

    public var id: String { scope }

    /// What to show: the friendly name when there is one, otherwise the ARM name.
    public var title: String { displayName ?? name }

    /// The folded scopes and this one, as one line: "Alpha / prod".
    public var path: String { ancestors.isEmpty ? title : (ancestors + [title]).joined(separator: " / ") }

    /// Every role at or below this node.
    public var roleCount: Int { roles.count + children.reduce(0) { $0 + $1.roleCount } }

    /// Every role at or below this node, in the order the tree shows them.
    public var allRoles: [EligibleRole] { roles + children.flatMap(\.allRoles) }
}

/// One line of the flattened tree: a scope header, or a role beneath one.
public struct ScopeTreeEntry: Identifiable, Sendable {
    public let depth: Int
    public let node: ScopeNode
    public let role: EligibleRole?

    public var isScope: Bool { role == nil }

    /// Stable across redraws: a role appears once, under the node that owns it.
    public var id: String { role.map { "role:\($0.key)" } ?? "scope:\(node.scope)" }

    init(depth: Int, node: ScopeNode, role: EligibleRole?) {
        self.depth = depth
        self.node = node
        self.role = role
    }
}

/// Turns a tenant's Azure eligibilities into the management group / subscription / resource group /
/// resource tree the panel draws, and flattens it back into rows. Kept out of the view so both the
/// panel and the tests can reach it.
public enum ScopeTree {
    /// The tree for `roles`, roots first. Roles that are not Azure resource roles are ignored; the
    /// caller has already narrowed to the Azure tab.
    public static func build(_ roles: [EligibleRole]) -> [ScopeNode] {
        var byScope: [String: Builder] = [:]
        var roots: [Builder] = []

        for role in roles {
            guard case .azureResource(let scope, _) = role.key.scope else { continue }
            let segments = ArmScope.segments(scope)
            guard !segments.isEmpty else { continue }

            var parent: Builder?
            for segment in segments {
                let lookup = segment.scope.lowercased()
                let node: Builder
                if let existing = byScope[lookup] {
                    node = existing
                } else {
                    node = Builder(kind: segment.kind, name: segment.name, scope: segment.scope)
                    byScope[lookup] = node
                    if let parent { parent.children.append(node) } else { roots.append(node) }
                }
                parent = node
            }

            // The caption the service gave names this role's own scope, not its ancestors.
            parent?.roles.append(role)
            if parent?.displayName == nil { parent?.displayName = ArmScope.displayName(detail: role.detail) }
        }

        return sorted(roots.map(freeze))
    }

    /// The tree as rows, outermost first, skipping the children of a collapsed node. `isCollapsed`
    /// is asked once per node; while the panel is filtering the caller passes a predicate that is
    /// always false, so every match stays visible in place.
    ///
    /// A scope that only leads to one role and branches nowhere is left out and its role row takes
    /// its place: sixty subscriptions with one eligibility each should not become a hundred and
    /// twenty rows. The role row still carries the scope's caption, as it does today.
    public static func flatten(_ nodes: [ScopeNode], isCollapsed: (ScopeNode) -> Bool = { _ in false }) -> [ScopeTreeEntry] {
        var rows: [ScopeTreeEntry] = []
        walk(nodes, depth: 0, isCollapsed: isCollapsed, into: &rows)
        return rows
    }

    /// Whether the tree would draw a header for `node` at all.
    public static func isRendered(_ node: ScopeNode) -> Bool {
        !node.children.isEmpty || node.roles.count > 1
    }

    private static func walk(_ nodes: [ScopeNode], depth: Int, isCollapsed: (ScopeNode) -> Bool, into rows: inout [ScopeTreeEntry]) {
        for node in nodes {
            guard isRendered(node) else {
                // Elided: one role, nowhere to branch. Its row stands where the header would have.
                for role in node.roles { rows.append(ScopeTreeEntry(depth: depth, node: node, role: role)) }
                continue
            }

            rows.append(ScopeTreeEntry(depth: depth, node: node, role: nil))
            guard !isCollapsed(node) else { continue }

            for role in node.roles { rows.append(ScopeTreeEntry(depth: depth + 1, node: node, role: role)) }
            walk(node.children, depth: depth + 1, isCollapsed: isCollapsed, into: &rows)
        }
    }

    private final class Builder {
        let kind: ArmScopeKind
        let name: String
        let scope: String
        var displayName: String?
        var roles: [EligibleRole] = []
        var children: [Builder] = []

        init(kind: ArmScopeKind, name: String, scope: String) {
            self.kind = kind
            self.name = name
            self.scope = scope
        }

        var title: String { displayName ?? name }
    }

    private static func sorted(_ nodes: [ScopeNode]) -> [ScopeNode] {
        nodes.sorted {
            $0.kind == $1.kind ? $0.title.lowercased() < $1.title.lowercased() : $0.kind < $1.kind
        }
    }

    private static func freeze(_ builder: Builder) -> ScopeNode {
        // Fold a chain that only passes through — no roles of its own, one way down — into the node
        // where the chain ends. Selecting that node still reaches everything the folded scopes
        // reached, because the chain is a straight line.
        var ancestors: [String] = []
        var deepest = builder
        while deepest.roles.isEmpty, deepest.children.count == 1 {
            ancestors.append(deepest.title)
            deepest = deepest.children[0]
        }

        let node = ScopeNode(kind: deepest.kind, name: deepest.name, scope: deepest.scope)
        node.displayName = deepest.displayName
        node.ancestors = ancestors
        node.roles = deepest.roles.sorted { $0.displayName.lowercased() < $1.displayName.lowercased() }
        node.children = sorted(deepest.children.map(freeze))
        return node
    }
}
