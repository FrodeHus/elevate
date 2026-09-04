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

    var globalReader: EligibleRole {
        EligibleRole(key: RoleKey(identityId: "id1", tenantId: "t1",
                                  scope: .entraDirectory(roleDefinitionId: "f2ef992c-3afb-46b9-b7cf-a126ee74c451", directoryScopeId: "/")),
                     displayName: "Global Reader", source: .discovered, policy: .manualDefault)
    }

    @Test func readsEndUserActivationPolicy() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleManagementPolicyAssignments", body: Fixtures.data("entra-policy"))
        let policy = try await p.policy(for: globalReader, identity: identity)
        #expect(policy.maximumDuration == .seconds(4 * 3600))
        #expect(policy.defaultDuration == .seconds(4 * 3600))
        #expect(policy.requiresJustification)
        #expect(policy.requiresMFA)
        #expect(!policy.requiresTicket)
        #expect(policy.requiresApproval)
        let req = await http.requests.first!
        #expect(req.url.absoluteString.contains("roleDefinitionId%20eq%20'f2ef992c-3afb-46b9-b7cf-a126ee74c451'")
                || req.url.absoluteString.contains("roleDefinitionId eq 'f2ef992c-3afb-46b9-b7cf-a126ee74c451'"))
    }

    @Test func activatePostsSelfActivateAndComputesEnd() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "/me?", body: Fixtures.data("me"))
        await http.on("POST", "roleAssignmentScheduleRequests", status: 201, body: Fixtures.data("entra-activate-response"))
        let request = ActivationRequest(roleKey: globalReader.key, duration: .seconds(7200), justification: "Ticket 42")
        let a = try await p.activate(request, identity: identity)
        #expect(a.status == .active)
        #expect(a.assignmentId == "req-1")
        #expect(a.startDateTime == GraphJSON.parseDate("2026-09-04T09:00:00Z"))
        #expect(a.endDateTime == GraphJSON.parseDate("2026-09-04T11:00:00Z"))

        let post = await http.requests(matching: "roleAssignmentScheduleRequests").first!
        let body = try JSONSerialization.jsonObject(with: post.body!) as! [String: Any]
        #expect(body["action"] as? String == "selfActivate")
        #expect(body["principalId"] as? String == "user-obj-1")
        #expect(body["roleDefinitionId"] as? String == "f2ef992c-3afb-46b9-b7cf-a126ee74c451")
        #expect(body["directoryScopeId"] as? String == "/")
        #expect(body["justification"] as? String == "Ticket 42")
        let sched = body["scheduleInfo"] as! [String: Any]
        let exp = sched["expiration"] as! [String: Any]
        #expect(exp["type"] as? String == "afterDuration")
        #expect(exp["duration"] as? String == "PT2H")
        #expect(body["ticketInfo"] == nil)
    }

    @Test func activateReportsPendingApproval() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "/me?", body: Fixtures.data("me"))
        var json = try JSONSerialization.jsonObject(with: Fixtures.data("entra-activate-response")) as! [String: Any]
        json["status"] = "PendingApproval"
        await http.on("POST", "roleAssignmentScheduleRequests", status: 201, body: try JSONSerialization.data(withJSONObject: json))
        let a = try await p.activate(ActivationRequest(roleKey: globalReader.key, duration: .seconds(3600), justification: "x"), identity: identity)
        #expect(a.status == .pendingApproval)
        #expect(a.assignmentId == "req-1")
    }

    @Test func activatePolicyFailureMapsToPolicyViolation() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "/me?", body: Fixtures.data("me"))
        await http.on("POST", "roleAssignmentScheduleRequests", status: 400,
                      body: Data(#"{"error":{"code":"RoleAssignmentRequestPolicyValidationFailed","message":"The following policy rules failed: [\"JustificationRule\"]"}}"#.utf8))
        await #expect(throws: PIMError.policyViolation(#"The following policy rules failed: ["JustificationRule"]"#)) {
            _ = try await p.activate(ActivationRequest(roleKey: globalReader.key, duration: .seconds(3600), justification: ""), identity: identity)
        }
    }

    @Test func deactivatePostsSelfDeactivate() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "/me?", body: Fixtures.data("me"))
        await http.on("POST", "roleAssignmentScheduleRequests", status: 201, body: Fixtures.data("entra-activate-response"))
        let a = ActiveAssignment(roleKey: globalReader.key, assignmentId: "inst-1", startDateTime: .now, endDateTime: nil, status: .active)
        try await p.deactivate(a, identity: identity)
        let post = await http.requests(matching: "roleAssignmentScheduleRequests").first!
        let body = try JSONSerialization.jsonObject(with: post.body!) as! [String: Any]
        #expect(body["action"] as? String == "selfDeactivate")
        #expect(body["scheduleInfo"] == nil)
    }

    @Test func cancelPendingRequestPostsToCancelEndpoint() async throws {
        let (p, http, _) = makeProvider()
        await http.on("POST", "roleAssignmentScheduleRequests/req-9/cancel", status: 204)
        let a = ActiveAssignment(roleKey: globalReader.key, assignmentId: "req-9", startDateTime: .now, endDateTime: nil, status: .pendingApproval)
        try await p.cancelPendingRequest(a, identity: identity)
        let post = await http.requests(matching: "/cancel").first!
        #expect(post.method == "POST")
        #expect(post.url.absoluteString == "https://graph.microsoft.com/v1.0/roleManagement/directory/roleAssignmentScheduleRequests/req-9/cancel")
        #expect(post.headers["Authorization"] == "Bearer token-t1")
    }

    @Test func cancelPendingRequestWithoutIdThrowsNotEligible() async throws {
        let (p, _, _) = makeProvider()
        let a = ActiveAssignment(roleKey: globalReader.key, assignmentId: nil, startDateTime: .now, endDateTime: nil, status: .pendingApproval)
        await #expect(throws: PIMError.notEligible) {
            try await p.cancelPendingRequest(a, identity: identity)
        }
    }
}
