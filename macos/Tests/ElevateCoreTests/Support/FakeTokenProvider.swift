import Foundation
import ElevateCore

actor FakeTokenProvider: TokenProviding {
    struct InteractiveCall: Equatable { let tenantId: String; let scopes: [String]; let claims: String? }
    var storedIdentities: [Identity] = []
    var silentError: PIMError?
    var interactiveError: PIMError?
    var signInError: PIMError?
    private(set) var interactiveCalls: [InteractiveCall] = []
    private(set) var silentCalls: [String] = []
    /// Identity ids passed to `signOut`, so a test can assert a sign-in was (or was not) discarded.
    private(set) var signOutCalls: [String] = []
    /// Lets a test simulate app state changing while an interactive sign-in is in flight (it can
    /// take minutes for real): run right before `signIn` returns its identity.
    private var onSignIn: (@MainActor () async -> Void)?

    func setSilentError(_ e: PIMError?) { silentError = e }
    func setInteractiveError(_ e: PIMError?) { interactiveError = e }
    func setSignInError(_ e: PIMError?) { signInError = e }
    func setOnSignIn(_ hook: (@MainActor () async -> Void)?) { onSignIn = hook }

    func signIn(method: SignInMethod) async throws -> Identity {
        if let signInError { throw signInError }
        if let onSignIn { await onSignIn() }
        let i = Identity(id: "new", upn: "new@x", displayName: "New", homeTenantId: "home", signInMethod: method)
        storedIdentities.append(i)
        return i
    }
    func signOut(_ identity: Identity) async throws {
        signOutCalls.append(identity.id)
        storedIdentities.removeAll { $0.id == identity.id }
    }
    func identities() async throws -> [Identity] { storedIdentities }

    /// Returned instead of the cached token when a caller forces a refresh, so a test can move the claims on.
    var refreshedToken: String?
    /// Tenant ids a forced refresh was asked for.
    private(set) var forcedCalls: [String] = []

    func setRefreshedToken(_ token: String?) { refreshedToken = token }

    /// Whether a probe may ask for a token minted now; a test turns it off to exercise the fallback.
    nonisolated let canForceRefresh: Bool

    init(canForceRefresh: Bool = true) { self.canForceRefresh = canForceRefresh }

    func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String {
        silentCalls.append(tenantId)
        if let silentError { throw silentError }
        return "token-\(tenantId)"
    }

    func accessToken(identity: Identity, tenantId: String, scopes: [String], forceRefresh: Bool) async throws -> String {
        guard forceRefresh else {
            return try await accessToken(identity: identity, tenantId: tenantId, scopes: scopes)
        }
        forcedCalls.append(tenantId)
        if let silentError { throw silentError }
        return refreshedToken ?? "token-\(tenantId)"
    }

    func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String {
        interactiveCalls.append(InteractiveCall(tenantId: tenantId, scopes: scopes, claims: claims))
        if let interactiveError { throw interactiveError }
        silentError = nil
        return "token-\(tenantId)"
    }
}
