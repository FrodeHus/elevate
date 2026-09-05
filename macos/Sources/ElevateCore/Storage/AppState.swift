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
    public var profiles: [ActivationProfile] = []

    private enum CodingKeys: String, CodingKey {
        case identities, tenants, manualRoles, memory, profiles
    }

    public init() {}

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        identities = try c.decodeIfPresent([Identity].self, forKey: .identities) ?? []
        tenants = try c.decodeIfPresent([TenantContext].self, forKey: .tenants) ?? []
        manualRoles = try c.decodeIfPresent([ManualRole].self, forKey: .manualRoles) ?? []
        memory = try c.decodeIfPresent([RoleMemory].self, forKey: .memory) ?? []
        profiles = try c.decodeIfPresent([ActivationProfile].self, forKey: .profiles) ?? []
    }

    public func profile(id: UUID) -> ActivationProfile? { profiles.first { $0.id == id } }
    public mutating func upsertProfile(_ p: ActivationProfile) {
        if let i = profiles.firstIndex(where: { $0.id == p.id }) { profiles[i] = p } else { profiles.append(p) }
    }
    public mutating func removeProfile(id: UUID) { profiles.removeAll { $0.id == id } }
    public mutating func moveProfile(fromOffsets: IndexSet, toOffset: Int) {
        let moving = fromOffsets.map { profiles[$0] }
        var remaining = profiles
        for index in fromOffsets.sorted(by: >) { remaining.remove(at: index) }
        let adjustedOffset = toOffset - fromOffsets.filter { $0 < toOffset }.count
        remaining.insert(contentsOf: moving, at: max(0, min(adjustedOffset, remaining.count)))
        profiles = remaining
    }

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
        for i in profiles.indices { profiles[i].entries.removeAll { $0.roleKey.tenantKey == key } }
    }

    public mutating func removeIdentity(_ identityId: String) {
        identities.removeAll { $0.id == identityId }
        for t in tenants where t.identityId == identityId { removeTenant(t.id) }
        for i in profiles.indices { profiles[i].entries.removeAll { $0.roleKey.identityId == identityId } }
    }

    public func memory(for key: RoleKey) -> RoleMemory? {
        memory.first { $0.roleKey == key }
    }

    public mutating func remember(roleKey: RoleKey, justification: String, duration: Duration?) {
        let entry = RoleMemory(roleKey: roleKey, justification: justification, lastDuration: duration)
        if let i = memory.firstIndex(where: { $0.roleKey == roleKey }) { memory[i] = entry } else { memory.append(entry) }
    }
}
