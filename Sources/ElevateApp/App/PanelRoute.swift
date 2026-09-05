import Foundation
import ElevateCore

enum PanelRoute: Codable, Hashable {
    case activate([RoleKey])
    case configureRoles(TenantKey)
    case addTenant(String)        // identity id
    case discoverTenants(String)  // identity id
}

protocol ExpiryNotifying: Sendable {
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String], tenantNames: [TenantKey: String]) async
}

struct NoopNotifier: ExpiryNotifying {
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String], tenantNames: [TenantKey: String]) async {}
}
