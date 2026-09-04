import Foundation

/// A role the user asserts they hold in a tenant where discovery is unavailable.
public struct ManualRole: Codable, Hashable, Sendable {
    public var tenantKey: TenantKey
    public var scope: RoleScope
    public var displayName: String
    public init(tenantKey: TenantKey, scope: RoleScope, displayName: String) {
        self.tenantKey = tenantKey
        self.scope = scope
        self.displayName = displayName
    }
}

public enum ManualRoleSource {
    public static func eligibleRoles(from manual: [ManualRole], tenantKey: TenantKey) -> [EligibleRole] {
        manual.filter { $0.tenantKey == tenantKey }.map {
            EligibleRole(key: RoleKey(identityId: tenantKey.identityId, tenantId: tenantKey.tenantId, scope: $0.scope),
                         displayName: $0.displayName, source: .manual, policy: .manualDefault)
        }
    }

    /// Discovered roles win over manual entries with the same key; manual-only roles are appended.
    public static func merge(discovered: [EligibleRole], manual: [EligibleRole]) -> [EligibleRole] {
        let known = Set(discovered.map(\.key))
        return discovered + manual.filter { !known.contains($0.key) }
    }
}
