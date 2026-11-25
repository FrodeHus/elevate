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
        #expect(model.handleEscape())
        #expect(model.searchQuery.isEmpty)
        #expect(model.panelSelection == .tenant(Sample.tenantKey))
        // Nothing left to dismiss: false (the panel closes) and the tenant stays selected.
        #expect(!model.handleEscape())
        #expect(model.panelSelection == .tenant(Sample.tenantKey))
        cleanup(model)
    }

    @Test func escapeOnATenantViewWithNothingToDismissReturnsFalseAndKeepsTheTenant() async {
        let model = await makeTwoTenantModel()
        model.select(.tenant(Sample.tenantKey))
        #expect(!model.handleEscape())
        #expect(model.scopeTenant == Sample.tenantKey)
        #expect(model.panelContent == .tenant(Sample.tenantKey))
        cleanup(model)
    }

    @Test func leavingAProfileReturnsToTheSelectionItWasOpenedFrom() async {
        let model = await makeTwoTenantModel()
        let profile = model.saveProfile(name: "Ops", keys: [Sample.entraKey])!
        model.scopeTenant = Sample.tenantKey
        model.select(.profile(profile.id))
        #expect(model.panelSelection == .profile(profile.id))
        #expect(model.handleEscape())
        #expect(model.panelSelection == .tenant(Sample.tenantKey))
        model.select(.allTenants)
        model.select(.profile(profile.id))
        #expect(model.handleEscape())
        #expect(model.panelSelection == .allTenants)
        cleanup(model)
    }

    @Test func selectModeAndBrowsingEachTakeOnePress() async {
        let model = await makeTwoTenantModel()
        model.active[Sample.entraKey] = Sample.assignment(Sample.entraKey)
        model.browsing = true
        model.selectMode = true
        #expect(model.handleEscape())
        #expect(!model.selectMode && model.browsing)
        #expect(model.handleEscape())
        #expect(!model.browsing && model.panelContent == .home)
        #expect(!model.handleEscape())
        cleanup(model)
    }

    @Test func browsingIsNotDismissedFromATenantView() async {
        let model = await makeTwoTenantModel()
        model.select(.tenant(Sample.tenantKey))
        model.browsing = true
        #expect(!model.handleEscape())
        #expect(model.panelSelection == .tenant(Sample.tenantKey))
        cleanup(model)
    }

    private static let package = AccessPackage(id: "pkg-1", displayName: "Finance Reporting Tools")

    @Test func accessPackagesAndTheRequestFormReplaceTheTenantView() async {
        let model = await makeTwoTenantModel()
        model.select(.tenant(Sample.tenantKey))
        model.openAccessPackages(Sample.tenantKey)
        #expect(model.panelContent == .accessPackages(Sample.tenantKey))
        model.beginPackageRequest(Self.package)
        #expect(model.panelContent == .requestPackage(Sample.tenantKey, Self.package))
        model.endPackageRequest()
        #expect(model.panelContent == .accessPackages(Sample.tenantKey))
        cleanup(model)
    }

    @Test func escapeStepsBackFormThenPackagesThenTheRoleListThenReturnsFalse() async {
        let model = await makeTwoTenantModel()
        model.select(.tenant(Sample.tenantKey))
        model.openAccessPackages(Sample.tenantKey)
        model.beginPackageRequest(Self.package)
        #expect(model.handleEscape())
        #expect(model.panelContent == .accessPackages(Sample.tenantKey))
        #expect(model.handleEscape())
        #expect(model.panelContent == .tenant(Sample.tenantKey))
        #expect(!model.handleEscape())
        #expect(model.panelSelection == .tenant(Sample.tenantKey))
        cleanup(model)
    }

    @Test func selectingAnythingInTheSidebarLeavesAccessPackages() async {
        let model = await makeTwoTenantModel()
        let profile = model.saveProfile(name: "Ops", keys: [Sample.entraKey])!
        for target in [PanelSelection.allTenants, .tenant(Self.otherKey), .tenant(Sample.tenantKey), .profile(profile.id)] {
            model.select(.tenant(Sample.tenantKey))
            model.openAccessPackages(Sample.tenantKey)
            model.beginPackageRequest(Self.package)
            model.select(target)
            #expect(model.accessPackagesTenant == nil && model.requestingPackage == nil)
        }
        cleanup(model)
    }

    @Test func changingTheTenantWithoutSelectDropsStaleAccessPackages() async {
        let model = await makeTwoTenantModel()
        model.openAccessPackages(Sample.tenantKey)
        model.beginPackageRequest(Self.package)
        model.searchAllTenants()
        #expect(model.accessPackagesTenant == nil && model.requestingPackage == nil)
        model.openAccessPackages(Sample.tenantKey)
        model.scopeTenant = nil
        #expect(model.accessPackagesTenant == nil)
        // The next Escape is not swallowed by packages that are no longer shown.
        model.openAccessPackages(Sample.tenantKey)
        model.searchAllTenants()
        model.browsing = true
        model.handleEscape()
        #expect(!model.browsing)
        cleanup(model)
    }

    @Test func openConfirmationCardsAreCountedAndNeverGoNegative() async {
        let model = await makeTwoTenantModel()
        #expect(!model.confirmationIsOpen)
        model.confirmationOpened()
        model.confirmationOpened()
        #expect(model.confirmationIsOpen)
        model.confirmationClosed()
        #expect(model.confirmationIsOpen)
        model.confirmationClosed()
        #expect(!model.confirmationIsOpen)
        model.confirmationClosed()   // an unbalanced close must not owe a later open
        model.confirmationOpened()
        #expect(model.confirmationIsOpen)
        model.confirmationClosed()
        #expect(!model.confirmationIsOpen)
        cleanup(model)
    }

    @Test func openingAccessPackagesFromTheAllTenantsListSelectsThatTenant() async {
        let model = await makeTwoTenantModel()
        model.openAccessPackages(Self.otherKey)
        #expect(model.panelSelection == .tenant(Self.otherKey))
        #expect(model.panelContent == .accessPackages(Self.otherKey))
        model.handleEscape()
        #expect(model.panelContent == .tenant(Self.otherKey))
        cleanup(model)
    }

    @Test func aMissingProfileIsNotASelection() async {
        let model = await makeTwoTenantModel()
        model.selectedProfile = UUID()
        #expect(model.panelSelection == .allTenants)
        cleanup(model)
    }

    @Test func arrowKeysAndReturnDoNothingOnHomeWhereNoRoleRowsAreDrawn() async {
        let model = await makeTwoTenantModel()
        model.active[Sample.azureKey] = Sample.assignment(Sample.azureKey)
        #expect(model.panelContent == .home)
        model.moveHighlight(1)
        #expect(model.highlightedRole == nil)
        #expect(model.returnTarget == nil)
        cleanup(model)
    }

    @Test func arrowKeysWalkATenantViewEvenWhenItsAccountIsCollapsedInTheSidebar() async {
        let model = await makeTwoTenantModel()
        model.select(.tenant(Sample.tenantKey))
        model.collapsedIdentities.insert(Sample.identity("id-1").id)
        model.moveHighlight(1)
        #expect(model.highlightedRole == Sample.entraKey)
        cleanup(model)
    }

    @Test func selectingAllTenantsWhileBrowsingGoesBackHome() async {
        let model = await makeTwoTenantModel()
        model.active[Sample.entraKey] = Sample.assignment(Sample.entraKey)
        model.browsing = true
        model.select(.allTenants)
        #expect(!model.browsing && model.panelContent == .home)
        cleanup(model)
    }

    @Test func escapeOnAProfileViewLeavesTheProfileEvenWithAFilterLeftBehind() async {
        let model = await makeTwoTenantModel()
        let profile = model.saveProfile(name: "Ops", keys: [Sample.entraKey])!
        model.searchQuery = "glob"
        model.select(.profile(profile.id))
        #expect(model.handleEscape())
        #expect(model.panelSelection == .allTenants)
        #expect(model.searchQuery == "glob")
        cleanup(model)
    }

    @Test func staleAccessPackagesOfATenantThatNeedsSignInDoNotSwallowEscape() async {
        let model = await makeTwoTenantModel()
        model.select(.tenant(Sample.tenantKey))
        model.openAccessPackages(Sample.tenantKey)
        model.signInNeeded.insert(Sample.tenantKey.identityId)
        model.browsing = true
        #expect(model.panelContent != .accessPackages(Sample.tenantKey))
        #expect(model.handleEscape())
        #expect(!model.browsing)
        cleanup(model)
    }

    @Test func reopeningThePanelDropsAStaleSignOutCardAndTheProfilesPopover() async {
        let model = await makeTwoTenantModel()
        model.signOutRequest = "id-1"
        model.allProfilesOpen = true
        model.panelOpened()
        #expect(model.signOutRequest == nil && !model.allProfilesOpen)
        cleanup(model)
    }

    @Test func closingThePanelClearsALeftoverConfirmation() async {
        let model = await makeModel()
        model.confirmationOpened()
        model.panelClosed()
        #expect(!model.confirmationIsOpen)
        cleanup(model)
    }
}
