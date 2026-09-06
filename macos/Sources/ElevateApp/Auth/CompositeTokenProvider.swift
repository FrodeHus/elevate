import Foundation
import ElevateCore

/// Routes every token operation to the provider that owns the identity's sign-in method:
/// `ownApp` to MSAL, every loopback method (first-party or custom client id) to its `LoopbackTokenProvider`.
///
/// On an unsigned (ad-hoc) build there is no MSAL provider — its token cache needs a keychain
/// access group the build has no entitlement for — so `ownApp` is routed to `ownAppLoopback`,
/// a loopback provider over the Settings client id that stamps its identities `.ownApp`.
final class CompositeTokenProvider: TokenProviding, Sendable {
    private let msal: MSALTokenProvider?
    private let loopback: LoopbackProviderRegistry
    private let ownAppLoopback: LoopbackTokenProvider?

    init(msal: MSALTokenProvider?, loopback: LoopbackProviderRegistry, ownAppLoopback: LoopbackTokenProvider? = nil) {
        self.msal = msal
        self.loopback = loopback
        self.ownAppLoopback = ownAppLoopback
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

    @discardableResult
    func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String {
        try await provider(for: identity.signInMethod)
            .acquireInteractively(identity: identity, tenantId: tenantId, scopes: scopes, claims: claims)
    }

    // MARK: Routing

    private func provider(for method: SignInMethod) throws -> any TokenProviding {
        if method.usesMSAL {
            if let msal { return msal }
            guard let ownAppLoopback else {
                throw PIMError.unexpected(status: 0, body: "Configure a client id in Settings")
            }
            return ownAppLoopback
        }
        guard let provider = loopback.provider(for: method) else {
            throw PIMError.unexpected(status: 0, body: "Unsupported sign-in method")
        }
        return provider
    }
}
