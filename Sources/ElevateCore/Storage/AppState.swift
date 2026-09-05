import Foundation

public struct RoleMemory: Codable, Hashable, Sendable {
    public var roleKey: RoleKey
    public var justification: String
    public var lastDuration: Duration?
}

public struct AppState: Codable, Hashable, Sendable {
    public var identities: [Identity] = []
    public var tenants: [TenantContext] = []
    public var manualRoles: [ManualRole] = []
    public var memory: [RoleMemory] = []

    public init() {}

    public func tenants(for identityId: String) -> [TenantContext] {
        tenants.filter { $0.identityId == identityId }
    }

    public mutating func upsertTenant(_ tenant: TenantContext) {
        if let i = tenants.firstIndex(where: { $0.id == tenant.id }) { tenants[i] = tenant } else { tenants.append(tenant) }
    }

    public mutating func removeTenant(_ key: TenantKey) {
        tenants.removeAll { $0.id == key }
        manualRoles.removeAll { $0.tenantKey == key }
        memory.removeAll { $0.roleKey.tenantKey == key }
    }

    public mutating func removeIdentity(_ identityId: String) {
        identities.removeAll { $0.id == identityId }
        for t in tenants where t.identityId == identityId { removeTenant(t.id) }
    }

    public func memory(for key: RoleKey) -> RoleMemory? {
        memory.first { $0.roleKey == key }
    }

    public mutating func remember(roleKey: RoleKey, justification: String, duration: Duration?) {
        let entry = RoleMemory(roleKey: roleKey, justification: justification, lastDuration: duration)
        if let i = memory.firstIndex(where: { $0.roleKey == roleKey }) { memory[i] = entry } else { memory.append(entry) }
    }
}
