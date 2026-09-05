import Testing
import Foundation
@testable import ElevateCore

@Suite struct AzureApprovalProviderTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t-home")
    let tenant = TenantContext(identityId: "id1", tenantId: "t1", displayName: "Contoso", source: .home)

    func makeProvider() -> (AzureApprovalProvider, StubHTTPClient) {
        let http = StubHTTPClient()
        return (AzureApprovalProvider(http: http, tokens: FakeTokenProvider()), http)
    }

    func request(_ decisionRef: String) -> ApprovalRequest {
        ApprovalRequest(id: "areq-1", tenantKey: TenantKey(identityId: "id1", tenantId: "t1"), kind: .azureResource,
                        action: .activate, targetName: "Reader", scopeCaption: "rg-ops · resource group",
                        requesterName: "Ann", decisionRef: decisionRef)
    }

    @Test func listsOnlyPendingApprovalRequests() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "asApprover", body: Fixtures.data("arm-approver-requests"))
        let items = try await p.pendingApprovals(identity: identity, tenant: tenant)
        #expect(items.map(\.id) == ["areq-1", "areq-2"])
        #expect(items.map(\.action) == [.activate, .extend])
        #expect(items.map(\.requesterName) == ["Ann Approver", "user-obj-2"])
        #expect(items.map(\.scopeCaption) == ["rg-ops · resource group", "Contoso Production · subscription"])
        #expect(items.map(\.decisionRef) == ["appr-1", "appr-2"])
        #expect(items[0].targetName == "Reader")
        #expect(items[1].targetName == "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c")
        #expect(items[0].requestedDuration == .seconds(4 * 3600))
        #expect(items[0].justification == "Incident 4711")
        #expect(items[0].createdAt == GraphJSON.parseDate("2026-09-05T08:00:00Z"))
        #expect(items.allSatisfy { $0.kind == .azureResource && $0.tenantKey == tenant.id })
        let url = (await http.requests.first!.url.absoluteString).removingPercentEncoding!
        #expect(url.contains("providers/Microsoft.Authorization/roleAssignmentScheduleRequests"))
        #expect(url.contains("$filter=asApprover()"))
        #expect(url.contains("api-version=2020-10-01"))
    }

    @Test func listForbiddenThrows() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "asApprover", status: 403, body: Data(#"{"error":{"code":"AuthorizationFailed","message":"nope"}}"#.utf8))
        await #expect(throws: PIMError.policyViolation("Not permitted at this scope")) {
            try await p.pendingApprovals(identity: identity, tenant: tenant)
        }
    }

    @Test func decidePatchesTheStageAwaitingReview() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "roleAssignmentApprovals/appr-1?", body: Fixtures.data("arm-approval"))
        await http.on("PATCH", "stages/stage-1", status: 200)
        try await p.decide(request("appr-1"), approve: true, justification: "ok", identity: identity)
        let get = await http.requests.first!
        let getURL = get.url.absoluteString
        #expect(getURL.contains("https://management.azure.com/providers/Microsoft.Authorization/roleAssignmentApprovals/appr-1?"))
        #expect(getURL.contains("api-version=2021-01-01-preview"))
        let patch = await http.requests.last!
        #expect(patch.method == "PATCH")
        #expect(patch.url.absoluteString.contains("roleAssignmentApprovals/appr-1/stages/stage-1?"))
        #expect(patch.url.absoluteString.contains("api-version=2021-01-01-preview"))
        let body = try JSONSerialization.jsonObject(with: patch.body!) as! [String: String]
        #expect(body == ["reviewResult": "Approve", "justification": "ok"])
    }

    @Test func decideRetriesWithThePropertiesWrappedBodyOnA400() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "roleAssignmentApprovals/appr-1?", body: Fixtures.data("arm-approval"))
        let attempts = Counter()
        await http.on("PATCH", "stages/stage-1") { _ in
            HTTPResponse(status: attempts.next() == 0 ? 400 : 200, body: Data(#"{"error":{"code":"BadRequest","message":"bad body"}}"#.utf8))
        }
        try await p.decide(request("appr-1"), approve: false, justification: "no", identity: identity)
        let patches = await http.requests.filter { $0.method == "PATCH" }
        #expect(patches.count == 2)
        let first = try JSONSerialization.jsonObject(with: patches[0].body!) as! [String: Any]
        #expect(first["reviewResult"] as? String == "Deny")
        let second = try JSONSerialization.jsonObject(with: patches[1].body!) as! [String: Any]
        let props = try #require(second["properties"] as? [String: String])
        #expect(props == ["reviewResult": "Deny", "justification": "no"])
    }

    @Test func decideSurfacesTheErrorWhenTheRetryAlsoFails() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "roleAssignmentApprovals/appr-1?", body: Fixtures.data("arm-approval"))
        await http.on("PATCH", "stages/stage-1", status: 400, body: Data(#"{"error":{"code":"BadRequest","message":"bad body"}}"#.utf8))
        await #expect(throws: PIMError.unexpected(status: 400, body: "bad body")) {
            try await p.decide(request("appr-1"), approve: true, justification: "ok", identity: identity)
        }
        #expect(await http.requests.filter { $0.method == "PATCH" }.count == 2)
    }

    @Test func decideWithNoStagesThrows() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "roleAssignmentApprovals/appr-1?", body: Data(#"{"properties":{"stages":[]}}"#.utf8))
        await #expect(throws: PIMError.unexpected(status: 0, body: "No approval step to decide")) {
            try await p.decide(request("appr-1"), approve: true, justification: "ok", identity: identity)
        }
    }

    @Test func decideFallsBackToTheFirstStage() async throws {
        let (p, http) = makeProvider()
        let approval = #"{"properties":{"stages":[{"name":"stage-1","properties":{"reviewResult":"Approve","status":"Completed"}}]}}"#
        await http.on("GET", "roleAssignmentApprovals/appr-1?", body: Data(approval.utf8))
        await http.on("PATCH", "stages/stage-1", status: 200)
        try await p.decide(request("appr-1"), approve: true, justification: "ok", identity: identity)
        #expect(await http.requests.last!.url.absoluteString.contains("stages/stage-1"))
    }
}

/// Counts calls from the stub's `@Sendable` responder.
final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    func next() -> Int {
        lock.lock(); defer { lock.unlock() }
        defer { count += 1 }
        return count
    }
}
