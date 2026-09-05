import Testing
import Foundation
@testable import ElevateCore

@Suite struct GroupProviderTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t-home")
    let tenant = TenantContext(identityId: "id1", tenantId: "t1", displayName: "Contoso", source: .home)

    func makeProvider() -> (GroupProvider, StubHTTPClient, FakeTokenProvider) {
        let http = StubHTTPClient()
        let tokens = FakeTokenProvider()
        return (GroupProvider(http: http, tokens: tokens), http, tokens)
    }

    @Test func listsEligibleGroupsAcrossPagesWithAccessCaption() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("group-eligible-page2"))
        let roles = try await p.eligibleRoles(identity: identity, tenant: tenant)
        #expect(roles.map(\.displayName) == ["Dev Contributors", "Ops Admins", "Security Owners"])
        #expect(roles.map(\.detail) == ["member", "member", "owner"])
        #expect(roles[1].key.scope == .group(groupId: "grp-ops", accessId: .member))
        #expect(roles[2].key.scope == .group(groupId: "grp-sec", accessId: .owner))
        #expect(roles[2].viaGroup == "group")
        #expect(roles[1].viaGroup == nil)
        #expect(roles.allSatisfy { $0.source == .discovered && $0.key.tenantId == "t1" })
        let first = await http.requests.first!
        #expect(first.headers["Authorization"] == "Bearer token-t1")
        #expect(first.url.absoluteString.contains("identityGovernance/privilegedAccess/group/eligibilityScheduleInstances/filterByCurrentUser(on='principal')"))
        #expect(first.url.absoluteString.contains("expand=group"))
    }

    @Test func activeKeepsActivatedOnlyAndMergesPending() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "assignmentScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-active"))
        await http.on("GET", "assignmentScheduleRequests/filterByCurrentUser", body: Fixtures.data("group-pending"))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 2)
        let ops = active.first { $0.roleKey.scope == .group(groupId: "grp-ops", accessId: .member) }!
        #expect(ops.status == .active)
        #expect(ops.assignmentId == "ginst-1")
        #expect(ops.endDateTime == GraphJSON.parseDate("2026-09-04T16:00:00Z"))
        let sec = active.first { $0.roleKey.scope == .group(groupId: "grp-sec", accessId: .owner) }!
        #expect(sec.status == .pendingApproval)
        #expect(sec.assignmentId == "greq-9")
        let pendingReq = await http.requests(matching: "assignmentScheduleRequests").first!
        #expect(pendingReq.url.absoluteString.contains("status eq 'PendingApproval'") || pendingReq.url.absoluteString.contains("status%20eq%20'PendingApproval'"))
    }

    @Test func futureRequestsAppearAsScheduled() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "assignmentScheduleInstances/filterByCurrentUser", body: Data(#"{"value":[]}"#.utf8))
        await http.on("GET", "assignmentScheduleRequests/filterByCurrentUser", body: Fixtures.data("group-pending"))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 2)                        // greq-9 started this afternoon: not upcoming
        let ops = try #require(active.first { $0.roleKey.scope == .group(groupId: "grp-ops", accessId: .member) })
        #expect(ops.status == .scheduled)
        #expect(ops.assignmentId == "greq-1")
        #expect(ops.startDateTime == GraphJSON.parseDate("2099-01-01T09:00:00Z"))
        #expect(ops.endDateTime == GraphJSON.parseDate("2099-01-01T11:00:00Z"))
        let req = await http.requests(matching: "assignmentScheduleRequests").first!
        let url = req.url.absoluteString.removingPercentEncoding ?? req.url.absoluteString
        #expect(url.contains("status eq 'ScheduleCreated'") && url.contains("status eq 'Provisioned'"))
        // The schedules list is no longer read at all.
        #expect(await http.requests(matching: "assignmentSchedules/filterByCurrentUser").isEmpty)
    }

    @Test func futureRequestDoesNotOverrideAnActiveAssignment() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "assignmentScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-active"))
        await http.on("GET", "assignmentScheduleRequests/filterByCurrentUser", body: Fixtures.data("group-pending"))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 2)
        // greq-1 is a future request for the same group: the live assignment still wins.
        let ops = active.first { $0.roleKey.scope == .group(groupId: "grp-ops", accessId: .member) }!
        #expect(ops.status == .active)
        #expect(ops.assignmentId == "ginst-1")
    }

    @Test func forbiddenReadMapsToConsentRequired() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "eligibilityScheduleInstances", status: 403, body: Data(#"{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}"#.utf8))
        await #expect(throws: PIMError.consentRequired) {
            _ = try await p.eligibleRoles(identity: identity, tenant: tenant)
        }
    }

    /// A JWT-shaped Graph token whose `oid` names the caller.
    private struct JWTTokenProvider: TokenProviding {
        let oid: String
        private var token: String {
            let payload = try! JSONSerialization.data(withJSONObject: ["oid": oid, "tid": "t1"])
            let b64 = payload.base64EncodedString().replacingOccurrences(of: "+", with: "-")
                .replacingOccurrences(of: "/", with: "_").trimmingCharacters(in: CharacterSet(charactersIn: "="))
            return "eyJhbGciOiJub25lIn0.\(b64).sig"
        }
        func signIn(method: SignInMethod) async throws -> Identity { throw PIMError.notEligible }
        func signOut(_ identity: Identity) async throws {}
        func identities() async throws -> [Identity] { [] }
        func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String { token }
        func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String { token }
    }

    var opsMember: EligibleRole {
        EligibleRole(key: RoleKey(identityId: "id1", tenantId: "t1", scope: .group(groupId: "grp-ops", accessId: .member)),
                     displayName: "Ops Admins", detail: "member", source: .discovered, policy: .manualDefault)
    }

    @Test func policyIsReadPerGroupAndAccess() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleManagementPolicyAssignments", body: Fixtures.data("group-policy"))
        let policy = try await p.policy(for: opsMember, identity: identity)
        #expect(policy.maximumDuration == .seconds(8 * 3600))
        #expect(policy.defaultDuration == .seconds(8 * 3600))
        #expect(policy.requiresJustification && policy.requiresTicket && !policy.requiresMFA && !policy.requiresApproval)
        #expect(policy.authenticationContext == "c3")
        let req = await http.requests.first!
        let u = req.url.absoluteString.removingPercentEncoding ?? req.url.absoluteString
        #expect(u.contains("scopeId eq 'grp-ops'") && u.contains("scopeType eq 'Group'") && u.contains("roleDefinitionId eq 'member'"))
        #expect(u.contains("$expand=policy($expand=rules)"))
    }

    @Test func activatePostsSelfActivateAsTheCaller() async throws {
        let http = StubHTTPClient()
        let p = GroupProvider(http: http, tokens: JWTTokenProvider(oid: "caller-oid"))
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible-page2"))
        await http.on("POST", "assignmentScheduleRequests", status: 201, body: Fixtures.data("group-activate-response"))
        let a = try await p.activate(ActivationRequest(roleKey: opsMember.key, duration: .seconds(7200), justification: "INC-7", ticket: TicketInfo(number: "42", system: "Jira")), identity: identity)
        #expect(a.status == .active)
        #expect(a.assignmentId == "greq-new")
        #expect(a.endDateTime == GraphJSON.parseDate("2026-09-04T14:00:00Z"))
        #expect(a.roleKey == opsMember.key)
        let post = await http.requests(matching: "assignmentScheduleRequests").first { $0.method == "POST" }!
        #expect(post.url.absoluteString.hasSuffix("/identityGovernance/privilegedAccess/group/assignmentScheduleRequests"))
        let body = try JSONSerialization.jsonObject(with: post.body!) as! [String: Any]
        #expect(body["action"] as? String == "selfActivate")
        #expect(body["accessId"] as? String == "member")
        #expect(body["groupId"] as? String == "grp-ops")
        #expect(body["principalId"] as? String == "caller-oid")
        #expect(body["justification"] as? String == "INC-7")
        #expect((body["ticketInfo"] as? [String: Any])?["ticketNumber"] as? String == "42")
        let exp = (body["scheduleInfo"] as! [String: Any])["expiration"] as! [String: Any]
        #expect(exp["type"] as? String == "afterDuration" && exp["duration"] as? String == "PT2H")
    }

    @Test func opaqueTokenFallsBackToEligibilityPrincipal() async throws {
        let (p, http, _) = makeProvider()   // FakeTokenProvider returns "token-t1", not a JWT
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("group-eligible-page2"))
        await http.on("POST", "assignmentScheduleRequests", status: 201, body: Fixtures.data("group-activate-response"))
        _ = try await p.activate(ActivationRequest(roleKey: opsMember.key, duration: .seconds(3600), justification: "x"), identity: identity)
        let post = await http.requests(matching: "assignmentScheduleRequests").first { $0.method == "POST" }!
        let body = try JSONSerialization.jsonObject(with: post.body!) as! [String: Any]
        #expect(body["principalId"] as? String == "user-obj-1")
    }

    @Test func activateWithFutureStartSendsItAndReportsScheduled() async throws {
        let http = StubHTTPClient()
        let p = GroupProvider(http: http, tokens: JWTTokenProvider(oid: "caller-oid"))
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible-page2"))
        // Same response as a normal activation, but with no echoed end: the service works it out from the duration.
        var json = try JSONSerialization.jsonObject(with: Fixtures.data("group-activate-response")) as! [String: Any]
        var sched = json["scheduleInfo"] as! [String: Any]
        sched["expiration"] = ["type": "afterDuration", "duration": "PT2H"]
        json["scheduleInfo"] = sched
        await http.on("POST", "assignmentScheduleRequests", status: 201, body: try JSONSerialization.data(withJSONObject: json))
        let start = GraphJSON.parseDate("2099-01-01T09:00:00Z")!
        let a = try await p.activate(ActivationRequest(roleKey: opsMember.key, duration: .seconds(7200), justification: "later", startDateTime: start), identity: identity)

        let post = await http.requests(matching: "assignmentScheduleRequests").first { $0.method == "POST" }!
        let body = (try JSONSerialization.jsonObject(with: post.body!) as! [String: Any])["scheduleInfo"] as! [String: Any]
        #expect(GraphJSON.parseDate(body["startDateTime"] as! String) == start)
        // The response echoes a start in the past; the request's future start wins.
        #expect(a.status == .scheduled)
        #expect(a.startDateTime == start)
        #expect(a.endDateTime == GraphJSON.parseDate("2099-01-01T11:00:00Z"))
    }

    @Test func futureStartDoesNotMaskPendingApproval() async throws {
        let http = StubHTTPClient()
        let p = GroupProvider(http: http, tokens: JWTTokenProvider(oid: "caller-oid"))
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible-page2"))
        var json = try JSONSerialization.jsonObject(with: Fixtures.data("group-activate-response")) as! [String: Any]
        json["status"] = "PendingApproval"
        await http.on("POST", "assignmentScheduleRequests", status: 201, body: try JSONSerialization.data(withJSONObject: json))
        let start = GraphJSON.parseDate("2099-01-01T09:00:00Z")!
        let a = try await p.activate(ActivationRequest(roleKey: opsMember.key, duration: .seconds(3600), justification: "later", startDateTime: start), identity: identity)
        #expect(a.status == .pendingApproval)
    }

    @Test func futureStartDoesNotMaskFailure() async throws {
        let http = StubHTTPClient()
        let p = GroupProvider(http: http, tokens: JWTTokenProvider(oid: "caller-oid"))
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible-page2"))
        var json = try JSONSerialization.jsonObject(with: Fixtures.data("group-activate-response")) as! [String: Any]
        json["status"] = "Denied"
        await http.on("POST", "assignmentScheduleRequests", status: 201, body: try JSONSerialization.data(withJSONObject: json))
        let start = GraphJSON.parseDate("2099-01-01T09:00:00Z")!
        let a = try await p.activate(ActivationRequest(roleKey: opsMember.key, duration: .seconds(3600), justification: "later", startDateTime: start), identity: identity)
        #expect(a.status == .failed("Denied"))
    }

    @Test func pendingApprovalResponseIsReported() async throws {
        let http = StubHTTPClient()
        let p = GroupProvider(http: http, tokens: JWTTokenProvider(oid: "caller-oid"))
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible-page2"))
        await http.on("POST", "assignmentScheduleRequests", status: 201,
                      body: Data(#"{"id":"greq-p","status":"PendingApproval","groupId":"grp-ops","accessId":"member","scheduleInfo":{"startDateTime":"2026-09-04T12:00:00Z"}}"#.utf8))
        let a = try await p.activate(ActivationRequest(roleKey: opsMember.key, duration: .seconds(3600), justification: "x"), identity: identity)
        #expect(a.status == .pendingApproval)
        #expect(a.endDateTime == nil)
    }

    @Test func deactivatePostsSelfDeactivateWithoutSchedule() async throws {
        let http = StubHTTPClient()
        let p = GroupProvider(http: http, tokens: JWTTokenProvider(oid: "caller-oid"))
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible-page2"))
        await http.on("POST", "assignmentScheduleRequests", status: 201, body: Fixtures.data("group-activate-response"))
        let a = ActiveAssignment(roleKey: opsMember.key, assignmentId: "ginst-1", startDateTime: .now, endDateTime: nil, status: .active)
        try await p.deactivate(a, identity: identity)
        let post = await http.requests(matching: "assignmentScheduleRequests").first { $0.method == "POST" }!
        let body = try JSONSerialization.jsonObject(with: post.body!) as! [String: Any]
        #expect(body["action"] as? String == "selfDeactivate")
        #expect(body["principalId"] as? String == "caller-oid")
        #expect(body["groupId"] as? String == "grp-ops" && body["accessId"] as? String == "member")
        #expect(body["scheduleInfo"] == nil)
    }

    @Test func cancelPostsToTheRequestCancelAction() async throws {
        let (p, http, _) = makeProvider()
        await http.on("POST", "/cancel", status: 204)
        let a = ActiveAssignment(roleKey: opsMember.key, assignmentId: "greq-9", startDateTime: .now, endDateTime: nil, status: .pendingApproval)
        try await p.cancelPendingRequest(a, identity: identity)
        let post = await http.requests.first!
        #expect(post.method == "POST")
        #expect(post.url.absoluteString.hasSuffix("/identityGovernance/privilegedAccess/group/assignmentScheduleRequests/greq-9/cancel"))
    }
}
