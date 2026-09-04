import Foundation

/// A signed-in Entra user. `id` is MSAL's home account identifier.
public struct Identity: Codable, Hashable, Sendable, Identifiable {
    public let id: String
    public var upn: String
    public var displayName: String
    public var homeTenantId: String

    public init(id: String, upn: String, displayName: String, homeTenantId: String) {
        self.id = id
        self.upn = upn
        self.displayName = displayName
        self.homeTenantId = homeTenantId
    }
}

public struct TenantKey: Codable, Hashable, Sendable {
    public let identityId: String
    public let tenantId: String
    public init(identityId: String, tenantId: String) {
        self.identityId = identityId
        self.tenantId = tenantId
    }
}

/// One tenant an identity can act in. The same identity may have many.
public struct TenantContext: Codable, Hashable, Sendable, Identifiable {
    public enum Source: String, Codable, Sendable { case home, discovered, manual }
    public enum DiscoveryMode: String, Codable, Sendable { case automatic, manualRoles }

    public var identityId: String
    public var tenantId: String
    public var displayName: String
    public var source: Source
    public var discoveryMode: DiscoveryMode
    /// Object id of the identity *inside this tenant* (guests differ per tenant).
    public var principalObjectId: String?
    public var lastDiscoveryError: String?

    public var id: TenantKey { TenantKey(identityId: identityId, tenantId: tenantId) }

    public init(identityId: String, tenantId: String, displayName: String, source: Source,
                discoveryMode: DiscoveryMode = .automatic, principalObjectId: String? = nil,
                lastDiscoveryError: String? = nil) {
        self.identityId = identityId
        self.tenantId = tenantId
        self.displayName = displayName
        self.source = source
        self.discoveryMode = discoveryMode
        self.principalObjectId = principalObjectId
        self.lastDiscoveryError = lastDiscoveryError
    }
}
