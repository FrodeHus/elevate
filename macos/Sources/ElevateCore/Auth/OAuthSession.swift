import Foundation

public protocol RefreshTokenStore: Sendable {
    func load(identityId: String) throws -> String?
    func save(_ token: String, identityId: String) throws
    func delete(identityId: String) throws
    func allIdentityIds() throws -> [String]
}

/// Non-persistent store for tests and previews; production uses the keychain-backed store in the app.
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
    /// Refreshes currently in flight, so concurrent callers for the same key share one POST.
    private var inFlight: [CacheKey: Task<String, Error>] = [:]
    /// Last failure while persisting a refresh token, if any. The access token is still usable;
    /// the account simply will not survive a restart until the store recovers.
    public private(set) var lastPersistenceError: String?

    public init(clientId: String, client: AuthorizationCodeClient, store: any RefreshTokenStore, now: @escaping @Sendable () -> Date = { Date() }) {
        self.clientId = clientId
        self.client = client
        self.store = store
        self.now = now
    }

    public func persistenceError() -> String? { lastPersistenceError }

    public func accessToken(identityId: String, tenantId: String, scopes: [String]) async throws -> String {
        let resource = AuthorizationCodeClient.resourceScope(for: scopes)
        let key = CacheKey(identityId: identityId, tenantId: tenantId, resource: resource)
        if let e = cache[key], e.expiresAt.timeIntervalSince(now()) > Self.skew { return e.token }
        if let running = inFlight[key] { return try await running.value }
        let task = Task<String, Error> { [scopes] in try await self.refresh(key: key, scopes: scopes) }
        inFlight[key] = task
        defer { inFlight[key] = nil }
        return try await task.value
    }

    private func refresh(key: CacheKey, scopes: [String]) async throws -> String {
        guard let refreshToken = try store.load(identityId: key.identityId) else { throw PIMError.interactionRequired }
        let response: TokenResponse
        do {
            response = try await client.refreshClassified(refreshToken: refreshToken, clientId: clientId, tenant: key.tenantId, scopes: [key.resource])
        } catch let outcome as RefreshOutcomeError {
            // The server rejected the refresh token itself, so it will never work again: drop it
            // rather than keep re-sending it. Transient, consent and MFA failures keep the token.
            if outcome.definitive, outcome.error == .interactionRequired {
                try? store.delete(identityId: key.identityId)
            }
            throw Self.surfaced(outcome.error)
        } catch let e as PIMError {
            throw Self.surfaced(e)
        }
        cache(response, identityId: key.identityId, tenantId: key.tenantId, scopes: scopes)
        return response.accessToken
    }

    /// Errors the UI knows how to act on pass through; anything else becomes a network failure.
    private static func surfaced(_ e: PIMError) -> PIMError {
        switch e {
        case .interactionRequired, .consentRequired, .claimsChallenge, .network: e
        default: .network(e.userMessage)
        }
    }

    /// Caches the access token and persists the rotated refresh token, if the response carried one.
    public func cache(_ response: TokenResponse, identityId: String, tenantId: String, scopes: [String]) {
        let resource = AuthorizationCodeClient.resourceScope(for: scopes)
        cache[CacheKey(identityId: identityId, tenantId: tenantId, resource: resource)] = Entry(token: response.accessToken, expiresAt: now().addingTimeInterval(TimeInterval(response.expiresIn)))
        guard let rt = response.refreshToken else { return }
        do {
            try store.save(rt, identityId: identityId)
            lastPersistenceError = nil
        } catch {
            // Keep the access token: the session still works until it expires.
            switch error {
            case PIMError.unexpected(_, let body): lastPersistenceError = body
            case let e as PIMError: lastPersistenceError = e.userMessage
            default: lastPersistenceError = error.localizedDescription
            }
        }
    }

    public func signOut(identityId: String) throws {
        cache = cache.filter { $0.key.identityId != identityId }
        try store.delete(identityId: identityId)
    }

    public func knownIdentityIds() throws -> [String] { try store.allIdentityIds() }
}
