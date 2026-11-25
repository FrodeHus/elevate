import Foundation
import Testing
import ElevateCore
@testable import Elevate

@MainActor
struct AppModelPanelTests {
    @Test func allTabShowsEveryKindInOrder() async {
        let model = await modelWithOneOfEachKind()
        model.panelTab = .all
        #expect(model.roles(for: Sample.tenantKey, tab: .all).count == 3)
        #expect(model.listRows(for: Sample.tenantKey) == [Sample.entraKey, Sample.azureKey, Sample.groupKey])
        #expect(model.navigableRoles == [Sample.entraKey, Sample.azureKey, Sample.groupKey])
        cleanup(model)
    }

    @Test func tabDefaultsToAllWhenNothingIsStoredAndKeepsAStoredTab() {
        let suite = "elevate-tests-\(UUID().uuidString)"
        defer { UserDefaults(suiteName: suite)?.removePersistentDomain(forName: suite) }
        #expect(AppSettings(defaults: UserDefaults(suiteName: suite)!, managed: ManagedConfiguration.none).panelTab == .all)
        UserDefaults(suiteName: suite)!.set("roles", forKey: AppSettings.panelTabKey)
        #expect(AppSettings(defaults: UserDefaults(suiteName: suite)!, managed: ManagedConfiguration.none).panelTab == .roles)
    }

    @Test func theScopedTenantSurvivesARelaunchAndClearingItForgetsIt() {
        let suite = "elevate-tests-\(UUID().uuidString)"
        defer { UserDefaults(suiteName: suite)?.removePersistentDomain(forName: suite) }
        func settings() -> AppSettings { AppSettings(defaults: UserDefaults(suiteName: suite)!, managed: ManagedConfiguration.none) }
        #expect(settings().panelTenant == nil)
        let fresh = settings()
        fresh.panelTenant = Sample.tenantKey
        #expect(settings().panelTenant == Sample.tenantKey)
        fresh.panelTenant = nil
        #expect(settings().panelTenant == nil)
    }

    @Test func privilegeComesFromTheFlagOrTheCatalogueButNeverForGroups() async {
        let model = await modelWithOneOfEachKind()
        let flagged = Sample.key(.entraDirectory(roleDefinitionId: "custom-role", directoryScopeId: "/"))
        model.roles[Sample.tenantKey] = [EligibleRole(key: flagged, displayName: "Custom", source: .discovered, policy: .manualDefault, isPrivileged: true)]
        #expect(model.isPrivileged(flagged))
        // Not loaded, flag unknown: the catalogue (Global Administrator) and the Azure ids (Owner) still say so.
        #expect(model.isPrivileged(Sample.key(.entraDirectory(roleDefinitionId: "62e90394-69f5-4237-9190-012177145e10", directoryScopeId: "/"))))
        #expect(model.isPrivileged(Sample.key(.azureResource(scope: "/subscriptions/s1", roleDefinitionId: "8e3af657-a8ff-443c-a75c-2fe8c4bcb635"))))
        #expect(!model.isPrivileged(Sample.entraKey))
        #expect(!model.isPrivileged(Sample.groupKey))
        cleanup(model)
    }

    @Test func sidebarSectionsStartExpandedAndRememberBeingCollapsed() {
        let suite = "elevate-tests-\(UUID().uuidString)"
        defer { UserDefaults(suiteName: suite)?.removePersistentDomain(forName: suite) }
        let fresh = AppSettings(defaults: UserDefaults(suiteName: suite)!, managed: ManagedConfiguration.none)
        #expect(!fresh.sidebarProfilesCollapsed && !fresh.sidebarTenantsCollapsed)
        fresh.sidebarProfilesCollapsed = true
        let again = AppSettings(defaults: UserDefaults(suiteName: suite)!, managed: ManagedConfiguration.none)
        #expect(again.sidebarProfilesCollapsed && !again.sidebarTenantsCollapsed)
        again.sidebarProfilesCollapsed = false
        again.sidebarTenantsCollapsed = true
        let last = AppSettings(defaults: UserDefaults(suiteName: suite)!, managed: ManagedConfiguration.none)
        #expect(!last.sidebarProfilesCollapsed && last.sidebarTenantsCollapsed)
    }

    @Test func groupCaptionNamesMembershipAndOwnership() async {
        let model = await modelWithOneOfEachKind()
        #expect(model.groupCaption(Sample.groupKey) == "Group membership")
        #expect(model.groupCaption(Sample.key(.group(groupId: "g", accessId: .owner))) == "Group ownership")
        #expect(model.groupCaption(Sample.entraKey) == nil)
        cleanup(model)
    }

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
        cleanup(model)
    }

    @Test func appRolesHaveTheirOwnTabAndStayOutOfEntra() async {
        let model = await modelWithOneOfEachKind()
        let appKey = Sample.key(.appRole(servicePrincipalId: "sp-1", appRoleId: "role-1"))
        model.roles[Sample.tenantKey, default: []].append(Sample.role(appKey, name: "Security Manager"))
        #expect(model.roles(for: Sample.tenantKey, tab: .apps).map(\.displayName) == ["Security Manager"])
        #expect(model.roles(for: Sample.tenantKey, tab: .roles).map(\.displayName) == ["Global Reader"])
        model.panelTab = .all
        #expect(model.listRows(for: Sample.tenantKey).last == appKey)
        cleanup(model)
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
        cleanup(model)
    }

    @Test func activeAssignmentsOrderedShowsEveryKind() async {
        let model = await modelWithOneOfEachKind()
        model.active = [
            Sample.entraKey: Sample.assignment(Sample.entraKey),
            Sample.azureKey: Sample.assignment(Sample.azureKey),
            Sample.groupKey: Sample.assignment(Sample.groupKey),
        ]
        for tab in [PanelTab.roles, .azure, .groups, .all] {
            model.panelTab = tab
            #expect(Set(model.activeAssignmentsOrdered.map(\.roleKey)) == [Sample.entraKey, Sample.azureKey, Sample.groupKey])
        }
        #expect(model.activeCount(for: .groups) == 1)
        cleanup(model)
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
        cleanup(model)
    }
}
