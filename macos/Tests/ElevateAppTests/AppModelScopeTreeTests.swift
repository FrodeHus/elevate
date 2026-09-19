import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// The Azure tab's scope tree: what it groups, what a search leaves, and what the subtree checkbox takes.
@MainActor
struct AppModelScopeTreeTests {
    private static let subA = "/subscriptions/aaaaaaaa-0000-0000-0000-000000000000"
    private static let subB = "/subscriptions/bbbbbbbb-0000-0000-0000-000000000000"

    private func azure(_ scope: String, _ name: String, detail: String? = nil,
                       identityId: String = Sample.identityId, tenantId: String = Sample.tenantId) -> EligibleRole {
        EligibleRole(key: Sample.key(.azureResource(scope: scope, roleDefinitionId: "rd-" + name),
                                     identityId: identityId, tenantId: tenantId),
                     displayName: name, detail: detail, source: .discovered, policy: .manualDefault)
    }

    /// Two subscriptions: one branching into resource groups, one holding a single role.
    private func seeded() async -> AppModel {
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]
        let model = await makeModel(state: state)
        model.roles[Sample.tenantKey] = [
            azure(Self.subA, "Owner", detail: "Alpha · subscription"),
            azure(Self.subA + "/resourceGroups/prod-rg", "Contributor", detail: "prod-rg · resource group"),
            azure(Self.subA + "/resourceGroups/test-rg", "Reader", detail: "test-rg · resource group"),
            azure(Self.subB, "Contributor", detail: "Bravo · subscription"),
            Sample.role(Sample.entraKey, name: "Global Reader"),
        ]
        return model
    }

    @Test func azureTreeGroupsResourceGroupsUnderTheirSubscriptionAndLeavesEntraOut() async {
        let model = await seeded()
        let tree = model.azureTree(for: Sample.tenantKey)

        #expect(tree.map(\.title) == ["Alpha", "Bravo"])
        #expect(tree[0].children.map(\.title) == ["prod-rg", "test-rg"])
        #expect(tree[0].roleCount == 3)
        #expect(tree[1].roleCount == 1)
        cleanup(model)
    }

    @Test func azureTreeNarrowsWithTheSearchBox() async {
        let model = await seeded()
        model.searchQuery = "prod-rg"

        let tree = model.azureTree(for: Sample.tenantKey)
        #expect(tree.count == 1)
        #expect(tree[0].allRoles.map(\.displayName) == ["Contributor"])
        cleanup(model)
    }

    @Test func theSearchBoxReachesTheArmPathEvenWhenNoCaptionShowsIt() async {
        let model = await seeded()
        model.searchQuery = "bbbbbbbb"

        #expect(model.azureTree(for: Sample.tenantKey).flatMap(\.allRoles).count == 1)
        cleanup(model)
    }

    @Test func aCollapsedScopeReopensWhenToggledAgain() async {
        let model = await seeded()
        let alpha = model.azureTree(for: Sample.tenantKey)[0]

        #expect(model.isScopeCollapsed(Sample.tenantKey, alpha) == false, "scopes start open, as tenants do")
        model.toggleScope(Sample.tenantKey, alpha)
        #expect(model.isScopeCollapsed(Sample.tenantKey, alpha))
        model.toggleScope(Sample.tenantKey, alpha)
        #expect(model.isScopeCollapsed(Sample.tenantKey, alpha) == false)
        cleanup(model)
    }

    @Test func aClosedScopeStillShowsItsMatchesWhileSearching() async {
        // A match must never be hidden behind a node the user closed earlier.
        let model = await seeded()
        model.toggleScope(Sample.tenantKey, model.azureTree(for: Sample.tenantKey)[0])

        model.searchQuery = "prod"
        let tree = model.azureTree(for: Sample.tenantKey)
        #expect(!tree.isEmpty)
        #expect(model.isScopeCollapsed(Sample.tenantKey, tree[0]) == false)
        cleanup(model)
    }

    @Test func toggleSubtreeTakesEveryEligibilityUnderTheScope() async {
        let model = await seeded()
        let alpha = model.azureTree(for: Sample.tenantKey)[0]

        #expect(model.subtreeState(alpha) == .none)
        #expect(model.toggleSubtree(alpha) == 3)
        #expect(model.subtreeState(alpha) == .all)
        #expect(model.selection.count == 3)
        cleanup(model)
    }

    @Test func pressingAFullyChosenSubtreeAgainLetsItGo() async {
        let model = await seeded()
        let alpha = model.azureTree(for: Sample.tenantKey)[0]

        _ = model.toggleSubtree(alpha)
        #expect(model.toggleSubtree(alpha) == 3)
        #expect(model.selection.isEmpty)
        cleanup(model)
    }

    @Test func aPartlyChosenSubtreeFillsUpRatherThanEmptying() async {
        let model = await seeded()
        let alpha = model.azureTree(for: Sample.tenantKey)[0]
        model.toggleSelection(alpha.roles[0].key)

        #expect(model.subtreeState(alpha) == .some)
        // The one already chosen is left where it is.
        #expect(model.toggleSubtree(alpha) == 2)
        #expect(model.subtreeState(alpha) == .all)
        cleanup(model)
    }

    @Test func aSubtreeCheckboxOnlyTakesWhatCanActuallyBeActivated() async {
        let model = await seeded()
        let alpha = model.azureTree(for: Sample.tenantKey)[0]
        model.active[alpha.roles[0].key] = Sample.assignment(alpha.roles[0].key)

        #expect(model.subtreeKeys(alpha).count == 2)
        #expect(model.toggleSubtree(alpha) == 2)
        #expect(model.subtreeState(alpha) == .all)
        cleanup(model)
    }

    @Test func aSubtreeWithNothingToTakeReportsNoSelection() async {
        let model = await seeded()
        let bravo = model.azureTree(for: Sample.tenantKey)[1]
        model.active[bravo.roles[0].key] = Sample.assignment(bravo.roles[0].key)

        #expect(model.subtreeKeys(bravo).isEmpty)
        #expect(model.subtreeState(bravo) == .none)
        #expect(model.toggleSubtree(bravo) == 0)
        cleanup(model)
    }

    @Test func theSameScopeUnderTwoAccountsCollapsesIndependently() async {
        var state = AppState()
        state.identities = [Sample.identity("id-1"), Sample.identity("id-2")]
        state.tenants = [Sample.tenant(identityId: "id-1"), Sample.tenant(identityId: "id-2", tenantId: "tenant-2")]
        let model = await makeModel(state: state)
        let first = TenantKey(identityId: "id-1", tenantId: Sample.tenantId)
        let second = TenantKey(identityId: "id-2", tenantId: "tenant-2")
        model.roles[first] = [
            azure(Self.subA, "Owner", identityId: "id-1"),
            azure(Self.subA + "/resourceGroups/rg", "Reader", identityId: "id-1"),
        ]
        model.roles[second] = [
            azure(Self.subA, "Owner", identityId: "id-2", tenantId: "tenant-2"),
            azure(Self.subA + "/resourceGroups/rg", "Reader", identityId: "id-2", tenantId: "tenant-2"),
        ]

        model.toggleScope(first, model.azureTree(for: first)[0])
        #expect(model.isScopeCollapsed(first, model.azureTree(for: first)[0]))
        #expect(model.isScopeCollapsed(second, model.azureTree(for: second)[0]) == false)
        cleanup(model)
    }

    @Test func theFlattenedRowsSkipWhatAClosedScopeHides() async {
        let model = await seeded()
        let tree = model.azureTree(for: Sample.tenantKey)

        #expect(ScopeTree.flatten(tree, isCollapsed: { model.isScopeCollapsed(Sample.tenantKey, $0) }).count == 5)
        model.toggleScope(Sample.tenantKey, tree[0])
        // Alpha's header stays, its three rows go, and Bravo's single role row remains.
        #expect(ScopeTree.flatten(tree, isCollapsed: { model.isScopeCollapsed(Sample.tenantKey, $0) }).count == 2)
        cleanup(model)
    }
}
