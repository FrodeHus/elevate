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
        let kinds = Self.kinds(for: panelTab)
        let ordered = ActiveSummary.order(active.values.filter { kinds.contains($0.roleKey.scope.kind) })
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
}
