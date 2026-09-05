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
            let detail: String? = switch $0.scope {
            case .azureResource(let scope, _): scope
            case .group(_, let accessId): accessId == .owner ? "owner" : "member"
            default: nil
            }
            return EligibleRole(key: RoleKey(identityId: tenantKey.identityId, tenantId: tenantKey.tenantId, scope: $0.scope),
                                displayName: $0.displayName, detail: detail, source: .manual, policy: .manualDefault)
        }
    }

    /// Discovered roles win over manual entries with the same key; a manual Azure entry is also dropped when a
    /// discovered Azure role has the same scope and display name (the manual entry names the role, ARM ids it).
    public static func merge(discovered: [EligibleRole], manual: [EligibleRole]) -> [EligibleRole] {
        let known = Set(discovered.map(\.key))
        let azureNames = Set(discovered.compactMap { role -> String? in
            guard case .azureResource(let scope, _) = role.key.scope else { return nil }
            return scope.lowercased() + "|" + role.displayName.lowercased()
        })
        return discovered + manual.filter { role in
            guard !known.contains(role.key) else { return false }
            if case .azureResource(let scope, _) = role.key.scope {
                return !azureNames.contains(scope.lowercased() + "|" + role.displayName.lowercased())
            }
            return true
        }
    }
}
