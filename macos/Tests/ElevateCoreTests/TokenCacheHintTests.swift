import Testing
import Foundation
@testable import ElevateCore

@Suite struct TokenCacheHintTests {
    let now = Date(timeIntervalSince1970: 1_000_000)

    func entra(_ identity: String = "i") -> RoleKey { RoleKey(identityId: identity, tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/")) }
    func azure(_ identity: String = "i") -> RoleKey { RoleKey(identityId: identity, tenantId: "t", scope: .azureResource(scope: "/subscriptions/s1", roleDefinitionId: "owner")) }
    func group(_ identity: String = "i") -> RoleKey { RoleKey(identityId: identity, tenantId: "t", scope: .group(groupId: "g1", accessId: .member)) }

    func activated(_ key: RoleKey) -> ActivationOutcome {
        ActivationOutcome(roleKey: key, result: .activated(ActiveAssignment(roleKey: key, assignmentId: "a", startDateTime: now,
                                                                             endDateTime: now.addingTimeInterval(3600), status: .active)))
    }
    func pending(_ key: RoleKey) -> ActivationOutcome {
        ActivationOutcome(roleKey: key, result: .pendingApproval(ActiveAssignment(roleKey: key, assignmentId: "p", startDateTime: now,
                                                                                   endDateTime: nil, status: .pendingApproval)))
    }

    @Test func azureAndGroupActivationsQualifyEntraDoesNot() {
        #expect(TokenCacheHint.qualifies(azure()))
        #expect(TokenCacheHint.qualifies(group()))
        #expect(!TokenCacheHint.qualifies(entra()))
    }

    /// Every account signed in through the Azure CLI app, so the tool cache is shared.
    func cli(_: String) -> SignInMethod? { .azureCLI }

    @Test func affectedAccountsListsEachAccountOnceForActivatedAzureOrGroupRoles() {
        let outcomes = [activated(entra("a")), activated(azure("a")), activated(group("a")), activated(group("b")),
                        pending(azure("c")), ActivationOutcome(roleKey: azure("d"), result: .failed(.forbidden("no")))]
        #expect(TokenCacheHint.affectedAccounts(outcomes, method: cli) == ["a", "b"])
    }

    @Test func onlyAccountsSharingTheToolTokenCacheAreAffected() {
        let methods: [String: SignInMethod] = ["cli": .azureCLI, "ps": .azurePowerShell, "app": .ownApp,
                                               "custom": .custom(clientId: "11111111-1111-1111-1111-111111111111")]
        let outcomes = [activated(azure("app")), activated(azure("cli")), activated(group("custom")), activated(group("ps")), activated(azure("gone"))]
        // An app registration has its own cache: Elevate cannot tell whether the tools were ever used as that account.
        #expect(TokenCacheHint.affectedAccounts(outcomes) { methods[$0] } == ["cli", "ps"])
        #expect(SignInMethod.azureCLI.sharesToolTokenCache)
        #expect(SignInMethod.azurePowerShell.sharesToolTokenCache)
        #expect(!SignInMethod.ownApp.sharesToolTokenCache)
        #expect(!methods["custom"]!.sharesToolTokenCache)
    }

    @Test func nothingQualifyingMeansNoAccounts() {
        #expect(TokenCacheHint.affectedAccounts([activated(entra()), pending(group())], method: cli).isEmpty)
        #expect(TokenCacheHint.affectedAccounts([], method: cli).isEmpty)
    }

    @Test func wordingNamesTheAccountAndTheExactCommands() {
        #expect(TokenCacheHint.message(account: "alex@contoso.com").contains("alex@contoso.com"))
        #expect(TokenCacheHint.advice.contains("az login"))
        #expect(TokenCacheHint.advice.contains("kubelogin remove-tokens"))
        // az has no flag that forces a fresh token; signing in again is the only way.
        #expect(!TokenCacheHint.advice.contains("--force-refresh"))
    }
}
