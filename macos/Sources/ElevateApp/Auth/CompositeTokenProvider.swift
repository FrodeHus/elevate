import Foundation
import ElevateCore

/// Routes every token operation to the provider that owns the identity's sign-in method:
/// `ownApp` to MSAL, `pinnedApp` to its own per-client-id registration, every loopback method
/// (first-party or custom client id) to its `LoopbackTokenProvider`.
///
/// On an unsigned (ad-hoc) build there is no MSAL provider — its token cache needs a keychain
/// access group the build has no entitlement for — so `ownApp` is routed to `ownAppLoopback`,
/// a loopback provider over the Settings client id that stamps its identities `.ownApp`.
final class CompositeTokenProvider: TokenProviding, Sendable {
    private let msal: MSALTokenProvider?
    private let loopback: LoopbackProviderRegistry
    private let ownAppLoopback: LoopbackTokenProvider?
    /// Resolves the provider for a pinned client id: an MSAL registry entry on a signed build,
    /// a loopback provider stamping `.pinnedApp` on an unsigned one. nil where pinning is unavailable.
    private let pinned: (@Sendable (String) throws -> any TokenProviding)?

    init(msal: MSALTokenProvider?, loopback: LoopbackProviderRegistry, ownAppLoopback: LoopbackTokenProvider? = nil,
         pinned: (@Sendable (String) throws -> any TokenProviding)? = nil) {
        self.msal = msal
        self.loopback = loopback
        self.ownAppLoopback = ownAppLoopback
        self.pinned = pinned
    }

    // MARK: TokenProviding

    func signIn(method: SignInMethod) async throws -> Identity {
        try await provider(for: method).signIn(method: method)
    }

    func signOut(_ identity: Identity) async throws {
        try await provider(for: identity.signInMethod).signOut(identity)
    }

    /// Only MSAL keeps its own account list; first-party identities live in `AppState` and are
    /// reconciled by `AppModel` through `refreshTokenState(for:)`.
    func identities() async throws -> [Identity] {
        guard let msal else { return [] }
        return try await msal.identities()
    }

    func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String {
        try await provider(for: identity.signInMethod).accessToken(identity: identity, tenantId: tenantId, scopes: scopes)
    }

    /// Every provider behind this one can force a refresh, so the probe always reaches one that does.
    var canForceRefresh: Bool { true }

    func accessToken(identity: Identity, tenantId: String, scopes: [String], forceRefresh: Bool) async throws -> String {
        try await provider(for: identity.signInMethod)
            .accessToken(identity: identity, tenantId: tenantId, scopes: scopes, forceRefresh: forceRefresh)
    }

    @discardableResult
    func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String {
        try await provider(for: identity.signInMethod)
            .acquireInteractively(identity: identity, tenantId: tenantId, scopes: scopes, claims: claims)
    }

    // MARK: Routing

    private func provider(for method: SignInMethod) throws -> any TokenProviding {
        switch method {
        case .pinnedApp(let id):
            guard let pinned else { throw PIMError.unexpected(status: 0, body: "Sign-in is unavailable in this build") }
            return try pinned(id)
        case .ownApp:
            if let msal { return msal }
            guard let ownAppLoopback else {
                throw PIMError.unexpected(status: 0, body: "Configure a client id in Settings")
            }
            return ownAppLoopback
        default:
            guard let provider = loopback.provider(for: method) else {
                throw PIMError.unexpected(status: 0, body: "Unsupported sign-in method")
            }
            return provider
        }
    }

    /// Forgets `identity`'s saved sign-in for its own method without opening a browser: MSAL's
    /// cache entry is removed locally, a loopback refresh token is deleted from the keychain.
    func discardCachedSignIn(_ identity: Identity) async {
        guard let provider = try? provider(for: identity.signInMethod) else { return }
        if let msal = provider as? MSALTokenProvider {
            try? msal.removeCachedAccounts([identity])
        } else {
            try? await provider.signOut(identity)
        }
    }
}
