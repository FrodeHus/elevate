import Testing
import Foundation
@testable import ElevateCore

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
        #expect(roles[0].viaGroup == nil)                 // Global Reader: memberType Direct
        #expect(roles[1].viaGroup == "group")             // User Administrator: memberType Group
        let req = await http.requests.first!
        #expect(req.headers["Authorization"] == "Bearer token-t1")
        #expect(req.url.absoluteString.contains("expand=roleDefinition"))
    }

    @Test func listsOnlyActivatedAssignmentsAndMergesPending() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleAssignmentScheduleInstances/filterByCurrentUser", body: Fixtures.data("entra-active"))
        await http.on("GET", "roleAssignmentScheduleRequests/filterByCurrentUser", body: Fixtures.data("entra-pending-requests"))
        await http.on("GET", "roleAssignmentSchedules/filterByCurrentUser", body: Data(#"{"value":[]}"#.utf8))
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
        #expect(policy.authenticationContext == "c1")
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

    @Test func activateWithFutureStartSendsItAndReportsScheduled() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "/me?", body: Fixtures.data("me"))
        await http.on("POST", "roleAssignmentScheduleRequests", status: 201, body: Fixtures.data("entra-activate-response"))
        let start = GraphJSON.parseDate("2099-01-01T09:00:00Z")!
        let request = ActivationRequest(roleKey: globalReader.key, duration: .seconds(7200), justification: "later", startDateTime: start)
        let a = try await p.activate(request, identity: identity)

        let post = await http.requests(matching: "roleAssignmentScheduleRequests").first!
        let sched = (try JSONSerialization.jsonObject(with: post.body!) as! [String: Any])["scheduleInfo"] as! [String: Any]
        #expect(GraphJSON.parseDate(sched["startDateTime"] as! String) == start)
        // The response echoes a start in the past; the request's future start wins.
        #expect(a.status == .scheduled)
        #expect(a.startDateTime == start)
        #expect(a.endDateTime == GraphJSON.parseDate("2099-01-01T11:00:00Z"))
    }

    @Test func futureSchedulesAppearAsScheduled() async throws {
        let (p, http, _) = makeProvider()
        let empty = Data(#"{"value":[]}"#.utf8)
        await http.on("GET", "roleAssignmentScheduleInstances/filterByCurrentUser", body: empty)
        await http.on("GET", "roleAssignmentScheduleRequests/filterByCurrentUser", body: empty)
        await http.on("GET", "roleAssignmentSchedules/filterByCurrentUser", body: Fixtures.data("entra-schedules"))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 1)                        // the 2020 schedule is not upcoming
        let gr = try #require(active.first)
        #expect(gr.roleKey.scope == .entraDirectory(roleDefinitionId: "f2ef992c-3afb-46b9-b7cf-a126ee74c451", directoryScopeId: "/"))
        #expect(gr.status == .scheduled)
        #expect(gr.assignmentId == "sched-1")
        #expect(gr.startDateTime == GraphJSON.parseDate("2099-01-01T09:00:00Z"))
        #expect(gr.endDateTime == GraphJSON.parseDate("2099-01-01T11:00:00Z"))
    }

    @Test func futureScheduleDoesNotOverrideAnActiveAssignment() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleAssignmentScheduleInstances/filterByCurrentUser", body: Fixtures.data("entra-active"))
        await http.on("GET", "roleAssignmentScheduleRequests/filterByCurrentUser", body: Fixtures.data("entra-pending-requests"))
        await http.on("GET", "roleAssignmentSchedules/filterByCurrentUser", body: Fixtures.data("entra-schedules"))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 2)
        let gr = active.first { $0.roleKey.scope == .entraDirectory(roleDefinitionId: "f2ef992c-3afb-46b9-b7cf-a126ee74c451", directoryScopeId: "/") }!
        #expect(gr.status == .active)
        #expect(gr.assignmentId == "inst-1")
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

@Suite struct FirstPartyForbiddenTests {
    @Test func forbiddenForFirstPartyIdentityCarriesServerMessage() async throws {
        let http = StubHTTPClient()
        await http.on("GET", "roleEligibilitySchedules", status: 403, body: Data(#"{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}"#.utf8))
        let provider = EntraDirectoryProvider(http: http, tokens: FakeTokenProvider())
        let identity = Identity(id: "id1", upn: "u@x", displayName: "U", homeTenantId: "t1", signInMethod: .azureCLI)
        let tenant = TenantContext(identityId: "id1", tenantId: "t1", displayName: "T", source: .home)
        await #expect(throws: PIMError.forbidden("Insufficient privileges to complete the operation.")) {
            _ = try await provider.eligibleRoles(identity: identity, tenant: tenant)
        }
        #expect(PIMError.forbidden("x").userMessage == "Not permitted: x")
        #expect(PIMError.unexpected(status: 500, body: "boom").userMessage == "Unexpected response (500): boom")
    }
}

@Suite struct FirstPartyScopeMessageTests {
    @Test func permissionScopeNotGrantedExplainsTheLimitation() {
        let body = #"{"error":{"code":"Authorization_RequestDenied","message":"Authorization failed due to missing permission scope RoleAssignmentSchedule.ReadWrite.Directory,RoleManagement.ReadWrite.Directory.","innerError":{"errorCode":"PermissionScopeNotGranted"}}}"#
        let m = GraphTransport.firstPartyForbiddenMessage(body: body, method: .azureCLI)
        #expect(m.hasPrefix("The Azure CLI app is not granted RoleAssignmentSchedule.ReadWrite.Directory in this tenant."))
        #expect(m.contains("try the Azure PowerShell app"))
        #expect(GraphTransport.firstPartyForbiddenMessage(body: #"{"error":{"code":"x","message":"plain"}}"#, method: .azureCLI) == "plain")
    }
}
