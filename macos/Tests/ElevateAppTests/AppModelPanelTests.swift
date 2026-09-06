import Foundation
import Testing
import ElevateCore
@testable import Elevate

@MainActor
struct AppModelPanelTests {
    /// Builds a model holding one Entra, one Azure and one group role in the same tenant.
    private func modelWithOneOfEachKind() async -> AppModel {
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]
        let model = await makeModel(state: state)
        model.roles[Sample.tenantKey] = [
            Sample.role(Sample.entraKey, name: "Global Reader"),
            Sample.role(Sample.azureKey, name: "Owner"),
            Sample.role(Sample.groupKey, name: "Platform Admins"),
        ]
        return model
    }

    @Test func rolesForTabFilterByKind() async {
        let model = await modelWithOneOfEachKind()
        #expect(model.roles(for: Sample.tenantKey, tab: .roles).map(\.displayName) == ["Global Reader"])
        #expect(model.roles(for: Sample.tenantKey, tab: .azure).map(\.displayName) == ["Owner"])
        #expect(model.roles(for: Sample.tenantKey, tab: .groups).map(\.displayName) == ["Platform Admins"])
    }

    @Test func visibleIdentitiesDropsAccountsWithoutAMatchWhileFiltering() async {
        var state = AppState()
        state.identities = [Sample.identity("id-1"), Sample.identity("id-2")]
        state.tenants = [Sample.tenant(identityId: "id-1"), Sample.tenant(identityId: "id-2", tenantId: "tenant-2")]
        let model = await makeModel(state: state)
        let otherKey = TenantKey(identityId: "id-2", tenantId: "tenant-2")
        model.roles[Sample.tenantKey] = [Sample.role(Sample.entraKey, name: "Global Reader")]
        model.roles[otherKey] = [Sample.role(Sample.key(.entraDirectory(roleDefinitionId: "other", directoryScopeId: "/"),
                                                        identityId: "id-2", tenantId: "tenant-2"), name: "Billing Admin")]

        #expect(model.visibleIdentities.map(\.id) == ["id-1", "id-2"])
        model.searchQuery = "billing"
        #expect(model.visibleIdentities.map(\.id) == ["id-2"])
        #expect(model.visibleTenants(for: "id-1").isEmpty)
    }

    @Test func activeAssignmentsOrderedShowsOnlyTheCurrentTabsKinds() async {
        let model = await modelWithOneOfEachKind()
        model.active = [
            Sample.entraKey: Sample.assignment(Sample.entraKey),
            Sample.azureKey: Sample.assignment(Sample.azureKey),
            Sample.groupKey: Sample.assignment(Sample.groupKey),
        ]
        model.panelTab = .roles
        #expect(model.activeAssignmentsOrdered.map(\.roleKey) == [Sample.entraKey])
        model.panelTab = .azure
        #expect(model.activeAssignmentsOrdered.map(\.roleKey) == [Sample.azureKey])
        #expect(model.activeCount(for: .groups) == 1)
    }

    @Test func canActivateIsFalseForAnEntraRoleOnAFirstPartyIdentity() async {
        // Set after bootstrap: a first-party identity with no Keychain refresh token is signed
        // out during bootstrap, and this test is about the sign-in method's capabilities.
        let model = await makeModel()
        model.state.identities = [Sample.identity(method: .azureCLI)]
        model.state.tenants = [Sample.tenant()]

        #expect(model.canActivate(Sample.entraKey) == false)
        #expect(model.entraViewOnlyReason(for: Sample.tenantKey) != nil)
        // Azure resource roles go through ARM and stay activatable with the same account.
        #expect(model.canActivate(Sample.azureKey))
    }
}
