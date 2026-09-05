import Testing
import Foundation
@testable import ElevateCore

@Suite struct ApprovalDiffTests {
    let tenant = TenantKey(identityId: "id1", tenantId: "t1")

    func request(_ id: String) -> ApprovalRequest {
        ApprovalRequest(id: id, tenantKey: tenant, kind: .entraDirectory, action: .activate,
                        targetName: "Reader", requesterName: "Ann")
    }

    @Test func returnsUnseenRequestsInInputOrder() {
        let current = [request("c"), request("a"), request("b")]
        let new = ApprovalDiff.newRequests(previousIds: ["a"], current: current)
        #expect(new.map(\.id) == ["c", "b"])
    }

    @Test func excludesEverythingAlreadySeen() {
        let current = [request("a"), request("b")]
        #expect(ApprovalDiff.newRequests(previousIds: ["a", "b"], current: current).isEmpty)
    }

    @Test func emptyInputs() {
        #expect(ApprovalDiff.newRequests(previousIds: [], current: []).isEmpty)
        #expect(ApprovalDiff.newRequests(previousIds: ["x"], current: []).isEmpty)
        #expect(ApprovalDiff.newRequests(previousIds: [], current: [request("a")]).map(\.id) == ["a"])
    }

    @Test func modelDefaultsAndRoundTrip() throws {
        let r = ApprovalRequest(id: "r1", tenantKey: tenant, kind: .group, action: .extend,
                               targetName: "Ops Admins", scopeCaption: "member", requesterName: "Bo",
                               justification: "need it", requestedDuration: .seconds(3600),
                               createdAt: GraphJSON.parseDate("2026-09-04T09:00:00Z"), decisionRef: "appr-1")
        let data = try GraphJSON.encoder.encode(r)
        #expect(try GraphJSON.decoder.decode(ApprovalRequest.self, from: data) == r)
        let bare = ApprovalRequest(id: "r2", tenantKey: tenant, kind: .azureResource, action: .other,
                                   targetName: "Contributor", requesterName: "Cy")
        #expect(bare.scopeCaption == nil && bare.justification == nil && bare.requestedDuration == nil
                && bare.createdAt == nil && bare.decisionRef == nil)
    }
}
