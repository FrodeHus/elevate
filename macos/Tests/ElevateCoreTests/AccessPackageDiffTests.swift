import Testing
import Foundation
@testable import ElevateCore

@Suite struct AccessPackageDiffTests {
    let now = GraphJSON.parseDate("2026-09-08T12:00:00Z")!

    func request(_ id: String, _ state: AccessPackageRequestState) -> AccessPackageRequest {
        AccessPackageRequest(id: id, packageId: "p-\(id)", packageName: "Package \(id)", requestType: "userAdd", state: state)
    }
    func assignment(_ id: String, _ state: AccessPackageAssignmentState = .delivered, expires: String? = nil) -> AccessPackageAssignment {
        AccessPackageAssignment(id: id, packageId: "p-\(id)", packageName: "Package \(id)", state: state,
                                expiresAt: expires.flatMap(GraphJSON.parseDate))
    }

    @Test func nilPreviousIsBaselineAndYieldsNothing() {
        let current = AccessPackageSnapshot(requests: [request("r", .delivered)], assignments: [assignment("a")])
        #expect(AccessPackageDiff.events(previous: nil, current: current, now: now).isEmpty)
    }

    @Test func unchangedYieldsNothing() {
        let s = AccessPackageSnapshot(requests: [request("r", .pendingApproval)], assignments: [assignment("a")])
        #expect(AccessPackageDiff.events(previous: s, current: s, now: now).isEmpty)
    }

    @Test func requestTransitionsProduceEvents() {
        let before = AccessPackageSnapshot(requests: [request("a", .pendingApproval), request("b", .submitted), request("c", .delivering), request("d", .pendingApproval)])
        let after = AccessPackageSnapshot(requests: [request("a", .delivered), request("b", .denied), request("c", .deliveryFailed), request("d", .canceled)])
        let events = AccessPackageDiff.events(previous: before, current: after, now: now)
        #expect(events == [.approved(request("a", .delivered)), .denied(request("b", .denied)), .deliveryFailed(request("c", .deliveryFailed))])
    }

    @Test func aRequestFirstSeenAlreadyDeliveredDoesNotNotify() {
        let before = AccessPackageSnapshot(requests: [])
        let after = AccessPackageSnapshot(requests: [request("a", .delivered)])
        #expect(AccessPackageDiff.events(previous: before, current: after, now: now).isEmpty)
    }

    @Test func revokedBeforeExpiryAndExpiredAtExpiry() {
        let before = AccessPackageSnapshot(assignments: [assignment("keep", expires: "2027-01-01T00:00:00Z"),
                                                         assignment("gone-early", expires: "2027-01-01T00:00:00Z"),
                                                         assignment("gone-late", expires: "2026-09-08T11:00:00Z"),
                                                         assignment("marked", expires: "2026-09-01T00:00:00Z"),
                                                         assignment("open-ended")])
        let after = AccessPackageSnapshot(assignments: [assignment("keep", expires: "2027-01-01T00:00:00Z"),
                                                        assignment("marked", .expired, expires: "2026-09-01T00:00:00Z")])
        let events = AccessPackageDiff.events(previous: before, current: after, now: now)
        #expect(events.contains(.revoked(before.assignments[1])))
        #expect(events.contains(.expired(before.assignments[2])))
        #expect(events.contains(.expired(before.assignments[3])))
        #expect(events.contains(.revoked(before.assignments[4])))
        #expect(events.count == 4)
    }

    @Test func eventsCarryTheirPackageName() {
        let e = AccessPackageEvent.approved(request("x", .delivered))
        #expect(e.packageName == "Package x")
        #expect(AccessPackageEvent.expired(assignment("y")).packageName == "Package y")
    }
}
