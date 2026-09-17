import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// Accounts with their own Entra app registration ("pinned"), beside the Settings registration.
@MainActor
struct AppModelPinnedAppTests {
    static let settingsId = "11111111-2222-3333-4444-555555555555"
    static let pinnedId = "aaaaaaaa-2222-3333-4444-555555555555"

    @Test func pinnedMethodIsAvailableWithoutASettingsClientId() async {
        let model = await makeModel(ownAppViaLoopback: true)
        defer { cleanup(model) }
        #expect(!model.isAvailable(.ownApp))
        #expect(model.canPin)
        #expect(model.isAvailable(.pinned(Self.pinnedId)))
        #expect(!model.isAvailable(.pinned("not-a-guid")))
        #expect(model.loopbackStore(for: .pinned(Self.pinnedId))?.reportedMethod == .pinned(Self.pinnedId))
        #expect(model.effectiveClientId(for: .pinned(Self.pinnedId)) == Self.pinnedId)
        #expect(model.effectiveClientId(for: .ownApp) == nil)
        #expect(model.settingsRegistrationLabel == "not configured")
    }

    @Test func pinningNeedsATransportAndAnUnmanagedClientId() async {
        let signed = await makeModel(ownAppViaLoopback: false)
        #expect(!signed.canPin)   // no MSAL registry in tests
        #expect(!signed.isAvailable(.pinned(Self.pinnedId)))
        cleanup(signed)

        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": Self.settingsId]))
        let model = await makeModel(managed: managed, ownAppViaLoopback: true)
        #expect(!model.canPin)
        #expect(!model.isAvailable(.pinned(Self.pinnedId)))
        #expect(model.settingsRegistrationLabel == "managed by your organization")
        cleanup(model)
    }

    @Test func addingAPinnedAccountRemembersTheIdAndStampsTheAccount() async {
        let tokens = FakeTokenProvider()
        let model = await makeModel(tokens: tokens, ownAppViaLoopback: true)
        defer { cleanup(model) }
        let added = await model.addAccount(method: .pinned(Self.pinnedId))
        #expect(added)
        #expect(model.identity("new")?.signInMethod == .pinned(Self.pinnedId))
        #expect(model.rememberedPinnedClientId == Self.pinnedId)
        #expect(model.settings.clientId.isEmpty)
    }

    @Test func consentLinkUsesThePinnedClientId() async {
        let settings = makeSettings()
        settings.clientId = Self.settingsId
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        defer { cleanup(model) }
        model.state.identities = [Sample.identity(method: .pinned(Self.pinnedId))]
        let url = model.adminConsentURL(identityId: Sample.identityId, tenantId: Sample.tenantId)?.absoluteString
        #expect(url?.contains(Self.pinnedId) == true)
        #expect(url?.contains(Self.settingsId) == false)
        #expect(url?.contains("nativeclient") == true)
        #expect(model.matchesSettingsClientId(Self.settingsId.uppercased()))
        #expect(!model.matchesSettingsClientId(Self.pinnedId))
    }

    @Test func pinnedSharedAppIdGetsTheSharedConsentRedirect() async {
        let model = await makeModel(ownAppViaLoopback: true)
        defer { cleanup(model) }
        model.state.identities = [Sample.identity(method: .pinned(AppSettings.sharedClientId))]
        let url = model.adminConsentURL(identityId: Sample.identityId, tenantId: Sample.tenantId)?.absoluteString
        #expect(url?.contains("elevate.reothor.no") == true)
    }

    @Test func diagnosticsNeverCarryThePinnedClientId() async {
        let model = await makeModel(ownAppViaLoopback: true)
        defer { cleanup(model) }
        model.state.identities = [Sample.identity(method: .pinned(Self.pinnedId))]
        let text = model.diagnosticsText()
        #expect(!text.contains(Self.pinnedId))
        #expect(text.contains("Entra app registration (own client ID)"))
    }

    @Test func consentBlockedMessageMatchesForAPinnedAccountToo() async {
        let http = StubHTTPClient()
        await http.on("GET", "/me?", body: Data(#"{"id":"user-obj-1"}"#.utf8))
        await http.on("POST", "roleAssignmentScheduleRequests", status: 403)
        let model = await makeModel(http: http, ownAppViaLoopback: true)
        defer { cleanup(model) }
        model.state.identities = [Sample.identity(method: .pinned(Self.pinnedId))]
        model.state.upsertTenant(Sample.tenant())
        let request = ActivationRequest(roleKey: Sample.entraKey, duration: .seconds(3600), justification: "j")
        _ = await model.activate([request])
        #expect(model.tenant(Sample.tenantKey)?.discoveryMode == .manualRoles)
        #expect(model.tenant(Sample.tenantKey)?.lastDiscoveryError == "Activation not permitted in this tenant until an admin consents.")
    }

    @Test func pinnedDuplicateOfTheSettingsIdSharesItsTokenStoreOnAnUnsignedBuild() async {
        let settings = makeSettings()
        settings.clientId = Self.settingsId
        let tokens = FakeTokenProvider()
        let model = await makeModel(settings: settings, tokens: tokens, ownAppViaLoopback: true)
        defer { cleanup(model) }
        // `FakeTokenProvider.signIn` always returns the identity id "new".
        model.state.identities = [Sample.identity("new", method: .ownApp)]
        let added = await model.addAccount(method: .pinned(Self.settingsId))
        #expect(!added)
        #expect(model.notice?.contains("already added") == true)
        #expect(await tokens.signOutCalls.isEmpty)
    }

    @Test func busyAccountsAreReported() async {
        let model = await makeModel(ownAppViaLoopback: true)
        defer { cleanup(model) }
        #expect(!model.isAccountBusy(Sample.identityId))
        model.inFlight = [Sample.azureKey]
        #expect(model.isAccountBusy(Sample.identityId))
        model.inFlight = []
        model.busy = [Sample.tenantKey]
        #expect(model.isAccountBusy(Sample.identityId))
    }

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

extension AppModelPinnedAppTests {
    fileprivate static func configuredState(for identity: Identity) -> (AppState, ManualRole) {
        var state = AppState()
        state.identities = [identity]
        state.upsertTenant(Sample.tenant(identityId: identity.id))
        let manual = ManualRole(tenantKey: TenantKey(identityId: identity.id, tenantId: Sample.tenantId),
                                scope: Sample.azureKey.scope, displayName: "Owner")
        state.manualRoles = [manual]
        return (state, manual)
    }

    @Test func changingTheSettingsIdKeepsFollowingAccountsAndAsksThemToSignIn() async throws {
        let settings = makeSettings()
        settings.clientId = Self.settingsId
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        defer { cleanup(model) }
        let own = Sample.identity(method: .ownApp)
        let pinned = Sample.identity("pin", method: .pinned(Self.pinnedId))
        var (state, _) = Self.configuredState(for: own)
        state.identities.append(pinned)
        state.upsertTenant(Sample.tenant(identityId: pinned.id))
        // Set after bootstrap, which would flag both for having no keychain token.
        model.state = state
        model.signInNeeded = []
        model.roles[Sample.tenantKey] = [Sample.role(Sample.azureKey, name: "Owner")]

        try model.applyClientId("22222222-2222-3333-4444-555555555555")

        #expect(model.identities.map(\.id) == [own.id, pinned.id])
        #expect(model.tenants(for: own.id).map(\.tenantId) == [Sample.tenantId])
        #expect(model.state.manualRoles.count == 1)
        #expect(model.roles(for: Sample.tenantKey).isEmpty)
        #expect(model.needsSignIn(own.id))
        #expect(!model.needsSignIn(pinned.id))
        #expect(model.identity(pinned.id)?.signInMethod == .pinned(Self.pinnedId))
    }

    @Test func signOutStillForgetsEverything() async {
        let model = await makeModel(ownAppViaLoopback: true)
        defer { cleanup(model) }
        let own = Sample.identity(method: .ownApp)
        model.state = Self.configuredState(for: own).0
        model.forgetIdentity(own.id)
        #expect(model.identities.isEmpty)
        #expect(model.tenants(for: own.id).isEmpty)
        #expect(!model.needsSignIn(own.id))
    }
}

/// Records the client ids the composite asked the pinned route for.
final class RouteLog: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [String] = []
    func record(_ id: String) { lock.withLock { stored.append(id) } }
    var ids: [String] { lock.withLock { stored } }
}
