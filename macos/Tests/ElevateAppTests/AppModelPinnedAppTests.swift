import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// Accounts with their own Entra app registration ("pinned"), beside the Settings registration.
@MainActor
struct AppModelPinnedAppTests {
    static let settingsId = "11111111-2222-3333-4444-555555555555"
    static let pinnedId = "aaaaaaaa-2222-3333-4444-555555555555"

    @Test func loopbackRegistryNeverServesOwnAppFormsDirectly() {
        let registry = LoopbackProviderRegistry(http: StubHTTPClient(), gate: InteractiveGate(),
                                                makeStore: { _ in InMemoryRefreshTokenStore() })
        #expect(registry.provider(for: .ownApp) == nil)
        #expect(registry.provider(for: .pinned(Self.pinnedId)) == nil)
        let stamped = registry.provider(clientId: Self.pinnedId, reportedMethod: .pinned(Self.pinnedId))
        #expect(stamped?.reportedMethod == .pinned(Self.pinnedId))
        // A company-app provider for the same id is a separate cache slot with its own stamp.
        #expect(registry.provider(for: .custom(clientId: Self.pinnedId))?.reportedMethod == .custom(clientId: Self.pinnedId))
    }

    @Test func compositeRoutesPinnedAccountsThroughThePinnedRoute() async throws {
        let fake = FakeTokenProvider()
        let routed = RouteLog()
        let composite = CompositeTokenProvider(
            msal: nil, loopback: LoopbackProviderRegistry(http: StubHTTPClient(), gate: InteractiveGate()),
            pinned: { id in routed.record(id); return fake })
        let identity = Sample.identity(method: .pinned(Self.pinnedId))
        _ = try await composite.accessToken(identity: identity, tenantId: Sample.tenantId, scopes: [])
        #expect(routed.ids == [Self.pinnedId])
        #expect(await fake.silentCalls == [Sample.tenantId])
        await composite.discardCachedSignIn(identity)
        #expect(await fake.signOutCalls == [Sample.identityId])
    }
}

/// Records the client ids the composite asked the pinned route for.
final class RouteLog: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [String] = []
    func record(_ id: String) { lock.withLock { stored.append(id) } }
    var ids: [String] { lock.withLock { stored } }
}
