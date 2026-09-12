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
    case deactivateProfile(UUID)
    case manageProfiles
    case decide(requestId: String, approve: Bool)
    case accessPackages(TenantKey)
}

/// A scheduled "access package expired" notification: the assignment id keys it, so repeated
/// polls replace rather than duplicate.
struct PackageExpiry: Hashable, Sendable {
    let id: String
    let packageName: String
    let tenantName: String
    let at: Date
}

protocol ExpiryNotifying: Sendable {
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String], tenantNames: [TenantKey: String]) async
    /// Posts a notification immediately; used to report the outcome of a quick activation.
    func notify(title: String, body: String) async
    /// Replaces the set of access package expiries to notify about at their end dates.
    func setPackageExpiries(_ expiries: [PackageExpiry]) async
}

struct NoopNotifier: ExpiryNotifying {
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String], tenantNames: [TenantKey: String]) async {}
    func notify(title: String, body: String) async {}
    func setPackageExpiries(_ expiries: [PackageExpiry]) async {}
}
