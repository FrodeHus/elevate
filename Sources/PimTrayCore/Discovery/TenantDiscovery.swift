import Foundation

public struct DiscoveredTenant: Hashable, Sendable, Identifiable {
    public let tenantId: String
    public let displayName: String
    public let defaultDomain: String?
    public var id: String { tenantId }
}

public struct TenantDiscovery: Sendable {
    let http: any HTTPClient
    let tokens: any TokenProviding

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        self.http = http
        self.tokens = tokens
    }

    struct ArmTenant: Decodable { let tenantId: String; let displayName: String?; let defaultDomain: String? }
    struct ArmCollection: Decodable { let value: [ArmTenant] }

    /// Every tenant the identity can reach, via Azure Resource Manager using a home-tenant token.
    public func discoverTenants(identity: Identity) async throws -> [DiscoveredTenant] {
        let transport = GraphTransport(http: http, tokens: tokens)
        let url = URL(string: "https://management.azure.com/tenants?api-version=2022-12-01")!
        let r = try await transport.get(identity: identity, tenantId: identity.homeTenantId, url: url, scopes: ArmScopes.all)
        return try JSONDecoder().decode(ArmCollection.self, from: r.body).value.map {
            DiscoveredTenant(tenantId: $0.tenantId, displayName: $0.displayName ?? $0.defaultDomain ?? $0.tenantId, defaultDomain: $0.defaultDomain)
        }
    }

    /// Accepts a tenant GUID or a verified domain; domains are resolved via the OpenID configuration issuer.
    public func resolveTenantId(domainOrId: String) async throws -> String {
        let input = domainOrId.trimmingCharacters(in: .whitespacesAndNewlines)
        if input.wholeMatch(of: /[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/) != nil {
            return input.lowercased()
        }
        let url = URL(string: "https://login.microsoftonline.com/\(input)/v2.0/.well-known/openid-configuration")!
        let r = try await http.send(HTTPRequest(method: "GET", url: url))
        guard r.status == 200 else { throw PIMError.unexpected(status: r.status, body: "Unknown tenant '\(input)'") }
        struct Config: Decodable { let issuer: String }
        let issuer = try JSONDecoder().decode(Config.self, from: r.body).issuer
        guard let m = issuer.firstMatch(of: /([0-9a-fA-F-]{36})/) else {
            throw PIMError.unexpected(status: 200, body: "No tenant id in issuer \(issuer)")
        }
        return String(m.1).lowercased()
    }

    /// Display name from Graph `/organization` inside that tenant.
    public func tenantDisplayName(identity: Identity, tenantId: String) async throws -> String {
        let transport = GraphTransport(http: http, tokens: tokens)
        let url = URL(string: GraphTransport.graphBase.absoluteString + "/organization?$select=id,displayName")!
        let r = try await transport.get(identity: identity, tenantId: tenantId, url: url, scopes: [GraphScopes.userRead])
        struct Org: Decodable { let displayName: String? }
        struct Col: Decodable { let value: [Org] }
        return try JSONDecoder().decode(Col.self, from: r.body).value.first?.displayName ?? tenantId
    }
}
