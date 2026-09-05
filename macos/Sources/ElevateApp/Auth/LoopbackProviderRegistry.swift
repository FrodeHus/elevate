import Foundation
import os
import ElevateCore

/// One `LoopbackTokenProvider` per loopback client id, created on first use and kept for the
/// life of the app. The fixed first-party methods and any number of custom client ids share
/// the same HTTP client and interactive gate; each gets its own keychain refresh-token store.
final class LoopbackProviderRegistry: Sendable {
    private let http: any HTTPClient
    private let gate: InteractiveGate
    private let makeStore: @Sendable (String) -> any RefreshTokenStore
    private let providers = OSAllocatedUnfairLock<[String: LoopbackTokenProvider]>(initialState: [:])

    init(http: any HTTPClient, gate: InteractiveGate,
         makeStore: @escaping @Sendable (String) -> any RefreshTokenStore = { KeychainRefreshTokenStore(clientId: $0) }) {
        self.http = http
        self.gate = gate
        self.makeStore = makeStore
    }

    /// The provider for a loopback method, or nil for `ownApp` (which MSAL owns).
    func provider(for method: SignInMethod) -> LoopbackTokenProvider? {
        guard let clientId = method.clientId else { return nil }
        return providers.withLock { cache in
            if let existing = cache[clientId] { return existing }
            let created = LoopbackTokenProvider(method: method, http: http, store: makeStore(clientId), gate: gate)
            cache[clientId] = created
            return created
        }
    }
}
