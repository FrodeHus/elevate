import Foundation

public enum RoleScopeKind: String, Codable, Hashable, Sendable, CaseIterable {
    case entraDirectory, azureResource, group
}

public enum GroupAccess: String, Codable, Hashable, Sendable { case member, owner }

public enum RoleScope: Codable, Hashable, Sendable {
    case entraDirectory(roleDefinitionId: String, directoryScopeId: String)
    case azureResource(scope: String, roleDefinitionId: String)
    case group(groupId: String, accessId: GroupAccess)

    public var kind: RoleScopeKind {
        switch self {
        case .entraDirectory: .entraDirectory
        case .azureResource: .azureResource
        case .group: .group
        }
    }
}

public struct RoleKey: Codable, Hashable, Sendable {
    public let identityId: String
    public let tenantId: String
    public let scope: RoleScope

    public init(identityId: String, tenantId: String, scope: RoleScope) {
        self.identityId = identityId
        self.tenantId = tenantId
        self.scope = scope
    }

    public var tenantKey: TenantKey { TenantKey(identityId: identityId, tenantId: tenantId) }
}
