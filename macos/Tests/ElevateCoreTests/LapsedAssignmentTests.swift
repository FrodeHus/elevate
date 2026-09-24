import Testing
import Foundation
@testable import ElevateCore

@Suite struct LapsedAssignmentTests {
    let now = Date(timeIntervalSince1970: 1_000_000)
    func assignment(_ status: ActiveAssignment.Status, endsIn: TimeInterval?) -> ActiveAssignment {
        ActiveAssignment(roleKey: RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/")),
                         assignmentId: "a", startDateTime: now.addingTimeInterval(-7200),
                         endDateTime: endsIn.map { now.addingTimeInterval($0) }, status: status)
    }

    @Test func activeLapsesOnceItsEndHasPassed() {
        #expect(assignment(.active, endsIn: -1).hasLapsed(at: now))
        #expect(assignment(.active, endsIn: 0).hasLapsed(at: now))
        #expect(!assignment(.active, endsIn: 1).hasLapsed(at: now))
    }

    @Test func scheduledLapsesOnceItsEndHasPassed() {
        #expect(assignment(.scheduled, endsIn: -60).hasLapsed(at: now))
        #expect(!assignment(.scheduled, endsIn: 60).hasLapsed(at: now))
    }

    @Test func withoutAnEndNothingLapses() {
        #expect(!assignment(.active, endsIn: nil).hasLapsed(at: now))
    }

    /// Requests and failures carry no activation window of their own; the service settles them.
    @Test func requestsAndFailuresNeverLapse() {
        #expect(!assignment(.pendingApproval, endsIn: -60).hasLapsed(at: now))
        #expect(!assignment(.pendingProvisioning, endsIn: -60).hasLapsed(at: now))
        #expect(!assignment(.failed("x"), endsIn: -60).hasLapsed(at: now))
    }
}
