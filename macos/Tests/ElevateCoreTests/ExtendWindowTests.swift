import Testing
import Foundation
@testable import ElevateCore

@Suite struct ExtendWindowTests {
    let now = Date(timeIntervalSince1970: 1_000_000)
    func a(_ status: ActiveAssignment.Status, end: TimeInterval?) -> ActiveAssignment {
        ActiveAssignment(roleKey: RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/")),
                         assignmentId: "x", startDateTime: now.addingTimeInterval(-3600), endDateTime: end.map { now.addingTimeInterval($0) }, status: status)
    }

    @Test func offeredOnlyInsideTheWindowWhileActive() {
        #expect(ExtendWindow.canExtend(a(.active, end: 900), policy: .manualDefault, now: now))
        #expect(ExtendWindow.canExtend(a(.active, end: 1), policy: .manualDefault, now: now))
        #expect(!ExtendWindow.canExtend(a(.active, end: 901), policy: .manualDefault, now: now))
        #expect(!ExtendWindow.canExtend(a(.active, end: 0), policy: .manualDefault, now: now))
        #expect(!ExtendWindow.canExtend(a(.active, end: nil), policy: .manualDefault, now: now))
        #expect(!ExtendWindow.canExtend(a(.pendingApproval, end: 100), policy: .manualDefault, now: now))
    }

    @Test func neverOfferedWhenTheRoleNeedsApproval() {
        var policy = RolePolicy.manualDefault
        policy.requiresApproval = true
        #expect(!ExtendWindow.canExtend(a(.active, end: 300), policy: policy, now: now))
    }
}
