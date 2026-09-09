import Testing
import Foundation
@testable import ElevateCore

@Suite struct ManagedPolicyTests {
    private func config(allowing kinds: Set<SignInMethodKind>?) -> ManagedConfiguration {
        var config = ManagedConfiguration()
        config.allowedSignInMethods = kinds
        return config
    }

    @Test func noAllowListAllowsEveryMethod() {
        let config = config(allowing: nil)
        for method in SignInMethod.builtIn + [.custom(clientId: "abc")] {
            #expect(ManagedPolicy.isAllowed(method, by: config))
        }
    }

    @Test func allowListRefusesMethodsOutsideIt() {
        let config = config(allowing: [.ownApp])
        #expect(ManagedPolicy.isAllowed(.ownApp, by: config))
        #expect(!ManagedPolicy.isAllowed(.azureCLI, by: config))
        #expect(!ManagedPolicy.isAllowed(.azurePowerShell, by: config))
        #expect(!ManagedPolicy.isAllowed(.custom(clientId: "abc"), by: config))
    }

    @Test func customInTheListAllowsAnyClientId() {
        let config = config(allowing: [.custom])
        #expect(ManagedPolicy.isAllowed(.custom(clientId: "abc"), by: config))
        #expect(ManagedPolicy.isAllowed(.custom(clientId: "11111111-2222-3333-4444-555555555555"), by: config))
        #expect(!ManagedPolicy.isAllowed(.ownApp, by: config))
    }

    @Test func emptyAllowListAllowsNothing() {
        #expect(!ManagedPolicy.isAllowed(.ownApp, by: config(allowing: [])))
    }

    @Test func noTenantAllowListAllowsEveryTenant() {
        #expect(ManagedPolicy.isTenantAllowed("11111111-2222-3333-4444-555555555555", allowedIds: nil))
    }

    @Test func tenantMatchIsCaseInsensitive() {
        let allowed: Set<String> = ["11111111-2222-3333-4444-AAAAAAAAAAAA"]
        #expect(ManagedPolicy.isTenantAllowed("11111111-2222-3333-4444-aaaaaaaaaaaa", allowedIds: allowed))
        #expect(ManagedPolicy.isTenantAllowed("11111111-2222-3333-4444-AAAAAAAAAAAA", allowedIds: allowed))
    }

    @Test func tenantOutsideTheListIsRefused() {
        #expect(!ManagedPolicy.isTenantAllowed("t-other", allowedIds: ["t-home"]))
        #expect(!ManagedPolicy.isTenantAllowed("t-home", allowedIds: []))
    }
}
