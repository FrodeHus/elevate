import Foundation
import ElevateCore

/// What the sidebar has selected.
enum PanelSelection: Hashable {
    case allTenants
    case tenant(TenantKey)
    case profile(UUID)
}

/// The access packages view's tabs; held by the model so the list comes back on the same tab after the request form.
enum AccessPackageTab: String, CaseIterable, Identifiable {
    case available = "Available", requested = "Requested", assigned = "Assigned", declined = "Declined"
    var id: String { rawValue }
}

/// What the main view shows for the current state.
enum PanelContent: Hashable {
    case home
    case roleList
    case tenant(TenantKey)
    case profile(UUID)
    case accessPackages(TenantKey)
    case requestPackage(TenantKey, AccessPackage)
}

extension PanelContent {
    /// The views with the filter field and the select-roles toggle: everything but a profile and the access packages.
    var isRoleView: Bool {
        switch self {
        case .home, .roleList, .tenant: true
        case .profile, .accessPackages, .requestPackage: false
        }
    }
}
