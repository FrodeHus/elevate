import Foundation
import ElevateCore

/// What the sidebar has selected.
enum PanelSelection: Hashable {
    case allTenants
    case tenant(TenantKey)
    case profile(UUID)
}

/// What the main view shows for the current state.
enum PanelContent: Hashable {
    case home
    case roleList
    case tenant(TenantKey)
    case profile(UUID)
}
