import Foundation
import os
import ElevateCore

/// One `MSALTokenProvider` per pinned client id (`SignInMethod.pinnedApp`), created on first use
/// and kept for the life of the app. The Settings registration keeps its own provider in
/// `AppModel`; these stamp `.pinnedApp` so each account routes back to its own registration.
/// All share the redirect URI, the auth anchor and the interactive gate.
final class MSALProviderRegistry: @unchecked Sendable {
    private let anchor: AuthAnchorWindow
    private let gate: InteractiveGate
    /// Captured at init, on the main actor, since `AppSettings.redirectUri` is main-actor-isolated
    /// and `provider(clientId:)` must not be.
    private let redirectUri: String
    private let providers = OSAllocatedUnfairLock<[String: MSALTokenProvider]>(initialState: [:])

    @MainActor
    init(anchor: AuthAnchorWindow, gate: InteractiveGate) {
        self.anchor = anchor
        self.gate = gate
        self.redirectUri = AppSettings.redirectUri
    }

    func provider(clientId: String) throws -> MSALTokenProvider {
        let method = SignInMethod.pinned(clientId)
        guard let id = method.clientId, !id.isEmpty else {
            throw PIMError.unexpected(status: 0, body: "Enter the application (client) ID as a GUID")
        }
        return try providers.withLockUnchecked { cache in
            if let existing = cache[id] { return existing }
            let created = try MSALTokenProvider(method: method, clientId: id, redirectUri: redirectUri,
                                                anchor: anchor, gate: gate)
            cache[id] = created
            return created
        }
    }
}
