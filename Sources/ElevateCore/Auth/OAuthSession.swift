import Foundation

public protocol RefreshTokenStore: Sendable {
    func load(identityId: String) throws -> String?
    func save(_ token: String, identityId: String) throws
    func delete(identityId: String) throws
    func allIdentityIds() throws -> [String]
}

public final class InMemoryRefreshTokenStore: RefreshTokenStore, @unchecked Sendable {
    private let lock = NSLock()
    private var tokens: [String: String] = [:]
    public init() {}
    public func load(identityId: String) throws -> String? { lock.withLock { tokens[identityId] } }
    public func save(_ token: String, identityId: String) throws { lock.withLock { tokens[identityId] = token } }
    public func delete(identityId: String) throws { lock.withLock { tokens[identityId] = nil } }
    public func allIdentityIds() throws -> [String] { lock.withLock { tokens.keys.sorted() } }
}

/// Access-token cache plus refresh-token handling for one public client id.
public actor OAuthSession {
    struct CacheKey: Hashable { let identityId: String; let tenantId: String; let resource: String }
    struct Entry { let token: String; let expiresAt: Date }
    static let skew: TimeInterval = 60

    let clientId: String
    let client: AuthorizationCodeClient
    let store: any RefreshTokenStore
    let now: @Sendable () -> Date
    private var cache: [CacheKey: Entry] = [:]

    public init(clientId: String, client: AuthorizationCodeClient, store: any RefreshTokenStore, now: @escaping @Sendable () -> Date = { Date() }) {
        self.clientId = clientId
        self.client = client
        self.store = store
        self.now = now
    }

    public func accessToken(identityId: String, tenantId: String, scopes: [String]) async throws -> String {
        let resource = AuthorizationCodeClient.resourceScope(for: scopes)
        let key = CacheKey(identityId: identityId, tenantId: tenantId, resource: resource)
        if let e = cache[key], e.expiresAt.timeIntervalSince(now()) > Self.skew { return e.token }
        guard let refreshToken = try store.load(identityId: identityId) else { throw PIMError.interactionRequired }
        let response: TokenResponse
        do {
            response = try await client.refresh(refreshToken: refreshToken, clientId: clientId, tenant: tenantId, scopes: [resource])
        } catch let e as PIMError {
            switch e {
            case .interactionRequired, .consentRequired, .claimsChallenge: throw e
            case .network: throw e
            default: throw PIMError.network(e.userMessage)
            }
        }
        store(response, identityId: identityId, tenantId: tenantId, scopes: scopes)
        return response.accessToken
    }

    public func store(_ response: TokenResponse, identityId: String, tenantId: String, scopes: [String]) {
        let resource = AuthorizationCodeClient.resourceScope(for: scopes)
        cache[CacheKey(identityId: identityId, tenantId: tenantId, resource: resource)] = Entry(token: response.accessToken, expiresAt: now().addingTimeInterval(TimeInterval(response.expiresIn)))
        if let rt = response.refreshToken { try? store.save(rt, identityId: identityId) }
    }

    public func signOut(identityId: String) throws {
        cache = cache.filter { $0.key.identityId != identityId }
        try store.delete(identityId: identityId)
    }

    public func knownIdentityIds() throws -> [String] { try store.allIdentityIds() }
}
