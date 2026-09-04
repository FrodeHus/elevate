import Foundation
import PimTrayCore

enum PanelRoute: Codable, Hashable {
    case activate([RoleKey])
    case configureRoles(TenantKey)
    case addTenant(String)        // identity id
    case discoverTenants(String)  // identity id
}

protocol ExpiryNotifying: Sendable {
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String]) async
}

struct NoopNotifier: ExpiryNotifying {
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String]) async {}
}

// TODO(Task 15): replace with the real ExpiryNotifier implementation and remove this alias.
typealias ExpiryNotifier = NoopNotifier
