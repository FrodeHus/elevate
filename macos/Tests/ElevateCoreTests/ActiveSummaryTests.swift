import Testing
import Foundation
@testable import ElevateCore

@Suite struct ActiveSummaryTests {
    let now = Date(timeIntervalSince1970: 1_000_000)
    func key(_ n: String) -> RoleKey { RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: n, directoryScopeId: "/")) }
    func a(_ n: String, _ status: ActiveAssignment.Status, start: TimeInterval = 0, end: TimeInterval? = nil) -> ActiveAssignment {
        ActiveAssignment(roleKey: key(n), assignmentId: n, startDateTime: now.addingTimeInterval(start),
                         endDateTime: end.map { now.addingTimeInterval($0) }, status: status)
    }

    @Test func activeFirstBySoonestExpiryThenPendingByStart() {
        let input = [
            a("prov", .pendingProvisioning),
            a("late", .active, end: 7200),
            a("pend2", .pendingApproval, start: 20),
            a("noend", .active, end: nil),
            a("soon", .active, end: 600),
            a("pend1", .pendingApproval, start: 10),
            a("failed", .failed("x")),
        ]
        let ids = ActiveSummary.order(input).map(\.assignmentId)
        #expect(ids == ["soon", "late", "noend", "pend1", "pend2", "prov"])
    }

    @Test func scheduledSortsAfterActiveAndBeforePending() {
        let input = [
            a("pend", .pendingApproval, start: 5),
            a("sched2", .scheduled, start: 200),
            a("active", .active, end: 600),
            a("sched1", .scheduled, start: 100),
            a("prov", .pendingProvisioning, start: 7),
        ]
        let ids = ActiveSummary.order(input).map(\.assignmentId)
        #expect(ids == ["active", "sched1", "sched2", "pend", "prov"])
    }
}
