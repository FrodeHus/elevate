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

    /// The provider for a loopback method, or nil for `ownApp`, which carries no client id of its
    /// own — on unsigned builds it goes through `provider(clientId:reportedMethod:)` instead.
    func provider(for method: SignInMethod) -> LoopbackTokenProvider? {
        guard let clientId = method.clientId else { return nil }
        return provider(clientId: clientId, method: method, reportedMethod: method)
    }

    /// A provider for `clientId` whose identities are stamped with `reportedMethod`. Used for the
    /// own-app registration on unsigned builds: the tokens are keyed by the Settings client id
    /// (a `.custom` method), while the account is recorded as `.ownApp`. Cached separately from
    /// the plain `.custom` provider for the same client id so the two cannot stamp each other's
    /// method, though they share one keychain store and therefore one set of refresh tokens.
    func provider(clientId: String, reportedMethod: SignInMethod) -> LoopbackTokenProvider? {
        guard !clientId.isEmpty else { return nil }
        return provider(clientId: clientId, method: .custom(clientId: clientId), reportedMethod: reportedMethod)
    }

    private func provider(clientId: String, method: SignInMethod, reportedMethod: SignInMethod) -> LoopbackTokenProvider {
        let key = method == reportedMethod ? clientId : "\(clientId)#\(reportedMethod.storageKey)"
        return providers.withLock { cache in
            if let existing = cache[key] { return existing }
            let created = LoopbackTokenProvider(method: method, http: http, store: makeStore(clientId), gate: gate,
                                                reportedMethod: reportedMethod)
            cache[key] = created
            return created
        }
    }
}
