import Testing
import Foundation
@testable import ElevateCore

@Suite struct OAuthSessionTests {
    let clientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"

    func session(_ http: StubHTTPClient, store: InMemoryRefreshTokenStore = InMemoryRefreshTokenStore(), now: @escaping @Sendable () -> Date = { Date() }) -> OAuthSession {
        OAuthSession(clientId: clientId, client: AuthorizationCodeClient(http: http), store: store, now: now)
    }

    @Test func noRefreshTokenMeansInteractionRequired() async throws {
        let s = session(StubHTTPClient())
        await #expect(throws: PIMError.interactionRequired) { _ = try await s.accessToken(identityId: "oid.tid", tenantId: "tid", scopes: GraphScopes.all) }
    }

    @Test func refreshesPerTenantAndResourceThenCaches() async throws {
        let http = StubHTTPClient()
        let store = InMemoryRefreshTokenStore()
        try store.save("RT0", identityId: "oid.tid")
        await http.on("POST", "/tid/oauth2/v2.0/token", body: Data(#"{"expires_in":3600,"access_token":"G1","refresh_token":"RT1"}"#.utf8))
        await http.on("POST", "/other/oauth2/v2.0/token", body: Data(#"{"expires_in":3600,"access_token":"A1"}"#.utf8))
        let s = session(http, store: store)
        #expect(try await s.accessToken(identityId: "oid.tid", tenantId: "tid", scopes: GraphScopes.all) == "G1")
        #expect(try await s.accessToken(identityId: "oid.tid", tenantId: "tid", scopes: GraphScopes.all) == "G1")   // cached
        #expect(try await s.accessToken(identityId: "oid.tid", tenantId: "other", scopes: ArmScopes.all) == "A1")
        #expect(await http.requests.count == 2)
        #expect(try store.load(identityId: "oid.tid") == "RT1")                                                     // rotated
        let arm = String(decoding: (await http.requests.last!).body!, as: UTF8.self)
        #expect(arm.contains("scope=https%3A%2F%2Fmanagement.azure.com%2F.default") && arm.contains("refresh_token=RT1"))
    }

    @Test func expiredCacheEntryIsRefreshed() async throws {
        let http = StubHTTPClient()
        let store = InMemoryRefreshTokenStore(); try store.save("RT", identityId: "i")
        await http.on("POST", "/t/oauth2/v2.0/token", body: Data(#"{"expires_in":100,"access_token":"X"}"#.utf8))
        let clock = Clock(); let s = session(http, store: store) { clock.now }
        _ = try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all)
        clock.advance(50); _ = try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all)   // still valid (skew 60s means 40s left)
        #expect(await http.requests.count == 2)   // 100 - 60 skew = 40 < 50 elapsed → refreshed
    }

    @Test func refreshFailureRequiringInteractionClearsNothingButThrows() async throws {
        let http = StubHTTPClient()
        let store = InMemoryRefreshTokenStore(); try store.save("RT", identityId: "i")
        await http.on("POST", "/oauth2/v2.0/token", status: 400, body: Data(#"{"error":"invalid_grant","error_description":"AADSTS50076: mfa"}"#.utf8))
        let s = session(http, store: store)
        await #expect(throws: PIMError.interactionRequired) { _ = try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all) }
        #expect(try store.load(identityId: "i") == "RT")
    }

    @Test func storeAndSignOut() async throws {
        let store = InMemoryRefreshTokenStore()
        let s = session(StubHTTPClient(), store: store)
        await s.cache(TokenResponse(accessToken: "AT", refreshToken: "RT", expiresIn: 3600, idToken: nil, scope: nil), identityId: "i", tenantId: "t", scopes: GraphScopes.all)
        #expect(try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all) == "AT")
        #expect(try await s.knownIdentityIds() == ["i"])
        try await s.signOut(identityId: "i")
        #expect(try store.load(identityId: "i") == nil)
        await #expect(throws: PIMError.interactionRequired) { _ = try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all) }
    }

    @Test func persistenceFailureIsRecordedButTokenStaysUsable() async throws {
        let s = OAuthSession(clientId: clientId, client: AuthorizationCodeClient(http: StubHTTPClient()), store: FailingRefreshTokenStore())
        await s.cache(TokenResponse(accessToken: "AT", refreshToken: "RT", expiresIn: 3600, idToken: nil, scope: nil), identityId: "i", tenantId: "t", scopes: GraphScopes.all)
        #expect(try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all) == "AT")
        let recorded = await s.persistenceError()
        #expect(recorded?.contains("keychain unavailable") == true)
        #expect(await s.lastPersistenceError == recorded)
    }

    @Test func definitivelyRejectedRefreshTokenIsDeleted() async throws {
        let http = StubHTTPClient()
        let store = InMemoryRefreshTokenStore(); try store.save("RT", identityId: "i")
        await http.on("POST", "/oauth2/v2.0/token", status: 400,
                      body: Data(#"{"error":"invalid_grant","error_description":"AADSTS700082: refresh token expired"}"#.utf8))
        let s = session(http, store: store)
        await #expect(throws: PIMError.interactionRequired) { _ = try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all) }
        #expect(try store.load(identityId: "i") == nil)
    }

    @Test func mfaRefreshFailureKeepsTheToken() async throws {
        let http = StubHTTPClient()
        let store = InMemoryRefreshTokenStore(); try store.save("RT", identityId: "i")
        await http.on("POST", "/oauth2/v2.0/token", status: 400,
                      body: Data(#"{"error":"invalid_grant","error_description":"AADSTS50076: MFA required"}"#.utf8))
        let s = session(http, store: store)
        await #expect(throws: PIMError.interactionRequired) { _ = try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all) }
        #expect(try store.load(identityId: "i") == "RT")
    }

    @Test func failedRefreshClearsInFlightSoTheNextCallRetries() async throws {
        let http = StubHTTPClient()
        let store = InMemoryRefreshTokenStore(); try store.save("RT", identityId: "i")
        // Transient sub-code, so the token survives the first failure and the retry can reuse it.
        await http.on("POST", "/t/oauth2/v2.0/token", status: 400,
                      body: Data(#"{"error":"invalid_grant","error_description":"AADSTS50158: external claims"}"#.utf8))
        let s = session(http, store: store)
        await #expect(throws: PIMError.interactionRequired) { _ = try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all) }
        await #expect(throws: PIMError.interactionRequired) { _ = try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all) }
        #expect(await http.requests.count == 2)
    }

    @Test func persistenceErrorClearsAfterALaterSuccessfulSave() async throws {
        let store = FlakyRefreshTokenStore()
        let s = OAuthSession(clientId: clientId, client: AuthorizationCodeClient(http: StubHTTPClient()), store: store)
        await s.cache(TokenResponse(accessToken: "AT1", refreshToken: "RT1", expiresIn: 3600, idToken: nil, scope: nil), identityId: "i", tenantId: "t", scopes: GraphScopes.all)
        #expect(await s.persistenceError()?.contains("keychain unavailable") == true)
        store.recover()
        await s.cache(TokenResponse(accessToken: "AT2", refreshToken: "RT2", expiresIn: 3600, idToken: nil, scope: nil), identityId: "i", tenantId: "t", scopes: GraphScopes.all)
        #expect(await s.persistenceError() == nil)
        #expect(try store.load(identityId: "i") == "RT2")
    }

    @Test func concurrentRefreshesShareOneRequest() async throws {
        let http = StubHTTPClient()
        let store = InMemoryRefreshTokenStore(); try store.save("RT", identityId: "i")
        await http.on("POST", "/t/oauth2/v2.0/token", body: Data(#"{"expires_in":3600,"access_token":"AT"}"#.utf8))
        let s = session(http, store: store)
        async let a = s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all)
        async let b = s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all)
        let (first, second) = try await (a, b)
        #expect(first == "AT" && second == "AT")
        #expect(await http.requests.count == 1)
    }
}

/// Store whose `save` always fails, standing in for a locked or unavailable keychain.
struct FailingRefreshTokenStore: RefreshTokenStore {
    func load(identityId: String) throws -> String? { nil }
    func save(_ token: String, identityId: String) throws { throw PIMError.unexpected(status: -25308, body: "keychain unavailable") }
    func delete(identityId: String) throws {}
    func allIdentityIds() throws -> [String] { [] }
}

/// Store whose `save` fails until `recover()` is called, standing in for a keychain that comes back.
final class FlakyRefreshTokenStore: RefreshTokenStore, @unchecked Sendable {
    private let lock = NSLock()
    private var healthy = false
    private var tokens: [String: String] = [:]

    func recover() { lock.withLock { healthy = true } }
    func load(identityId: String) throws -> String? { lock.withLock { tokens[identityId] } }
    func save(_ token: String, identityId: String) throws {
        try lock.withLock {
            guard healthy else { throw PIMError.unexpected(status: -25308, body: "keychain unavailable") }
            tokens[identityId] = token
        }
    }
    func delete(identityId: String) throws { lock.withLock { tokens[identityId] = nil } }
    func allIdentityIds() throws -> [String] { lock.withLock { tokens.keys.sorted() } }
}

final class Clock: @unchecked Sendable {
    private let lock = NSLock(); private var t = Date(timeIntervalSince1970: 1_000_000)
    var now: Date { lock.withLock { t } }
    func advance(_ s: TimeInterval) { lock.withLock { t = t.addingTimeInterval(s) } }
}
