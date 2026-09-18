import Testing
@testable import ElevateCore

struct ArmActionsTests {
    @Test(arguments: [
        ("*", "Microsoft.Compute/virtualMachines/read", true),
        ("Microsoft.Compute/*", "Microsoft.Compute/virtualMachines/read", true),
        ("*/read", "Microsoft.Compute/virtualMachines/read", true),
        ("Microsoft.Compute/*/read", "Microsoft.Compute/virtualMachines/read", true),
        ("microsoft.compute/*", "Microsoft.Compute/virtualMachines/read", true),
        ("Microsoft.Storage/*", "Microsoft.Compute/virtualMachines/read", false),
        ("*/write", "Microsoft.Compute/virtualMachines/read", false),
        ("Microsoft.Compute/virtualMachines/read", "Microsoft.Compute/virtualMachines/read", true),
    ])
    func coversMatchesGlobsAcrossSlashesAndIgnoresCase(_ testCase: (String, String, Bool)) {
        let (pattern, action, expected) = testCase
        #expect(ArmActions.covers(pattern: pattern, action: action) == expected)
    }

    @Test func grantedStarAbsorbsTheLiteralStarAnOwnerDefinitionAsksFor() {
        #expect(ArmActions.covers(pattern: "*", action: "*"))
    }

    @Test func grantsIsFalseWhenTheSameEntryTakesTheActionBack() {
        let held = [ArmPermission(actions: ["*"], notActions: ["Microsoft.Authorization/*/write"])]
        #expect(ArmActions.grants(held, action: "Microsoft.Compute/virtualMachines/read"))
        #expect(!ArmActions.grants(held, action: "Microsoft.Authorization/roleAssignments/write"))
    }

    @Test func grantsIgnoresAnExclusionInADifferentEntry() {
        // ARM evaluates each assignment on its own: Reader's exclusions do not remove what Owner grants.
        let held = [
            ArmPermission(actions: ["*"], notActions: ["Microsoft.Authorization/*/write"]),
            ArmPermission(actions: ["*"]),
        ]
        #expect(ArmActions.grants(held, action: "Microsoft.Authorization/roleAssignments/write"))
    }

    @Test func coversIsTrueOnlyWhenEveryRequiredActionIsGranted() {
        let held = [ArmPermission(actions: ["Microsoft.Compute/*"])]
        #expect(ArmActions.covers(held, required: [ArmPermission(actions: ["Microsoft.Compute/virtualMachines/read"])]))
        #expect(!ArmActions.covers(held, required: [
            ArmPermission(actions: ["Microsoft.Compute/virtualMachines/read", "Microsoft.Storage/*"]),
        ]))
    }

    @Test func coversIsFalseWhenTheRoleDefinitionGrantsNothing() {
        // An empty permission set is a read that went wrong, not a role that is in effect.
        #expect(!ArmActions.covers([ArmPermission(actions: ["*"])], required: []))
    }
}
