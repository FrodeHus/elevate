import Foundation
import ElevateCore

@MainActor
extension AppModel {
    /// The tenant the panel is scoped to. Only a tracked tenant whose account does not need to sign
    /// in again counts; anything else reads as nil (all tenants) without touching the stored choice.
    var scopeTenant: TenantKey? {
        get {
            guard let key = settings.panelTenant, tenant(key) != nil, !signInNeeded.contains(key.identityId) else { return nil }
            return key
        }
        // Any change of tenant, however it comes about, leaves that tenant's access packages.
        set {
            settings.panelTenant = newValue
            closeAccessPackages()
        }
    }

    var panelSelection: PanelSelection {
        if let selectedProfile, profile(id: selectedProfile) != nil { return .profile(selectedProfile) }
        if let scopeTenant { return .tenant(scopeTenant) }
        return .allTenants
    }

    /// Switching tenant or going back to all tenants leaves the query and bulk selection alone;
    /// only the highlighted row goes, since it may no longer be listed.
    func select(_ selection: PanelSelection) {
        closeAccessPackages()
        switch selection {
        case .allTenants:
            scopeTenant = nil
            clearOpenItems()
        case .tenant(let key):
            scopeTenant = key
            clearOpenItems()
        case .profile(let id):
            selectedProfile = id
        }
    }

    private func clearOpenItems() {
        selectedProfile = nil
        highlightedRole = nil
    }

    /// Shows the tenant's access packages in the main view; from the all-tenants list this selects the tenant too.
    func openAccessPackages(_ key: TenantKey) {
        select(.tenant(key))
        accessPackagesTenant = key
        accessPackagesTab = .available
    }

    /// Inline confirmation cards on screen. While one is open its own Cancel is the only Escape handler.
    var confirmationIsOpen: Bool { openConfirmations > 0 }
    func confirmationOpened() { openConfirmations += 1 }
    func confirmationClosed() { openConfirmations = max(openConfirmations - 1, 0) }

    func beginPackageRequest(_ package: AccessPackage) { requestingPackage = package }
    func endPackageRequest() { requestingPackage = nil }

    func closeAccessPackages() {
        accessPackagesTenant = nil
        requestingPackage = nil
    }

    var panelContent: PanelContent {
        if let key = accessPackagesTenant, panelSelection == .tenant(key) {
            if let package = requestingPackage { return .requestPackage(key, package) }
            return .accessPackages(key)
        }
        switch panelSelection {
        case .profile(let id): return .profile(id)
        case .tenant(let key): return .tenant(key)
        case .allTenants: return browsing || isFiltering || selectMode || homeIsEmpty ? .roleList : .home
        }
    }

    /// Roles matching the filter in every tracked tenant but the scoped one, so the list can offer to widen the search.
    var otherTenantMatchCount: Int {
        guard let scopeTenant, isFiltering else { return 0 }
        return state.tenants.filter { $0.id != scopeTenant }.reduce(0) { $0 + roles(for: $1.id, tab: panelTab).count }
    }

    func searchAllTenants() { scopeTenant = nil }

    /// Dismisses one transient thing per press and never touches the sidebar selection: the request
    /// form, access packages, a filter, a profile view (back to the tenant or All tenants it was
    /// opened from), select mode, browsing (All tenants only). False when none was left; the caller closes the panel.
    @discardableResult
    func handleEscape() -> Bool {
        if requestingPackage != nil {
            endPackageRequest()
        } else if accessPackagesTenant != nil {
            closeAccessPackages()
        } else if !searchQuery.isEmpty {
            searchQuery = ""
        } else if case .profile = panelSelection {
            selectedProfile = nil
        } else if selectMode {
            selectMode = false
        } else if browsing && panelSelection == .allTenants {
            browsing = false
        } else {
            return false
        }
        return true
    }
}
