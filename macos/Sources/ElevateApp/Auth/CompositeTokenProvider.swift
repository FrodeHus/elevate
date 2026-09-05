import Foundation
import ElevateCore

/// Routes every token operation to the provider that owns the identity's sign-in method:
/// `ownApp` to MSAL, the first-party methods to their `LoopbackTokenProvider`.
final class CompositeTokenProvider: TokenProviding, Sendable {
    private let msal: MSALTokenProvider?
    private let loopback: [SignInMethod: LoopbackTokenProvider]

    init(msal: MSALTokenProvider?, loopback: [SignInMethod: LoopbackTokenProvider]) {
        self.msal = msal
        self.loopback = loopback
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

    /// The provider for `identityId`'s method, or nil when nothing owns that identity.
    func loopbackProvider(for method: SignInMethod) -> LoopbackTokenProvider? { loopback[method] }

    private func provider(for method: SignInMethod) throws -> any TokenProviding {
        if method.usesMSAL {
            guard let msal else { throw PIMError.unexpected(status: 0, body: "Configure a client id in Settings") }
            return msal
        }
        guard let provider = loopback[method] else {
            throw PIMError.unexpected(status: 0, body: "Unsupported sign-in method")
        }
        return provider
    }
}
