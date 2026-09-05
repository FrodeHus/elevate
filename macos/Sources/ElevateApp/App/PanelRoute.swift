import Foundation
import ElevateCore

enum PanelRoute: Codable, Hashable {
    case activate([RoleKey])
    case configureRoles(TenantKey)
    case addTenant(String)        // identity id
    case discoverTenants(String)  // identity id
    case addAccount
    case saveProfile([RoleKey])
    case runProfile(UUID)
    case manageProfiles
}

protocol ExpiryNotifying: Sendable {
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String], tenantNames: [TenantKey: String]) async
    /// Posts a notification immediately; used to report the outcome of a quick activation.
    func notify(title: String, body: String) async
}

struct NoopNotifier: ExpiryNotifying {
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String], tenantNames: [TenantKey: String]) async {}
    func notify(title: String, body: String) async {}
}
