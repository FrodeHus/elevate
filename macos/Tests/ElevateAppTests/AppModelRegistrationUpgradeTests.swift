import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// Upgrading an Azure CLI / Azure PowerShell / custom (other-app) account to an Entra app
/// registration through `AppModel.changeSignInRegistration(_:to:)`.
@MainActor
struct AppModelRegistrationUpgradeTests {
    static let settingsId = "11111111-2222-3333-4444-555555555555"
    static let pinnedId = "aaaaaaaa-2222-3333-4444-555555555555"

    private func modelWithAccount(_ identity: Identity, tenant: TenantContext, tokens: FakeTokenProvider,
                                  managed: ManagedConfiguration = .none) async -> AppModel {
        let settings = makeSettings(managed: managed)
        if !settings.isClientIdManaged { settings.clientId = Self.settingsId }
        let model = await makeModel(settings: settings, tokens: tokens, ownAppViaLoopback: true)
        var state = AppState()
        state.identities = [identity]
        state.upsertTenant(tenant)
        let manual = ManualRole(tenantKey: TenantKey(identityId: identity.id, tenantId: tenant.tenantId),
                                scope: Sample.azureKey.scope, displayName: "Owner")
        state.manualRoles = [manual]
        model.state = state
        model.signInNeeded = []
        return model
    }

    @Test func azureCLIAccountUpgradesToSettingsRegistrationAndClearsLimitedFlags() async {
        let tokens = FakeTokenProvider()
        let tenant = TenantContext(identityId: "new", tenantId: Sample.tenantId, displayName: "Contoso", source: .home,
                                   discoveryMode: .manualRoles, lastDiscoveryError: "boom",
                                   azureUnavailableReason: "no arm access",
                                   entraActivation: .unsupported(reason: "no consent"),
                                   groupsUnavailableReason: "no consent")
        let model = await modelWithAccount(Sample.identity("new", method: .azureCLI), tenant: tenant, tokens: tokens)
        defer { cleanup(model) }

        let ok = await model.changeSignInRegistration(model.identity("new")!, to: .ownApp)

        #expect(ok)
        #expect(model.identity("new")?.signInMethod == .ownApp)
        let key = TenantKey(identityId: "new", tenantId: Sample.tenantId)
        let updated = model.tenant(key)
        #expect(updated?.discoveryMode == .automatic)
        #expect(updated?.lastDiscoveryError == nil)
        #expect(updated?.azureUnavailableReason == nil)
        #expect(updated?.entraActivation == nil)
        #expect(updated?.groupsUnavailableReason == nil)
        #expect(await tokens.signOutCalls == ["new"])
        #expect(model.tenants(for: "new").map(\.tenantId) == [Sample.tenantId])
        #expect(model.state.manualRoles.count == 1)
        #expect(!model.needsSignIn("new"))
    }

    @Test func customAccountUpgradesToAPinnedRegistration() async {
        let tokens = FakeTokenProvider()
        let tenant = Sample.tenant(identityId: "new")
        let model = await modelWithAccount(Sample.identity("new", method: .custom(clientId: "some-id")), tenant: tenant, tokens: tokens)
        defer { cleanup(model) }

        let ok = await model.changeSignInRegistration(model.identity("new")!, to: .pinned(Self.pinnedId))

        #expect(ok)
        #expect(model.identity("new")?.signInMethod == .pinned(Self.pinnedId))
    }

    @Test func aDifferentUserSignsInLeavingAnAzureCLIAccountUntouched() async {
        // FakeTokenProvider.signIn always returns the identity id "new"; the account under test
        // must have a different id for its sign-in to look like someone else.
        let tokens = FakeTokenProvider()
        let tenant = TenantContext(identityId: "id-1", tenantId: Sample.tenantId, displayName: "Contoso", source: .home,
                                   groupsUnavailableReason: "no consent")
        let model = await modelWithAccount(Sample.identity("id-1", method: .azureCLI), tenant: tenant, tokens: tokens)
        defer { cleanup(model) }

        let ok = await model.changeSignInRegistration(model.identity("id-1")!, to: .pinned(Self.pinnedId))
        #expect(!ok)
        #expect(model.identity("id-1")?.signInMethod == .azureCLI)
        #expect(model.tenant(TenantKey(identityId: "id-1", tenantId: Sample.tenantId))?.groupsUnavailableReason == "no consent")
        #expect(model.notice?.contains("was expected") == true)
    }

    @Test func aFailedSignInLeavesAnAzureCLIAccountUntouched() async {
        let tokens = FakeTokenProvider()
        await tokens.setSignInError(.network("Sign-in cancelled"))
        let tenant = TenantContext(identityId: "new", tenantId: Sample.tenantId, displayName: "Contoso", source: .home,
                                   groupsUnavailableReason: "no consent")
        let model = await modelWithAccount(Sample.identity("new", method: .azureCLI), tenant: tenant, tokens: tokens)
        defer { cleanup(model) }

        let ok = await model.changeSignInRegistration(model.identity("new")!, to: .ownApp)
        #expect(!ok)
        #expect(model.identity("new")?.signInMethod == .azureCLI)
        #expect(model.tenant(TenantKey(identityId: "new", tenantId: Sample.tenantId))?.groupsUnavailableReason == "no consent")
    }

    @Test func aNonEntraTargetIsRefused() async {
        let tokens = FakeTokenProvider()
        let model = await modelWithAccount(Sample.identity("new", method: .azureCLI), tenant: Sample.tenant(identityId: "new"), tokens: tokens)
        defer { cleanup(model) }

        let ok = await model.changeSignInRegistration(model.identity("new")!, to: .azureCLI)
        #expect(!ok)
        #expect(model.notice?.contains("Only an Entra app registration") == true)
        #expect(model.identity("new")?.signInMethod == .azureCLI)
    }

    @Test func canChangeRegistrationCoversAnyCurrentMethod() async {
        let tokens = FakeTokenProvider()
        let model = await modelWithAccount(Sample.identity("new", method: .azureCLI), tenant: Sample.tenant(identityId: "new"), tokens: tokens)
        defer { cleanup(model) }
        #expect(model.canChangeRegistration(for: model.identity("new")!))

        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": Self.settingsId]))
        let lockedModel = await modelWithAccount(Sample.identity("new", method: .azureCLI), tenant: Sample.tenant(identityId: "new"),
                                                 tokens: tokens, managed: managed)
        defer { cleanup(lockedModel) }
        #expect(lockedModel.canChangeRegistration(for: lockedModel.identity("new")!))
        #expect(!lockedModel.canPin)
        let pinnedRefused = await lockedModel.changeSignInRegistration(lockedModel.identity("new")!, to: .pinned(Self.pinnedId))
        #expect(!pinnedRefused)
        #expect(lockedModel.notice?.contains("organization") == true)
    }
}
