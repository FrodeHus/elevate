import Testing
import Foundation
@testable import PimTrayCore

@Suite struct AzureResourceProviderTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t1")
    let tenant = TenantContext(identityId: "id1", tenantId: "t1", displayName: "Contoso", source: .home)
    let contributorId = "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c"
    let readerId = "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7"

    func makeProvider() -> (AzureResourceProvider, StubHTTPClient, FakeTokenProvider) {
        let http = StubHTTPClient()
        let tokens = FakeTokenProvider()
        return (AzureResourceProvider(http: http, tokens: tokens), http, tokens)
    }

    @Test func listsEligibleRolesAcrossPagesWithScopeCaption() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        let roles = try await p.eligibleRoles(identity: identity, tenant: tenant)
        #expect(roles.map(\.displayName) == ["Contributor", "Reader"])
        #expect(roles[0].detail == "Pay-As-You-Go · subscription")
        #expect(roles[1].detail == "rg-ops · resource group")
        #expect(roles[0].key.scope == .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: contributorId))
        #expect(roles.allSatisfy { $0.source == .discovered && $0.key.tenantId == "t1" })
        let first = await http.requests.first!
        #expect(first.url.absoluteString.hasPrefix("https://management.azure.com/providers/Microsoft.Authorization/roleEligibilityScheduleInstances"))
        #expect(first.url.absoluteString.contains("api-version=2020-10-01"))
        #expect(first.url.absoluteString.contains("asTarget()"))
        #expect(first.headers["Authorization"] == "Bearer token-t1")
        #expect(await http.requests.count == 2)
    }

    @Test func listsActivatedAndPendingAssignments() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleAssignmentScheduleInstances", body: Fixtures.data("arm-active"))
        await http.on("GET", "roleAssignmentScheduleRequests", body: Fixtures.data("arm-pending"))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 2)
        let contributor = active.first { $0.roleKey.scope == .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: contributorId) }!
        #expect(contributor.status == .active)
        #expect(contributor.assignmentId == "inst-1")
        #expect(contributor.endDateTime == GraphJSON.parseDate("2026-09-04T12:00:00Z"))
        let reader = active.first { $0.roleKey.scope == .azureResource(scope: "/subscriptions/sub-1/resourceGroups/rg-ops", roleDefinitionId: readerId) }!
        #expect(reader.status == .pendingApproval)
        #expect(reader.assignmentId == "req-77")
    }

    @Test func forbiddenIsNotTreatedAsConsent() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances", status: 403, body: Data(#"{"error":{"code":"AuthorizationFailed","message":"x"}}"#.utf8))
        await #expect(throws: PIMError.policyViolation("Not permitted at this scope")) {
            _ = try await p.eligibleRoles(identity: identity, tenant: tenant)
        }
    }
}
