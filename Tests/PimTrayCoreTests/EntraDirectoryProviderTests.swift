import Testing
import Foundation
@testable import PimTrayCore

@Suite struct EntraDirectoryProviderTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t-home")
    let tenant = TenantContext(identityId: "id1", tenantId: "t1", displayName: "Contoso", source: .home)

    func makeProvider() -> (EntraDirectoryProvider, StubHTTPClient, FakeTokenProvider) {
        let http = StubHTTPClient()
        let tokens = FakeTokenProvider()
        return (EntraDirectoryProvider(http: http, tokens: tokens), http, tokens)
    }

    @Test func listsEligibleRolesWithBearerTokenForTenant() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilitySchedules/filterByCurrentUser", body: Fixtures.data("entra-eligible"))
        let roles = try await p.eligibleRoles(identity: identity, tenant: tenant)
        #expect(roles.map(\.displayName) == ["Global Reader", "User Administrator"])
        #expect(roles.allSatisfy { $0.source == .discovered && $0.key.tenantId == "t1" && $0.key.identityId == "id1" })
        #expect(roles[0].key.scope == .entraDirectory(roleDefinitionId: "f2ef992c-3afb-46b9-b7cf-a126ee74c451", directoryScopeId: "/"))
        let req = await http.requests.first!
        #expect(req.headers["Authorization"] == "Bearer token-t1")
        #expect(req.url.absoluteString.contains("expand=roleDefinition"))
    }

    @Test func listsOnlyActivatedAssignmentsAndMergesPending() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleAssignmentScheduleInstances/filterByCurrentUser", body: Fixtures.data("entra-active"))
        await http.on("GET", "roleAssignmentScheduleRequests/filterByCurrentUser", body: Fixtures.data("entra-pending-requests"))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 2)
        let gr = active.first { $0.roleKey.scope == .entraDirectory(roleDefinitionId: "f2ef992c-3afb-46b9-b7cf-a126ee74c451", directoryScopeId: "/") }!
        #expect(gr.status == .active)
        #expect(gr.assignmentId == "inst-1")
        #expect(gr.endDateTime == GraphJSON.parseDate("2026-09-04T16:00:00Z"))
        let ua = active.first { $0.roleKey.scope == .entraDirectory(roleDefinitionId: "fe930be7-5e62-47db-91af-98c3a49a38b1", directoryScopeId: "/") }!
        #expect(ua.status == .pendingApproval)
        #expect(ua.assignmentId == "req-9")
    }

    @Test func forbiddenMapsToConsentRequired() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilitySchedules", status: 403, body: Data(#"{"error":{"code":"Authorization_RequestDenied"}}"#.utf8))
        await #expect(throws: PIMError.consentRequired) {
            _ = try await p.eligibleRoles(identity: identity, tenant: tenant)
        }
    }

    @Test func unauthorizedWithClaimsMapsToClaimsChallenge() async throws {
        let (p, http, _) = makeProvider()
        let claims = #"{"access_token":{"acrs":{"essential":true,"values":["c1"]}}}"#
        let b64 = Data(claims.utf8).base64EncodedString()
        await http.on("GET", "roleEligibilitySchedules", status: 401,
                      headers: ["WWW-Authenticate": #"Bearer error="insufficient_claims", claims="\#(b64)""#])
        await #expect(throws: PIMError.claimsChallenge(claims)) {
            _ = try await p.eligibleRoles(identity: identity, tenant: tenant)
        }
    }

    @Test func silentTokenFailurePropagatesInteractionRequired() async throws {
        let (p, _, tokens) = makeProvider()
        await tokens.setSilentError(.interactionRequired)
        await #expect(throws: PIMError.interactionRequired) {
            _ = try await p.eligibleRoles(identity: identity, tenant: tenant)
        }
    }
}
