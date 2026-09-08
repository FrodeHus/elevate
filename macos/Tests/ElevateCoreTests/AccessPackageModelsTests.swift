import Testing
import Foundation
@testable import ElevateCore

@Suite struct AccessPackageModelsTests {
    @Test func requestStateParsesCaseInsensitivelyAndFallsBackToUnknown() {
        #expect(AccessPackageRequestState.parse("PendingApproval") == .pendingApproval)
        #expect(AccessPackageRequestState.parse("delivered") == .delivered)
        #expect(AccessPackageRequestState.parse("partiallyDelivered") == .partiallyDelivered)
        #expect(AccessPackageRequestState.parse("somethingNew") == .unknown)
        #expect(AccessPackageRequestState.parse(nil) == .unknown)
    }

    @Test func requestStateGroupsIntoTabs() {
        #expect(AccessPackageRequestState.pendingApproval.isOpen)
        #expect(AccessPackageRequestState.scheduled.isOpen)
        #expect(!AccessPackageRequestState.delivered.isOpen)
        #expect(AccessPackageRequestState.denied.isDeclined)
        #expect(AccessPackageRequestState.canceled.isDeclined)
        #expect(AccessPackageRequestState.deliveryFailed.isDeclined)
        #expect(!AccessPackageRequestState.submitted.isDeclined)
        #expect(AccessPackageRequestState.submitted.isCancellable)
        #expect(AccessPackageRequestState.pendingApproval.isCancellable)
        #expect(!AccessPackageRequestState.delivering.isCancellable)
    }

    @Test func assignmentStateParses() {
        #expect(AccessPackageAssignmentState.parse("Delivered") == .delivered)
        #expect(AccessPackageAssignmentState.parse("expired") == .expired)
        #expect(AccessPackageAssignmentState.parse("nope") == .unknown)
    }

    @Test func modelsRoundTripThroughCodable() throws {
        let request = AccessPackageRequest(id: "r", packageId: "p", packageName: "Pkg", requestType: "userAdd",
                                           state: .denied, status: "Denied", justification: "why",
                                           createdAt: GraphJSON.parseDate("2026-09-02T09:00:00Z"),
                                           completedAt: GraphJSON.parseDate("2026-09-02T09:03:00Z"), policyId: "pol")
        let assignment = AccessPackageAssignment(id: "a", packageId: "p", packageName: "Pkg", state: .delivered,
                                                 policyName: "All", expiresAt: GraphJSON.parseDate("2027-01-01T00:00:00Z"))
        let snapshot = AccessPackageSnapshot(requests: [request], assignments: [assignment])
        let data = try GraphJSON.encoder.encode(snapshot)
        #expect(try GraphJSON.decoder.decode(AccessPackageSnapshot.self, from: data) == snapshot)
        let requirement = PolicyRequirement(id: "pol", displayName: "Engineers", description: nil, isApprovalRequired: true, requiresAnswers: false)
        #expect(try JSONDecoder().decode(PolicyRequirement.self, from: JSONEncoder().encode(requirement)) == requirement)
    }
}
