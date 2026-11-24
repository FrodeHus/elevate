import Foundation
import Testing
import ElevateCore
@testable import Elevate

@MainActor
struct AppModelSelectionStateTests {
    private static let otherKey = TenantKey(identityId: "id-2", tenantId: "tenant-2")

    /// Two accounts, one tenant each, one Entra role in each.
    private func makeTwoTenantModel() async -> AppModel {
        var state = AppState()
        state.identities = [Sample.identity("id-1"), Sample.identity("id-2")]
        state.tenants = [Sample.tenant(), Sample.tenant(identityId: "id-2", tenantId: "tenant-2", name: "Fabrikam")]
        let model = await makeModel(state: state)
        let otherEntra = Sample.key(.entraDirectory(roleDefinitionId: "role-def", directoryScopeId: "/"), identityId: "id-2", tenantId: "tenant-2")
        model.roles[Sample.tenantKey] = [Sample.role(Sample.entraKey, name: "Global Reader")]
        model.roles[Self.otherKey] = [Sample.role(otherEntra, name: "Billing Admin")]
        return model
    }

    @Test func defaultsToAllTenantsAndHome() async {
        let model = await makeTwoTenantModel()
        model.active[Sample.entraKey] = Sample.assignment(Sample.entraKey)  // an empty home falls back to the list
        #expect(model.panelSelection == .allTenants)
        #expect(model.panelContent == .home)
        cleanup(model)
    }

    @Test func selectingATenantPersistsAndScopesTheList() async {
        let model = await makeTwoTenantModel()
        model.select(.tenant(Sample.tenantKey))
        #expect(model.settings.panelTenant == Sample.tenantKey)
        #expect(model.visibleTenants(for: "id-2").isEmpty)
        #expect(model.visibleIdentities.map(\.id) == ["id-1"])
        #expect(model.panelContent == .tenant(Sample.tenantKey))
        cleanup(model)
    }

    @Test func aRemovedTenantFallsBackToAllTenants() async {
        let model = await makeTwoTenantModel()
        model.scopeTenant = Sample.tenantKey
        model.state.tenants.removeAll { $0.id == Sample.tenantKey }
        #expect(model.scopeTenant == nil)
        #expect(model.panelSelection == .allTenants)
        cleanup(model)
    }

    @Test func anAccountNeedingSignInFallsBackToAllTenants() async {
        let model = await makeTwoTenantModel()
        model.scopeTenant = Sample.tenantKey
        model.signInNeeded.insert("id-1")
        #expect(model.scopeTenant == nil)
        cleanup(model)
    }

    @Test func typingBrowsingOrSelectModeShowTheRoleList() async {
        let model = await makeTwoTenantModel()
        model.searchQuery = "g"
        #expect(model.panelContent == .roleList)
        model.searchQuery = ""
        model.browsing = true
        #expect(model.panelContent == .roleList)
        model.browsing = false
        model.selectMode = true
        #expect(model.panelContent == .roleList)
        cleanup(model)
    }

    @Test func selectingATenantKeepsQueryAndSelection() async {
        let model = await makeTwoTenantModel()
        model.selectMode = true
        model.searchQuery = "glob"
        model.selection = [Sample.entraKey]
        model.select(.tenant(Self.otherKey))
        #expect(model.selectMode)
        #expect(model.searchQuery == "glob")
        #expect(model.selection == [Sample.entraKey])
        cleanup(model)
    }

    @Test func otherTenantMatchesAreCountedOnlyWhenScopedAndFiltering() async {
        let model = await makeTwoTenantModel()
        model.scopeTenant = Sample.tenantKey
        model.searchQuery = "billing"
        #expect(model.roles(for: Sample.tenantKey, tab: .all).isEmpty)
        #expect(model.otherTenantMatchCount == 1)
        model.searchQuery = "zzz"
        #expect(model.otherTenantMatchCount == 0)
        model.searchQuery = "billing"
        model.scopeTenant = nil
        #expect(model.otherTenantMatchCount == 0)
        cleanup(model)
    }

    @Test func searchAllTenantsKeepsTheQuery() async {
        let model = await makeTwoTenantModel()
        model.scopeTenant = Sample.tenantKey
        model.searchQuery = "billing"
        model.searchAllTenants()
        #expect(model.scopeTenant == nil)
        #expect(model.searchQuery == "billing")
        cleanup(model)
    }

    @Test func escapeClearsTheFilterThenReturnsHome() async {
        let model = await makeTwoTenantModel()
        model.active[Sample.entraKey] = Sample.assignment(Sample.entraKey)
        model.scopeTenant = Sample.tenantKey
        model.searchQuery = "glob"
        model.handleEscape()
        #expect(model.searchQuery.isEmpty)
        #expect(model.panelSelection == .tenant(Sample.tenantKey))
        model.handleEscape()
        #expect(model.panelSelection == .allTenants)
        #expect(model.panelContent == .home)
        cleanup(model)
    }

    @Test func escapeOnAProfileReturnsToTheTenantUnderneath() async {
        let model = await makeTwoTenantModel()
        let profile = model.saveProfile(name: "Ops", keys: [Sample.entraKey])!
        model.scopeTenant = Sample.tenantKey
        model.select(.profile(profile.id))
        #expect(model.panelSelection == .profile(profile.id))
        model.handleEscape()
        #expect(model.panelSelection == .tenant(Sample.tenantKey))
        cleanup(model)
    }

    @Test func aMissingProfileIsNotASelection() async {
        let model = await makeTwoTenantModel()
        model.selectedProfile = UUID()
        #expect(model.panelSelection == .allTenants)
        cleanup(model)
    }
}
