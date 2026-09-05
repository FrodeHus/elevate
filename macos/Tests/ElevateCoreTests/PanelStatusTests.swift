import Testing
import Foundation
@testable import ElevateCore

@Suite struct PanelStatusTests {
    let now = Date(timeIntervalSince1970: 1_000_000)
    func key(_ n: String) -> RoleKey { RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: n, directoryScopeId: "/")) }
    func assignment(_ n: String, status: ActiveAssignment.Status, endsIn: TimeInterval? = 3600) -> ActiveAssignment {
        ActiveAssignment(roleKey: key(n), assignmentId: n, startDateTime: now.addingTimeInterval(-600),
                         endDateTime: endsIn.map { now.addingTimeInterval($0) }, status: status)
    }

    @Test func emptyIsIdle() {
        #expect(PanelStatus.compute([], now: now) == PanelStatus(activeCount: 0, expiringSoon: false, pendingApproval: false))
    }

    @Test func countsActiveOnly() {
        let s = PanelStatus.compute([assignment("a", status: .active), assignment("b", status: .pendingApproval), assignment("c", status: .pendingProvisioning), assignment("d", status: .failed("x"))], now: now)
        #expect(s.activeCount == 1)
        #expect(s.pendingApproval)
        #expect(!s.expiringSoon)
    }

    @Test func expiringSoonBoundary() {
        #expect(PanelStatus.compute([assignment("a", status: .active, endsIn: 300)], now: now).expiringSoon)
        #expect(!PanelStatus.compute([assignment("a", status: .active, endsIn: 301)], now: now).expiringSoon)
        #expect(!PanelStatus.compute([assignment("a", status: .active, endsIn: -1)], now: now).expiringSoon)   // already past: not "soon"
        #expect(!PanelStatus.compute([assignment("a", status: .active, endsIn: nil)], now: now).expiringSoon)
        #expect(!PanelStatus.compute([assignment("a", status: .pendingApproval, endsIn: 10)], now: now).expiringSoon)
    }
}
