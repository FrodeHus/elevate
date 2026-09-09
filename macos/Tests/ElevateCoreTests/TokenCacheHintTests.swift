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

    @Test func affectedAccountsListsEachAccountOnceForActivatedAzureOrGroupRoles() {
        let outcomes = [activated(entra("a")), activated(azure("a")), activated(group("a")), activated(group("b")),
                        pending(azure("c")), ActivationOutcome(roleKey: azure("d"), result: .failed(.forbidden("no")))]
        #expect(TokenCacheHint.affectedAccounts(outcomes) == ["a", "b"])
    }

    @Test func nothingQualifyingMeansNoAccounts() {
        #expect(TokenCacheHint.affectedAccounts([activated(entra()), pending(group())]).isEmpty)
        #expect(TokenCacheHint.affectedAccounts([]).isEmpty)
    }

    @Test func wordingNamesTheAccountAndTheExactCommands() {
        #expect(TokenCacheHint.message(account: "alex@contoso.com").contains("alex@contoso.com"))
        #expect(TokenCacheHint.advice.contains("az login"))
        #expect(TokenCacheHint.advice.contains("kubelogin remove-tokens"))
        // az has no flag that forces a fresh token; signing in again is the only way.
        #expect(!TokenCacheHint.advice.contains("--force-refresh"))
    }
}
