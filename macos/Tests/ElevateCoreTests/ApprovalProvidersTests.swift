import Testing
import Foundation
@testable import ElevateCore

@Suite struct EntraApprovalProviderTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t-home")
    let tenant = TenantContext(identityId: "id1", tenantId: "t1", displayName: "Contoso", source: .home)

    func makeProvider() -> (EntraApprovalProvider, StubHTTPClient) {
        let http = StubHTTPClient()
        return (EntraApprovalProvider(http: http, tokens: FakeTokenProvider()), http)
    }

    func request(_ id: String, decisionRef: String? = nil) -> ApprovalRequest {
        ApprovalRequest(id: id, tenantKey: TenantKey(identityId: "id1", tenantId: "t1"), kind: .entraDirectory,
                        action: .activate, targetName: "Global Administrator", requesterName: "Ann",
                        decisionRef: decisionRef ?? id)
    }

    @Test func listsPendingApprovalsWithActionTargetAndRequester() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "approver", body: Fixtures.data("entra-approver-requests"))
        let items = try await p.pendingApprovals(identity: identity, tenant: tenant)
        #expect(items.map(\.id) == ["areq-1", "areq-2"])
        #expect(items.map(\.action) == [.activate, .extend])
        #expect(items.map(\.targetName) == ["Global Administrator", "def-role-2"])
        #expect(items.map(\.requesterName) == ["Ann Approver", "user-obj-2"])
        #expect(items[0].requestedDuration == .seconds(4 * 3600))
        #expect(items[0].justification == "Incident 4711")
        #expect(items[0].createdAt == GraphJSON.parseDate("2026-09-05T08:00:00Z"))
        #expect(items[0].decisionRef == "areq-1")
        #expect(items.allSatisfy { $0.kind == .entraDirectory && $0.tenantKey == tenant.id })
        let url = await http.requests.first!.url.absoluteString.removingPercentEncoding!
        #expect(url.contains("/v1.0/roleManagement/directory/roleAssignmentScheduleRequests/filterByCurrentUser(on='approver')"))
        #expect(url.contains("status eq 'PendingApproval'"))
        #expect(url.contains("$expand=roleDefinition,principal"))
    }

    @Test func listForbiddenThrows() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "approver", status: 403, body: Data(#"{"error":{"code":"Authorization_RequestDenied","message":"nope"}}"#.utf8))
        await #expect(throws: PIMError.consentRequired) {
            try await p.pendingApprovals(identity: identity, tenant: tenant)
        }
    }

    @Test func decideApprovesTheInProgressStepOnBeta() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "/steps", body: Fixtures.data("entra-approval-steps"))
        await http.on("PATCH", "steps/step-1", status: 204)
        try await p.decide(request("areq-1"), approve: true, justification: "ok", identity: identity)
        let get = await http.requests.first!
        #expect(get.url.absoluteString == "https://graph.microsoft.com/beta/roleManagement/directory/roleAssignmentApprovals/areq-1/steps")
        let patch = await http.requests.last!
        #expect(patch.method == "PATCH")
        #expect(patch.url.absoluteString == "https://graph.microsoft.com/beta/roleManagement/directory/roleAssignmentApprovals/areq-1/steps/step-1")
        let body = try JSONSerialization.jsonObject(with: patch.body!) as! [String: String]
        #expect(body == ["reviewResult": "Approve", "justification": "ok"])
    }

    @Test func decideDeniesUsingTheDecisionRef() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "/steps", body: Fixtures.data("entra-approval-steps"))
        await http.on("PATCH", "steps/step-1", status: 204)
        try await p.decide(request("areq-1", decisionRef: "appr-9"), approve: false, justification: "no", identity: identity)
        let patch = await http.requests.last!
        #expect(patch.url.absoluteString.contains("roleAssignmentApprovals/appr-9/steps/step-1"))
        let body = try JSONSerialization.jsonObject(with: patch.body!) as! [String: String]
        #expect(body == ["reviewResult": "Deny", "justification": "no"])
    }

    @Test func decideWithNoStepsThrows() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "/steps", body: Data(#"{"value":[]}"#.utf8))
        await #expect(throws: PIMError.unexpected(status: 0, body: "No approval step to decide")) {
            try await p.decide(request("areq-1"), approve: true, justification: "ok", identity: identity)
        }
    }

    @Test func decideFallsBackToTheStepAssignedToMe() async throws {
        let (p, http) = makeProvider()
        let steps = #"{"value":[{"id":"step-0","status":"Completed","assignedToMe":false},{"id":"step-1","status":"NotStarted","assignedToMe":true}]}"#
        await http.on("GET", "/steps", body: Data(steps.utf8))
        await http.on("PATCH", "steps/step-1", status: 204)
        try await p.decide(request("areq-1"), approve: true, justification: "ok", identity: identity)
        #expect(await http.requests.last!.url.absoluteString.hasSuffix("/steps/step-1"))
    }
}

@Suite struct GroupApprovalProviderTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t-home")
    let tenant = TenantContext(identityId: "id1", tenantId: "t1", displayName: "Contoso", source: .home)

    func makeProvider() -> (GroupApprovalProvider, StubHTTPClient) {
        let http = StubHTTPClient()
        return (GroupApprovalProvider(http: http, tokens: FakeTokenProvider()), http)
    }

    func request(_ id: String) -> ApprovalRequest {
        ApprovalRequest(id: id, tenantKey: TenantKey(identityId: "id1", tenantId: "t1"), kind: .group,
                        action: .activate, targetName: "Ops Admins", scopeCaption: "member",
                        requesterName: "Ann", decisionRef: id)
    }

    @Test func listsPendingApprovalsWithAccessCaption() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "approver", body: Fixtures.data("group-approver-requests"))
        let items = try await p.pendingApprovals(identity: identity, tenant: tenant)
        #expect(items.map(\.id) == ["gareq-1", "gareq-2"])
        #expect(items.map(\.action) == [.activate, .extend])
        #expect(items.map(\.targetName) == ["Ops Admins", "grp-sec"])
        #expect(items.map(\.requesterName) == ["Ann Approver", "user-obj-2"])
        #expect(items.map(\.scopeCaption) == ["member", "owner"])
        #expect(items[0].requestedDuration == .seconds(4 * 3600))
        #expect(items[0].decisionRef == "gareq-1")
        #expect(items.allSatisfy { $0.kind == .group && $0.tenantKey == tenant.id })
        let url = await http.requests.first!.url.absoluteString.removingPercentEncoding!
        #expect(url.contains("/v1.0/identityGovernance/privilegedAccess/group/assignmentScheduleRequests/filterByCurrentUser(on='approver')"))
        #expect(url.contains("status eq 'PendingApproval'"))
        #expect(url.contains("$expand=group,principal"))
    }

    @Test func listForbiddenThrows() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "approver", status: 403, body: Data(#"{"error":{"code":"Authorization_RequestDenied","message":"nope"}}"#.utf8))
        await #expect(throws: PIMError.consentRequired) {
            try await p.pendingApprovals(identity: identity, tenant: tenant)
        }
    }

    @Test func decidePatchesTheStepOnBeta() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "/steps", body: Fixtures.data("group-approval-steps"))
        await http.on("PATCH", "steps/step-1", status: 204)
        try await p.decide(request("gareq-1"), approve: false, justification: "no", identity: identity)
        let get = await http.requests.first!
        #expect(get.url.absoluteString == "https://graph.microsoft.com/beta/identityGovernance/privilegedAccess/group/assignmentApprovals/gareq-1/steps")
        let patch = await http.requests.last!
        #expect(patch.method == "PATCH")
        #expect(patch.url.absoluteString.hasSuffix("/assignmentApprovals/gareq-1/steps/step-1"))
        let body = try JSONSerialization.jsonObject(with: patch.body!) as! [String: String]
        #expect(body == ["reviewResult": "Deny", "justification": "no"])
    }
}
