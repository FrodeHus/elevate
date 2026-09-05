import Foundation
import AppKit
import ElevateCore

/// Authorization-code + PKCE sign-in against a Microsoft first-party client id, using the
/// default browser and a loopback redirect. Needs no app registration and no admin consent.
final class LoopbackTokenProvider: TokenProviding, Sendable {
    /// Scopes requested at sign-in: identity claims, a refresh token, and Graph.
    static let signInScopes = ["openid", "profile", "offline_access", "https://graph.microsoft.com/.default"]

    let method: SignInMethod
    private let clientId: String
    private let client: AuthorizationCodeClient
    private let session: OAuthSession
    private let gate: InteractiveGate

    /// - Parameter method: must be a first-party method (`method.clientId != nil`).
    init(method: SignInMethod, http: any HTTPClient, store: any RefreshTokenStore, gate: InteractiveGate = InteractiveGate()) {
        guard let clientId = method.clientId else {
            preconditionFailure("LoopbackTokenProvider requires a first-party client id, got \(method)")
        }
        self.method = method
        self.clientId = clientId
        self.client = AuthorizationCodeClient(http: http)
        self.session = OAuthSession(clientId: clientId, client: self.client, store: store)
        self.gate = gate
    }

    // MARK: TokenProviding

    func signIn(method: SignInMethod) async throws -> Identity {
        guard method == self.method else { throw PIMError.unexpected(status: 0, body: "Unsupported sign-in method") }
        return try await gate.run { [self] in
            let pkce = PKCE.generate()
            let state = Self.randomState()
            let listener = try await LoopbackListener.start()
            let redirectURI = listener.redirectURI
            let url = AuthorizationCodeClient.authorizeURL(
                clientId: clientId, tenant: "organizations", redirectURI: redirectURI,
                scopes: Self.signInScopes, state: state, codeChallenge: pkce.challenge, prompt: "select_account")
            try await Self.openInBrowser(url, listener: listener)
            let code = try await listener.waitForCode(expectedState: state)
            let response = try await client.redeem(code: code, verifier: pkce.verifier, clientId: clientId,
                                                   redirectURI: redirectURI, tenant: "organizations", scopes: Self.signInScopes)
            guard let idToken = response.idToken else {
                throw PIMError.unexpected(status: 0, body: "Sign-in response carried no id token")
            }
            let claims = try IdTokenClaims.parse(idToken)
            let upn = claims.preferredUsername ?? "unknown"
            let identity = Identity(id: "\(claims.oid).\(claims.tid)", upn: upn,
                                    displayName: claims.name ?? upn, homeTenantId: claims.tid, signInMethod: self.method)
            await session.store(response, identityId: identity.id, tenantId: claims.tid, scopes: Self.signInScopes)
            return identity
        }
    }

    func signOut(_ identity: Identity) async throws {
        try await session.signOut(identityId: identity.id)
    }

    /// Always empty: `AppState` owns the identity list, this provider only owns refresh tokens.
    /// Use `hasRefreshToken(for:)` to reconcile a stored identity against the keychain.
    func identities() async throws -> [Identity] { [] }

    func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String {
        try await session.accessToken(identityId: identity.id, tenantId: tenantId, scopes: scopes)
    }

    @discardableResult
    func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String {
        try await gate.run { [self] in
            let resource = AuthorizationCodeClient.resourceScope(for: scopes)
            let requested = [resource, "openid", "offline_access"]
            let pkce = PKCE.generate()
            let state = Self.randomState()
            let listener = try await LoopbackListener.start()
            let redirectURI = listener.redirectURI
            let url = AuthorizationCodeClient.authorizeURL(
                clientId: clientId, tenant: tenantId, redirectURI: redirectURI, scopes: requested,
                state: state, codeChallenge: pkce.challenge, claims: claims, loginHint: identity.upn)
            try await Self.openInBrowser(url, listener: listener)
            let code = try await listener.waitForCode(expectedState: state)
            let response = try await client.redeem(code: code, verifier: pkce.verifier, clientId: clientId,
                                                   redirectURI: redirectURI, tenant: tenantId, scopes: requested)
            await session.store(response, identityId: identity.id, tenantId: tenantId, scopes: scopes)
            return response.accessToken
        }
    }

    // MARK: Reconciliation

    /// Whether a refresh token for this identity is still in the store, so `AppModel` can drop
    /// identities whose tokens were removed behind the app's back.
    func hasRefreshToken(for identityId: String) async -> Bool {
        ((try? await session.knownIdentityIds()) ?? []).contains(identityId)
    }

    /// Last failure while persisting a refresh token, for surfacing in the UI.
    func persistenceError() async -> String? { await session.persistenceError() }

    // MARK: Helpers

    /// 32 random bytes, base64url so the value survives the query string untouched.
    static func randomState() -> String {
        var generator = SystemRandomNumberGenerator()
        let bytes = Data((0..<32).map { _ in UInt8.random(in: .min ... .max, using: &generator) })
        return bytes.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .trimmingCharacters(in: CharacterSet(charactersIn: "="))
    }

    private static func openInBrowser(_ url: URL, listener: LoopbackListener) async throws {
        guard await open(url) else {
            await listener.stop()
            throw PIMError.network("Could not open the browser for sign-in")
        }
    }

    @MainActor private static func open(_ url: URL) -> Bool { NSWorkspace.shared.open(url) }
}
