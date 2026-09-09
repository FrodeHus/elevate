import Foundation
import Testing
import ElevateCore
@testable import Elevate

@Suite @MainActor
struct AppModelManagedTests {
    static let id = "11111111-2222-3333-4444-555555555555"

    @Test func managedClientIdConfiguresTheApp() async {
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": Self.id]))
        let model = await makeModel(managed: managed, ownAppViaLoopback: true)
        defer { cleanup(model) }

        #expect(model.settings.isClientIdManaged)
        #expect(model.settings.clientId == Self.id)
        #expect(model.isConfigured)

        // The user cannot write over a managed client id, in the settings object or through the model.
        model.settings.clientId = "22222222-2222-3333-4444-555555555555"
        #expect(model.settings.clientId == Self.id)
        #expect(throws: PIMError.self) { try model.applyClientId("22222222-2222-3333-4444-555555555555") }
    }

    @Test func userClientIdStillWorksWithoutManagement() async {
        let model = await makeModel(ownAppViaLoopback: true)
        defer { cleanup(model) }

        #expect(!model.settings.isClientIdManaged)
        model.settings.clientId = Self.id
        #expect(model.settings.storedClientId == Self.id)
        #expect(model.settings.clientId == Self.id)
        #expect(model.isConfigured)
    }

    @Test func disabledUpdateCheckNeverCallsGitHub() async {
        let http = StubHTTPClient()
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["DisableUpdateCheck": true]))
        let model = await makeModel(http: http, online: true, managed: managed)
        defer { cleanup(model) }

        await model.checkForUpdates(force: true)

        #expect(await http.requests(matching: "api.github.com").isEmpty)
        #expect(model.updateAvailable == nil)
        #expect(model.updateCheckMessage == nil)
    }

    @Test func diagnosticsListsManagedKeys() async {
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": Self.id, "DisableUpdateCheck": true], origin: "unit"))
        let model = await makeModel(managed: managed)
        defer { cleanup(model) }

        let text = model.diagnosticsText()
        #expect(text.contains("Source: unit"))
        #expect(text.contains("Keys: ClientId, DisableUpdateCheck"))
        #expect(!text.contains(Self.id))
    }
}

/// Managed sign-in methods and tenants: what the app offers, keeps and refuses once an
/// organization has restricted them.
@Suite @MainActor
struct AppModelManagedTenantTests {
    static let id = "11111111-2222-3333-4444-555555555555"
    /// The tenant id `fabrikam.com` resolves to through the stubbed OpenID configuration.
    static let fabrikamId = "aaaaaaaa-0000-0000-0000-000000000002"
    /// A GUID entry that needs no lookup at all.
    static let allowedGuid = "aaaaaaaa-0000-0000-0000-000000000001"

    /// Answers the unauthenticated tenant lookup for `domain` with `tenantId`, the way
    /// `TenantDiscovery.tenantId(domainOrId:http:)` expects it.
    private static func stubTenantLookup(_ http: StubHTTPClient, domain: String, tenantId: String) async {
        await http.on("GET", "\(domain)/v2.0/.well-known/openid-configuration",
                      body: Data(#"{"issuer":"https://login.microsoftonline.com/\#(tenantId)/v2.0"}"#.utf8))
    }

    @Test func disallowedMethodsAreHiddenAndRefused() async {
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedSignInMethods": ["ownApp"]]))
        let model = await makeModel(managed: managed)
        defer { cleanup(model) }

        #expect(model.availableMethods == [.ownApp])
        #expect(!model.isAvailable(.azureCLI))
        #expect(!model.isAvailable(.custom(clientId: Self.id)))
        #expect(await model.addAccount(method: .azureCLI) == false)
        #expect(model.notice == "That sign-in method is not permitted by your organization")
    }

    @Test func accountWithDisallowedMethodStaysButCannotRetry() async {
        var state = AppState()
        let identity = Sample.identity(method: .azureCLI)
        state.identities = [identity]
        state.upsertTenant(Sample.tenant())
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedSignInMethods": ["ownApp"]]))
        let model = await makeModel(state: state, managed: managed)
        defer { cleanup(model) }

        #expect(model.needsSignIn(identity.id))
        #expect(await model.retrySignIn(identity) == false)
        #expect(model.notice == "That sign-in method is not permitted by your organization")
        #expect(model.identities.map(\.id) == [identity.id])
    }

    @Test func allowedTenantsFilterDiscoveryAndAdds() async {
        var state = AppState()
        state.identities = [Sample.identity(method: .azureCLI)]
        state.upsertTenant(Sample.tenant())
        state.upsertTenant(TenantContext(identityId: Sample.identityId, tenantId: "other-tenant",
                                         displayName: "Other", source: .discovered))
        let http = StubHTTPClient()
        await Self.stubTenantLookup(http, domain: "fabrikam.com", tenantId: Self.fabrikamId)
        await Self.stubTenantLookup(http, domain: "zzz", tenantId: "bbbbbbbb-0000-0000-0000-000000000003")
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedTenants": [Self.allowedGuid, "fabrikam.com"]]))
        let model = await makeModel(state: state, http: http, managed: managed)
        defer { cleanup(model) }

        #expect(model.allowedTenantIds == [Self.allowedGuid, Self.fabrikamId])
        #expect(model.managedTenantWarnings.isEmpty)
        // The tracked tenant nobody allowed is gone; the account's home tenant is exempt.
        #expect(model.tenants(for: Sample.identityId).map(\.tenantId) == [Sample.tenantId])

        await model.trackTenants(identityId: Sample.identityId, tenants: [
            DiscoveredTenant(tenantId: "zzz", displayName: "Zzz", defaultDomain: nil),
            DiscoveredTenant(tenantId: Self.fabrikamId, displayName: "Fabrikam", defaultDomain: "fabrikam.com"),
        ])
        #expect(model.tenants(for: Sample.identityId).map(\.tenantId).sorted() == [Self.fabrikamId, Sample.tenantId].sorted())

        do {
            try await model.addTenant(identityId: Sample.identityId, domainOrId: "zzz")
            Issue.record("adding a tenant nobody allowed should throw")
        } catch {
            #expect(((error as? PIMError)?.userMessage ?? "").contains("not permitted by your organization"))
        }
    }

    @Test func pinnedTenantsAreTrackedAtBootstrapAndAfterSignIn() async {
        var state = AppState()
        state.identities = [Sample.identity(method: .azureCLI)]
        state.upsertTenant(Sample.tenant())
        let http = StubHTTPClient()
        await Self.stubTenantLookup(http, domain: "fabrikam.com", tenantId: Self.fabrikamId)
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["PinnedTenants": ["fabrikam.com"]]))
        let model = await makeModel(state: state, http: http, managed: managed)
        defer { cleanup(model) }

        #expect(model.pinnedTenantIds == [Self.fabrikamId])
        let pinned = model.tenants(for: Sample.identityId).first { $0.tenantId == Self.fabrikamId }
        #expect(pinned?.source == .discovered)
        // No Graph name was available, so the entry as configured stands in for it.
        #expect(pinned?.displayName == "fabrikam.com")
        let key = TenantKey(identityId: Sample.identityId, tenantId: Self.fabrikamId)
        #expect(model.isPinnedTenant(key))

        model.removeTenant(key)
        #expect(model.tenant(key) != nil)
        #expect(model.notice == "This tenant is pinned by your organization")

        // A newly added account gets the pinned tenant as well.
        #expect(await model.addAccount(method: .azureCLI))
        #expect(model.tenants(for: "new").map(\.tenantId).contains(Self.fabrikamId))
    }

    @Test func unresolvedManagedTenantIsAWarningNotABlock() async {
        var state = AppState()
        state.identities = [Sample.identity(method: .azureCLI)]
        state.upsertTenant(Sample.tenant())
        let http = StubHTTPClient()
        await http.on("GET", "nowhere.example/v2.0/.well-known/openid-configuration", status: 404)
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedTenants": ["nowhere.example"]]))
        let model = await makeModel(state: state, http: http, managed: managed)
        defer { cleanup(model) }

        #expect(model.allowedTenantIds == nil)
        #expect(model.managedTenantWarnings == ["AllowedTenants: could not resolve 'nowhere.example'"])
        #expect(model.tenants(for: Sample.identityId).map(\.tenantId) == [Sample.tenantId])

        await model.trackTenants(identityId: Sample.identityId, tenants: [
            DiscoveredTenant(tenantId: "zzz", displayName: "Zzz", defaultDomain: nil),
        ])
        #expect(model.tenants(for: Sample.identityId).map(\.tenantId).contains("zzz"))
    }
}
