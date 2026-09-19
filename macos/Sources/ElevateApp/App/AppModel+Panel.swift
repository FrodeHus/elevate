import Foundation
import ElevateCore

@MainActor
extension AppModel {
    // MARK: Tabs and collapse state

    /// The list the panel shows; the bulk selection survives switching so a profile can span tabs.
    var panelTab: PanelTab {
        get { settings.panelTab }
        set { settings.panelTab = newValue }
    }

    var collapsedActive: Bool { settings.collapsedActive }
    func toggleActive() { settings.collapsedActive.toggle() }

    var collapsedApprovals: Bool { settings.collapsedApprovals }
    func toggleApprovals() { settings.collapsedApprovals.toggle() }

    static func kinds(for tab: PanelTab) -> Set<RoleScopeKind> {
        switch tab {
        case .roles: [.entraDirectory]
        case .azure: [.azureResource]
        case .groups: [.group]
        }
    }

    func toggleTenant(_ key: TenantKey) { if collapsedTenants.contains(key) { collapsedTenants.remove(key) } else { collapsedTenants.insert(key) } }
    func toggleIdentity(_ id: String) { if collapsedIdentities.contains(id) { collapsedIdentities.remove(id) } else { collapsedIdentities.insert(id) } }

    // MARK: The Azure tab's scope tree

    /// The Azure tab's eligibilities for one tenant, as the management group / subscription /
    /// resource group / resource tree the scope strings already describe. Built from the filtered
    /// rows, so a search narrows the tree rather than sitting beside it.
    func azureTree(for tenantKey: TenantKey) -> [ScopeNode] {
        ScopeTree.build(roles(for: tenantKey, tab: .azure))
    }

    /// A scope node's collapsed state is per tenant: the same scope can be reached by two accounts.
    static func scopeNodeKey(_ tenantKey: TenantKey, _ node: ScopeNode) -> String {
        "\(tenantKey.identityId)|\(tenantKey.tenantId)|\(node.scope)"
    }

    /// While a search is running every match stays visible in its place in the tree, so a closed
    /// node does not hide a row the user is looking at.
    func isScopeCollapsed(_ tenantKey: TenantKey, _ node: ScopeNode) -> Bool {
        !isFiltering && collapsedScopes.contains(Self.scopeNodeKey(tenantKey, node))
    }

    func toggleScope(_ tenantKey: TenantKey, _ node: ScopeNode) {
        let key = Self.scopeNodeKey(tenantKey, node)
        if collapsedScopes.contains(key) { collapsedScopes.remove(key) } else { collapsedScopes.insert(key) }
    }

    // MARK: Search

    var isFiltering: Bool { PanelFilter.isActive(searchQuery) }

    private func matchesFilter(_ role: EligibleRole) -> Bool {
        guard isFiltering else { return true }
        let tenantName = tenant(role.key.tenantKey)?.displayName ?? role.key.tenantId
        let upn = identity(role.key.identityId)?.upn ?? ""
        return PanelFilter.matches(query: searchQuery, role: role, tenantName: tenantName, upn: upn)
    }

    // MARK: Visibility

    func roles(for tenantKey: TenantKey, tab: PanelTab) -> [EligibleRole] {
        let kinds = Self.kinds(for: tab)
        return roles(for: tenantKey).filter { kinds.contains($0.key.scope.kind) && matchesFilter($0) }
    }

    /// While filtering, only tenants with a matching row in the current tab; otherwise all of them.
    func visibleTenants(for identityId: String) -> [TenantContext] {
        let all = tenants(for: identityId)
        guard isFiltering else { return all }
        return all.filter { !roles(for: $0.id, tab: panelTab).isEmpty }
    }

    var visibleIdentities: [Identity] {
        guard isFiltering else { return identities }
        return identities.filter { !visibleTenants(for: $0.id).isEmpty }
    }

    // MARK: Summary

    /// Name for the summary row; before the eligible list has loaded only the key is known.
    func summaryName(for key: RoleKey) -> String {
        if let r = role(for: key) { return r.displayName }
        switch key.scope {
        case .entraDirectory(let id, _): return id
        case .azureResource(let scope, let id): return "\(id.components(separatedBy: "/").last ?? id) @ \(scope)"
        case .group(let gid, let access): return "\(gid) (\(access == .owner ? "owner" : "member"))"
        }
    }

    /// Active assignments of `tab`'s kinds, for the tab labels' counts.
    func activeCount(for tab: PanelTab) -> Int {
        let kinds = Self.kinds(for: tab)
        return active.values.count { $0.status == .active && kinds.contains($0.roleKey.scope.kind) }
    }

    /// The "Active now" summary shows only the current tab's kinds; the tab labels carry the other counts.
    var activeAssignmentsOrdered: [ActiveAssignment] {
        summaryOrdered(active.values)
    }

    /// Rows for the "Active now" section: the ordered active list, then assignments deactivated
    /// moments ago so their row can show the confirmation before it leaves.
    var activeRowsOrdered: [ActiveAssignment] {
        let items = activeAssignmentsOrdered
        let lingering = recentlyDeactivated.values.filter { a in !items.contains { $0.roleKey == a.roleKey } }
        return items + summaryOrdered(lingering)
    }

    /// Keeps `assignment`'s row in "Active now" for a moment after its deactivation succeeded.
    /// Held by the model, not the view: the section may not exist yet when the row is retained,
    /// and a view task hung off a conditional section never runs inside the panel's lazy stack.
    func retainDeactivatedRow(_ assignment: ActiveAssignment) {
        let key = assignment.roleKey
        recentlyDeactivated[key] = assignment
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(3))
            guard let self, self.recentlyDeactivated[key] == assignment else { return }
            self.recentlyDeactivated[key] = nil
        }
    }

    private func summaryOrdered(_ assignments: some Sequence<ActiveAssignment>) -> [ActiveAssignment] {
        let kinds = Self.kinds(for: panelTab)
        let ordered = ActiveSummary.order(assignments.filter { kinds.contains($0.roleKey.scope.kind) })
        guard isFiltering else { return ordered }
        return ordered.filter { a in
            if let r = role(for: a.roleKey) { return matchesFilter(r) }
            return PanelFilter.matches(query: searchQuery, text: summaryName(for: a.roleKey))
        }
    }

    // MARK: Selection

    var selectionCount: Int { selection.count }

    /// Per-kind counts of the bulk selection, for the cross-tab hint in the bulk bar.
    var selectionBreakdown: (entra: Int, azure: Int, groups: Int) {
        var entra = 0, azure = 0, groups = 0
        for key in selection {
            switch key.scope.kind {
            case .entraDirectory: entra += 1
            case .azureResource: azure += 1
            case .group: groups += 1
            }
        }
        return (entra, azure, groups)
    }

    /// Noun for the bulk bar: roles and groups can be selected together across tabs.
    var selectionNoun: String {
        let kinds = Set(selection.map(\.scope.kind))
        if kinds.isEmpty || kinds == [.group] { return kinds.isEmpty ? "role" : "group" }
        return kinds.contains(.group) ? "item" : "role"
    }

    func toggleSelection(_ key: RoleKey) {
        guard canActivate(key) else { return }
        if selection.contains(key) { selection.remove(key) } else { selection.insert(key) }
    }

    /// Where a scope node's checkbox stands: nothing under it chosen, some of it, or all of it.
    enum SubtreeSelection { case none, some, all }

    /// The roles a scope node's checkbox acts on: everything at or below it that can actually be
    /// activated. A role already active, or view-only, is not something the checkbox can take, so
    /// it is left out of both the count and the toggle.
    func subtreeKeys(_ node: ScopeNode) -> [RoleKey] {
        node.allRoles.map(\.key).filter(canSelect)
    }

    /// Whether a role's own checkbox would be enabled: activatable, and not already active,
    /// scheduled or awaiting approval — there is nothing for a bulk activation to do with those.
    /// A failed one can be chosen again.
    func canSelect(_ key: RoleKey) -> Bool {
        guard canActivate(key) else { return false }
        guard let assignment = assignment(for: key) else { return true }
        if case .failed = assignment.status { return true }
        return false
    }

    func subtreeState(_ node: ScopeNode) -> SubtreeSelection {
        let keys = subtreeKeys(node)
        guard !keys.isEmpty else { return .none }
        let chosen = keys.count { selection.contains($0) }
        if chosen == 0 { return .none }
        return chosen == keys.count ? .all : .some
    }

    /// "Select every eligibility under this scope", and pressing it again lets them all go. A
    /// partly chosen subtree fills up rather than emptying: the checkbox is offering the rest.
    /// Returns how many keys the press added or removed, for the confirmation the row shows.
    @discardableResult
    func toggleSubtree(_ node: ScopeNode) -> Int {
        let keys = subtreeKeys(node)
        guard !keys.isEmpty else { return 0 }
        if subtreeState(node) == .all {
            let removed = keys.count { selection.contains($0) }
            selection.subtract(keys)
            return removed
        }
        let added = keys.count { !selection.contains($0) }
        selection.formUnion(keys)
        return added
    }
}
