import Testing
import Foundation
@testable import ElevateCore

/// Payloads are trimmed copies of what Graph beta returned for the preview on 2026-11-25.
@Suite struct AppRoleProviderTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t-home")
    let tenant = TenantContext(identityId: "id1", tenantId: "t1", displayName: "Contoso", source: .home)
    let scope = RoleScope.appRole(servicePrincipalId: "sp-1", appRoleId: "role-sec")
    var key: RoleKey { RoleKey(identityId: "id1", tenantId: "t1", scope: scope) }

    static let eligible = Data(#"""
    {"value":[{"id":"sp-1_e1","startDateTime":"2026-11-25T06:55:40.8671888Z","endDateTime":null,"principalId":"user-1",
      "appRoleId":"role-sec","servicePrincipalId":"sp-1","memberType":"direct","eligibilityScheduleId":"sp-1_e1",
      "appRole":{"id":"role-sec","displayName":"Security Manager","value":"SecurityManager","origin":"Application"},
      "servicePrincipal":{"id":"sp-1","displayName":"Insights API","appId":null}}]}
    """#.utf8)

    static let instances = Data(#"""
    {"value":[
     {"id":"sp-1_standing","startDateTime":null,"endDateTime":null,"principalId":"user-1","appRoleId":"role-admin",
      "servicePrincipalId":"sp-1","memberType":"direct","assignmentType":"assigned","assignmentScheduleId":"sp-1_standing"},
     {"id":"sp-1_req-1","startDateTime":"2026-11-25T07:05:00.915844Z","endDateTime":"2026-11-25T07:35:00.454815Z","principalId":"user-1",
      "appRoleId":"role-sec","servicePrincipalId":"sp-1","memberType":"direct","assignmentType":"activated","assignmentScheduleId":"sp-1_req-1"}]}
    """#.utf8)

    static let created = Data(#"""
    {"id":"req-1","status":"PendingProvisioning","action":"selfActivate","principalId":"user-1","appRoleId":"role-sec",
     "servicePrincipalId":"sp-1","targetScheduleId":"sp-1_req-1","createdDateTime":"2026-11-25T07:04:59.5547522Z",
     "scheduleInfo":{"startDateTime":"2026-11-25T07:05:00.454815Z","recurrence":null,"expiration":{"type":"afterDuration","endDateTime":null,"duration":"PT30M"}}}
    """#.utf8)

    func makeProvider() -> (AppRoleProvider, StubHTTPClient) {
        let http = StubHTTPClient()
        return (AppRoleProvider(http: http, tokens: FakeTokenProvider()), http)
    }

    @Test func listsEligibleAppRolesFromBetaWithTheAppAsDetail() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Self.eligible)
        let roles = try await p.eligibleRoles(identity: identity, tenant: tenant)
        #expect(roles.map(\.displayName) == ["Security Manager"])
        #expect(roles.first?.detail == "Insights API")
        #expect(roles.first?.key == key)
        let url = await http.requests.first!.url.absoluteString
        #expect(url.contains("/beta/identityGovernance/privilegedAccess/appRole/eligibilityScheduleInstances/filterByCurrentUser(on='principal')"))
    }

    @Test func activeListsActivationsButNotStandingAssignments() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "assignmentScheduleInstances/filterByCurrentUser", body: Self.instances)
        await http.on("GET", "assignmentScheduleRequests/filterByCurrentUser", body: Data(#"{"value":[]}"#.utf8))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.map(\.roleKey) == [key])
        #expect(active.first?.status == .active)
        #expect(active.first?.scheduleId == "sp-1_req-1")
        #expect(active.first?.endDateTime == GraphJSON.parseDate("2026-11-25T07:35:00.454815Z"))
    }

    @Test(arguments: [400, 403, 404])
    func aTenantWithoutThePreviewReadsAsNoAppRoles(status: Int) async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "privilegedAccess/appRole", status: status, body: Data(#"{"error":{"code":"UnknownError","message":"The provider is missing."}}"#.utf8))
        #expect(try await p.eligibleRoles(identity: identity, tenant: tenant).isEmpty)
        #expect(try await p.activeAssignments(identity: identity, tenant: tenant).isEmpty)
    }

    @Test func aServerFaultIsAFailedReadNotAnEmptyOne() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "privilegedAccess/appRole", status: 503, body: Data(#"{"error":{"code":"ServiceUnavailable","message":"Try later"}}"#.utf8))
        await #expect(throws: PIMError.self) { _ = try await p.eligibleRoles(identity: identity, tenant: tenant) }
        await #expect(throws: PIMError.self) { _ = try await p.activeAssignments(identity: identity, tenant: tenant) }
    }

    @Test func activatePostsSelfActivateForTheAppRole() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Self.eligible)
        await http.on("POST", "assignmentScheduleRequests", status: 201, body: Self.created)
        let a = try await p.activate(ActivationRequest(roleKey: key, duration: .seconds(1800), justification: "INC-7"), identity: identity)
        #expect(a.status == .pendingProvisioning)
        #expect(a.assignmentId == "req-1" && a.scheduleId == "sp-1_req-1")
        let post = await http.requests(matching: "assignmentScheduleRequests").first { $0.method == "POST" }!
        #expect(post.url.absoluteString.hasSuffix("/beta/identityGovernance/privilegedAccess/appRole/assignmentScheduleRequests"))
        let body = try JSONSerialization.jsonObject(with: post.body!) as! [String: Any]
        #expect(body["action"] as? String == "selfActivate")
        #expect(body["servicePrincipalId"] as? String == "sp-1" && body["appRoleId"] as? String == "role-sec")
        #expect(body["principalId"] as? String == "user-1")   // FakeTokenProvider's token is opaque: the eligibility's principal
        #expect(body["justification"] as? String == "INC-7")
        let exp = (body["scheduleInfo"] as! [String: Any])["expiration"] as! [String: Any]
        #expect(exp["duration"] as? String == "PT30M")
    }

    @Test func deactivatePostsSelfDeactivateWithTheSchedule() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Self.eligible)
        await http.on("POST", "assignmentScheduleRequests", status: 201, body: Data(#"{"id":"req-2","status":"Revoked","servicePrincipalId":"sp-1","appRoleId":"role-sec"}"#.utf8))
        try await p.deactivate(ActiveAssignment(roleKey: key, assignmentId: "sp-1_req-1", startDateTime: .now, endDateTime: nil, status: .active, scheduleId: "sp-1_req-1"), identity: identity)
        let post = await http.requests(matching: "assignmentScheduleRequests").first { $0.method == "POST" }!
        let body = try JSONSerialization.jsonObject(with: post.body!) as! [String: Any]
        #expect(body["action"] as? String == "selfDeactivate" && body["targetScheduleId"] as? String == "sp-1_req-1")
    }

    @Test func policyIsReadPerAppAndRole() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "roleManagementPolicyAssignments", body: Fixtures.data("group-policy"))
        let role = EligibleRole(key: key, displayName: "Security Manager", detail: "Insights API", source: .discovered, policy: .manualDefault)
        let policy = try await p.policy(for: role, identity: identity)
        #expect(policy.maximumDuration == .seconds(8 * 3600))
        let req = await http.requests.first!
        let u = req.url.absoluteString.removingPercentEncoding ?? req.url.absoluteString
        #expect(u.contains("scopeId eq 'sp-1'") && u.contains("scopeType eq 'AppRole'") && u.contains("roleDefinitionId eq 'role-sec'"))
    }

    @Test func scopeSurvivesTheStateFile() throws {
        let data = try JSONEncoder().encode(scope)
        #expect(try JSONDecoder().decode(RoleScope.self, from: data) == scope)
        #expect(String(decoding: data, as: UTF8.self).contains(#""appRole":{"#))
    }

    // MARK: Approvals

    static let approverRequests = Data(#"""
    {"value":[{"id":"req-9","approvalId":"appr-9","status":"PendingApproval","action":"selfActivate","principalId":"user-2",
      "appRoleId":"role-sec","servicePrincipalId":"sp-1","justification":"INC-9","createdDateTime":"2026-11-25T07:04:59Z",
      "scheduleInfo":{"startDateTime":"2026-11-25T07:05:00Z","expiration":{"type":"afterDuration","duration":"PT2H"}},
      "appRole":{"id":"role-sec","displayName":"Security Manager","value":"SecurityManager"},
      "servicePrincipal":{"id":"sp-1","displayName":"Insights API"},
      "principal":{"@odata.type":"#microsoft.graph.user","displayName":"Sam Chen"}}]}
    """#.utf8)

    @Test func pendingApprovalsNameTheRoleItsAppAndTheRequester() async throws {
        let http = StubHTTPClient()
        let p = AppRoleApprovalProvider(http: http, tokens: FakeTokenProvider())
        await http.on("GET", "approver", body: Self.approverRequests)
        let items = try await p.pendingApprovals(identity: identity, tenant: tenant)
        #expect(items.count == 1)
        let item = try #require(items.first)
        #expect(item.kind == .appRole && item.targetName == "Security Manager" && item.scopeCaption == "Insights API")
        #expect(item.requesterName == "Sam Chen" && item.justification == "INC-9")
        #expect(item.requestedDuration == .seconds(7200) && item.decisionRef == "appr-9")
        let url = await http.requests.first!.url.absoluteString
        #expect(url.contains("/beta/identityGovernance/privilegedAccess/appRole/assignmentScheduleRequests/filterByCurrentUser(on='approver')"))
    }

    @Test func decideReadsTheStagesAndPatchesTheOneInProgress() async throws {
        let http = StubHTTPClient()
        let p = AppRoleApprovalProvider(http: http, tokens: FakeTokenProvider())
        await http.on("GET", "assignmentApprovals/appr-9", body: Data(#"{"id":"appr-9","stages":[{"id":"s1","status":"Completed"},{"id":"s2","status":"InProgress","assignedToMe":true}]}"#.utf8))
        await http.on("PATCH", "stages/s2", status: 204, body: Data())
        let request = ApprovalRequest(id: "req-9", tenantKey: tenant.id, kind: .appRole, action: .activate, targetName: "Security Manager",
                                      requesterName: "Sam Chen", decisionRef: "appr-9")
        try await p.decide(request, approve: true, justification: "ok", identity: identity)
        let get = await http.requests.first { $0.method == "GET" }!
        #expect(get.url.absoluteString.hasSuffix("/beta/identityGovernance/privilegedAccess/appRole/assignmentApprovals/appr-9?$expand=stages"))
        let patch = await http.requests.first { $0.method == "PATCH" }!
        #expect(patch.url.absoluteString.hasSuffix("/assignmentApprovals/appr-9/stages/s2"))
        let body = try JSONSerialization.jsonObject(with: patch.body!) as! [String: Any]
        #expect(body["reviewResult"] as? String == "Approve" && body["justification"] as? String == "ok")
    }
}
