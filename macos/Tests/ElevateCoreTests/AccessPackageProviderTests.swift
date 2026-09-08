import Testing
import Foundation
@testable import ElevateCore

@Suite struct AccessPackageProviderTests {
    let identity = Identity(id: "id1", upn: "alex.rivera@contoso.com", displayName: "Alex", homeTenantId: "t1")

    func makeProvider() -> (AccessPackageProvider, StubHTTPClient) {
        let http = StubHTTPClient()
        return (AccessPackageProvider(http: http, tokens: FakeTokenProvider()), http)
    }

    @Test func listsRequestablePackagesAcrossPages() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "accessPackages/filterByCurrentUser", body: Fixtures.data("ap-packages"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("ap-packages-page2"))
        let packages = try await p.requestablePackages(identity: identity, tenantId: "t1")
        #expect(packages.map(\.id) == ["pkg-finance", "pkg-exchange", "pkg-sandbox", "pkg-hidden"])
        #expect(packages[3].isHidden)
        #expect(packages[3].description == nil)
        let first = await http.requests.first!
        #expect(first.headers["Authorization"] == "Bearer token-t1")
        #expect(first.url.absoluteString.contains("identityGovernance/entitlementManagement/accessPackages/filterByCurrentUser(on='allowedRequestor')"))
    }

    @Test func listsMyRequestsWithPackageNamesAndStates() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "assignmentRequests/filterByCurrentUser", body: Fixtures.data("ap-requests"))
        let requests = try await p.myRequests(identity: identity, tenantId: "t1")
        #expect(requests.map(\.id) == ["req-1", "req-2", "req-3", "req-4"])
        #expect(requests[0].state == .pendingApproval)
        #expect(requests[0].packageName == "Azure Sandbox Contributor")
        #expect(requests[0].packageId == "pkg-sandbox")
        #expect(requests[0].policyId == "pol-eng")
        #expect(requests[0].justification == "Need a sandbox for the cost-alerting spike (INC-4412).")
        #expect(requests[1].state == .delivered)
        #expect(requests[1].completedAt == GraphJSON.parseDate("2026-09-07T14:40:00Z"))
        #expect(requests[2].state == .denied)
        // Unknown state and a missing package still decode; the name falls back to the id.
        #expect(requests[3].state == .unknown)
        #expect(requests[3].packageName == "req-4")
        #expect(requests[3].packageId == "")
        let url = await http.requests.first!.url.absoluteString
        #expect(url.contains("assignmentRequests/filterByCurrentUser(on='target')"))
        #expect(url.contains("expand=accessPackage,assignment"))
    }

    @Test func listsMyAssignmentsWithExpiryAndPolicy() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "assignments/filterByCurrentUser", body: Fixtures.data("ap-assignments"))
        let assignments = try await p.myAssignments(identity: identity, tenantId: "t1")
        #expect(assignments.map(\.id) == ["asg-1", "asg-2", "asg-3"])
        #expect(assignments[0].state == .delivered)
        #expect(assignments[0].expiresAt == GraphJSON.parseDate("2027-03-07T14:40:00Z"))
        #expect(assignments[0].policyName == "On-call staff")
        #expect(assignments[1].expiresAt == nil)
        #expect(assignments[2].state == .expired)
        #expect(assignments[2].policyName == nil)
        let url = await http.requests.first!.url.absoluteString
        #expect(url.contains("assignments/filterByCurrentUser(on='target')"))
        #expect(url.contains("expand=accessPackage,assignmentPolicy"))
    }

    @Test func forbiddenMapsToConsentRequiredForOwnApp() async {
        let (p, http) = makeProvider()
        await http.on("GET", "accessPackages/filterByCurrentUser", status: 403, body: Data(#"{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}"#.utf8))
        await #expect(throws: PIMError.consentRequired) {
            _ = try await p.requestablePackages(identity: identity, tenantId: "t1")
        }
    }

    @Test func requirementsWithOnePolicy() async throws {
        let (p, http) = makeProvider()
        await http.on("POST", "getApplicablePolicyRequirements", body: Fixtures.data("ap-requirements-one"))
        let reqs = try await p.requirements(packageId: "pkg-sandbox", identity: identity, tenantId: "t1")
        #expect(reqs.count == 1)
        #expect(reqs[0].id == "pol-eng")
        #expect(reqs[0].displayName == "Engineers")
        #expect(reqs[0].description == "30 days, approval by the platform team.")
        #expect(reqs[0].isApprovalRequired)
        #expect(!reqs[0].requiresAnswers)
        let sent = await http.requests.first!
        #expect(sent.method == "POST")
        #expect(sent.url.absoluteString.hasSuffix("/entitlementManagement/accessPackages/pkg-sandbox/getApplicablePolicyRequirements"))
    }

    @Test func requirementsWithTwoPoliciesAndQuestions() async throws {
        let (p, http) = makeProvider()
        await http.on("POST", "pkg-two/getApplicablePolicyRequirements", body: Fixtures.data("ap-requirements-two"))
        await http.on("POST", "pkg-q/getApplicablePolicyRequirements", body: Fixtures.data("ap-requirements-questions"))
        let two = try await p.requirements(packageId: "pkg-two", identity: identity, tenantId: "t1")
        #expect(two.map(\.id) == ["pol-eng", "pol-lead"])
        #expect(two[1].isApprovalRequired == false)
        let q = try await p.requirements(packageId: "pkg-q", identity: identity, tenantId: "t1")
        #expect(q.count == 1 && q[0].requiresAnswers)
    }

    @Test func requestPostsUserAddBodyWithOptionalPolicy() async throws {
        let (p, http) = makeProvider()
        await http.on("POST", "assignmentRequests", status: 201, body: Fixtures.data("ap-request-created"))
        let created = try await p.request(packageId: "pkg-sandbox", policyId: "pol-eng", justification: "Need it", identity: identity, tenantId: "t1")
        #expect(created.id == "req-new")
        #expect(created.state == .submitted)
        #expect(created.packageId == "pkg-sandbox")
        #expect(created.policyId == "pol-eng")
        let body = try JSONSerialization.jsonObject(with: await http.requests.first!.body!) as! [String: Any]
        #expect(body["requestType"] as? String == "userAdd")
        #expect(body["justification"] as? String == "Need it")
        let assignment = body["assignment"] as! [String: Any]
        #expect(assignment["accessPackageId"] as? String == "pkg-sandbox")
        #expect(assignment["assignmentPolicyId"] as? String == "pol-eng")

        _ = try await p.request(packageId: "pkg-sandbox", policyId: nil, justification: "Again", identity: identity, tenantId: "t1")
        let second = try JSONSerialization.jsonObject(with: await http.requests.last!.body!) as! [String: Any]
        let secondAssignment = second["assignment"] as! [String: Any]
        #expect(secondAssignment["assignmentPolicyId"] == nil)
    }

    @Test func cancelPostsToTheCancelAction() async throws {
        let (p, http) = makeProvider()
        await http.on("POST", "assignmentRequests/req-1/cancel", status: 204)
        try await p.cancel(requestId: "req-1", identity: identity, tenantId: "t1")
        let sent = await http.requests.first!
        #expect(sent.method == "POST")
        #expect(sent.url.absoluteString.hasSuffix("/assignmentRequests/req-1/cancel"))
    }

    @Test func myAccessURLPointsAtThePackageInTheTenant() {
        let url = AccessPackageProvider.myAccessURL(tenantId: "11111111-2222-3333-4444-555555555555", packageId: "pkg-sandbox")
        #expect(url.absoluteString == "https://myaccess.microsoft.com/@11111111-2222-3333-4444-555555555555#/access-packages/pkg-sandbox")
    }
}
