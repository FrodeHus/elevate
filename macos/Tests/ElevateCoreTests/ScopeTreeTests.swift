import Testing
@testable import ElevateCore

/// Port of the C# `ScopeTreeTests`.
struct ScopeTreeTests {
    let subA = "/subscriptions/aaaaaaaa-0000-0000-0000-000000000000"
    let subB = "/subscriptions/bbbbbbbb-0000-0000-0000-000000000000"
    let mg = "/providers/Microsoft.Management/managementGroups/platform"

    private func role(_ name: String, _ scope: String, detail: String? = nil) -> EligibleRole {
        EligibleRole(
            key: RoleKey(identityId: "i", tenantId: "t", scope: .azureResource(scope: scope, roleDefinitionId: "rd-" + name)),
            displayName: name, detail: detail, source: .discovered, policy: .manualDefault)
    }

    @Test func buildNestsResourceGroupsUnderTheirSubscription() {
        let tree = ScopeTree.build([
            role("Contributor", subA + "/resourceGroups/prod", detail: "prod · resource group"),
            role("Reader", subA + "/resourceGroups/test", detail: "test · resource group"),
            role("Owner", subA, detail: "Platform · subscription"),
        ])

        #expect(tree.count == 1)
        let sub = tree[0]
        #expect(sub.kind == .subscription)
        // The role held on the subscription itself carries its caption.
        #expect(sub.title == "Platform")
        #expect(sub.roles.map(\.displayName) == ["Owner"])
        #expect(sub.children.map(\.title) == ["prod", "test"])
        #expect(sub.roleCount == 3)
    }

    @Test func buildShowsTheArmNameWhenNoRoleSitsOnTheScopeItself() {
        let tree = ScopeTree.build([
            role("Contributor", subA + "/resourceGroups/prod", detail: "prod · resource group"),
            role("Reader", subA + "/resourceGroups/test", detail: "test · resource group"),
        ])

        // Nothing is eligible on the subscription, so the service never named it.
        #expect(tree[0].displayName == nil)
        #expect(tree[0].title == "aaaaaaaa-0000-0000-0000-000000000000")
    }

    @Test func buildPutsManagementGroupsBesideSubscriptionsRatherThanAboveThem() {
        // ARM never repeats the management group in a subscription's scope, so the string cannot
        // tell us the subscription sits under it.
        let tree = ScopeTree.build([
            role("Reader", mg, detail: "Platform · management group"),
            role("Owner", subA, detail: "A · subscription"),
        ])
        #expect(tree.map(\.kind) == [.managementGroup, .subscription])
    }

    @Test func buildIgnoresRolesThatAreNotAzureResourceRoles() {
        let entra = EligibleRole(
            key: RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "rd", directoryScopeId: "/")),
            displayName: "Global Reader", source: .discovered, policy: .manualDefault)
        #expect(ScopeTree.build([entra, role("Owner", subA)]).count == 1)
    }

    @Test func buildSortsRootsByKindThenTitle() {
        let tree = ScopeTree.build([
            role("Owner", subB, detail: "Zulu · subscription"),
            role("Owner", subA, detail: "Alpha · subscription"),
            role("Reader", mg, detail: "Platform · management group"),
        ])
        #expect(tree.map(\.title) == ["Platform", "Alpha", "Zulu"])
    }

    @Test func flattenElidesAScopeThatLeadsToOneRoleAndBranchesNowhere() {
        // The motivating case: many subscriptions, one eligibility each. A header per subscription
        // would double the rows and show nothing the role row does not already say.
        let tree = ScopeTree.build([
            role("Contributor", subA, detail: "Alpha · subscription"),
            role("Contributor", subB, detail: "Zulu · subscription"),
        ])
        let rows = ScopeTree.flatten(tree)

        #expect(rows.count == 2)
        #expect(rows.allSatisfy { !$0.isScope })
        #expect(rows.allSatisfy { $0.depth == 0 })
    }

    @Test func flattenDrawsAHeaderAsSoonAsAScopeHasSomethingToBranchInto() {
        let tree = ScopeTree.build([
            role("Owner", subA, detail: "Alpha · subscription"),
            role("Contributor", subA + "/resourceGroups/prod", detail: "prod · resource group"),
        ])
        let rows = ScopeTree.flatten(tree)

        #expect(rows.map(\.depth) == [0, 1, 1])
        #expect(rows.map(\.isScope) == [true, false, false])
        #expect(rows.compactMap { $0.role?.displayName } == ["Owner", "Contributor"])
    }

    @Test func flattenDrawsAHeaderWhenOneScopeHoldsSeveralRoles() {
        let tree = ScopeTree.build([
            role("Owner", subA, detail: "Alpha · subscription"),
            role("Reader", subA, detail: "Alpha · subscription"),
        ])
        let rows = ScopeTree.flatten(tree)
        #expect(rows[0].isScope)
        #expect(rows.dropFirst().compactMap { $0.role?.displayName } == ["Owner", "Reader"])
    }

    @Test func flattenStopsAtACollapsedNode() {
        let tree = ScopeTree.build([
            role("Owner", subA, detail: "Alpha · subscription"),
            role("Contributor", subA + "/resourceGroups/prod", detail: "prod · resource group"),
        ])
        let rows = ScopeTree.flatten(tree) { $0.scope == subA }

        #expect(rows.count == 1)
        #expect(rows[0].isScope)
    }

    @Test func flattenNestsAResourceUnderItsResourceGroup() {
        let rg = subA + "/resourceGroups/prod"
        let tree = ScopeTree.build([
            role("Owner", rg, detail: "prod · resource group"),
            role("Reader", rg + "/providers/Microsoft.Compute/virtualMachines/web01", detail: "web01 · virtualmachines"),
            role("Reader", rg + "/providers/Microsoft.Compute/virtualMachines/web02", detail: "web02 · virtualmachines"),
        ])
        let rows = ScopeTree.flatten(tree)

        // The subscription is folded away — it holds no role of its own and leads to a single child.
        #expect(rows.map(\.depth) == [0, 1, 1, 1])
        #expect(rows.map(\.isScope) == [true, false, false, false])
        #expect(rows[0].node.kind == .resourceGroup)
    }

    @Test func buildFoldsAPassThroughScopeIntoTheNodeBelowIt() {
        let rg = subA + "/resourceGroups/prod"
        let tree = ScopeTree.build([
            role("Owner", rg, detail: "prod · resource group"),
            role("Reader", rg, detail: "prod · resource group"),
        ])

        #expect(tree.count == 1)
        #expect(tree[0].kind == .resourceGroup)
        #expect(tree[0].ancestors == ["aaaaaaaa-0000-0000-0000-000000000000"])
        #expect(tree[0].path == "aaaaaaaa-0000-0000-0000-000000000000 / prod")
        // Selecting the folded node must still reach the same subtree.
        #expect(tree[0].scope == rg)
    }

    @Test func buildDoesNotFoldAScopeThatHoldsARoleOfItsOwn() {
        let tree = ScopeTree.build([
            role("Owner", subA, detail: "Alpha · subscription"),
            role("Reader", subA + "/resourceGroups/prod", detail: "prod · resource group"),
        ])
        #expect(tree[0].kind == .subscription)
        #expect(tree[0].ancestors.isEmpty)
    }

    @Test func buildDoesNotFoldAScopeThatBranches() {
        let tree = ScopeTree.build([
            role("Owner", subA + "/resourceGroups/prod", detail: "prod · resource group"),
            role("Reader", subA + "/resourceGroups/test", detail: "test · resource group"),
        ])
        #expect(tree[0].kind == .subscription)
        #expect(tree[0].children.count == 2)
    }

    @Test func allRolesReachesEverythingBelowTheNode() {
        let tree = ScopeTree.build([
            role("Owner", subA, detail: "Alpha · subscription"),
            role("Contributor", subA + "/resourceGroups/prod", detail: "prod · resource group"),
            role("Reader", subA + "/resourceGroups/prod/providers/Microsoft.Compute/virtualMachines/web01", detail: "web01 · vm"),
        ])
        #expect(Set(tree[0].allRoles.map(\.displayName)) == ["Owner", "Contributor", "Reader"])
        #expect(tree[0].roleCount == 3)
    }
}
