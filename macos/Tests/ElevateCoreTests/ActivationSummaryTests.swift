import Testing
import Foundation
@testable import ElevateCore

@Suite struct ActivationSummaryTests {
    let now = Date(timeIntervalSince1970: 1_000_000)

    func key(_ n: String) -> RoleKey { RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: n, directoryScopeId: "/")) }
    func assignment(_ n: String, _ status: ActiveAssignment.Status, start: TimeInterval = 0, end: TimeInterval? = nil) -> ActiveAssignment {
        ActiveAssignment(roleKey: key(n), assignmentId: n, startDateTime: now.addingTimeInterval(start),
                         endDateTime: end.map { now.addingTimeInterval($0) }, status: status)
    }
    func outcome(_ n: String, _ result: ActivationOutcome.Result) -> ActivationOutcome {
        ActivationOutcome(roleKey: key(n), result: result)
    }
    func body(_ outcomes: [ActivationOutcome], attempted: Int) -> String {
        ActivationSummary.body(outcomes: outcomes, attempted: attempted, names: { $0.scopeName }, now: now)
    }

    @Test func nothingAttemptedReadsNothingToDo() {
        #expect(body([], attempted: 0) == "Nothing to do")
    }

    @Test func attemptedWithNoOutcomesReadsNotCompleted() {
        #expect(body([], attempted: 3) == "Not completed; open Elevate for details")
    }

    @Test func singleActivatedWithEndShowsDuration() {
        let o = outcome("a", .activated(assignment("a", .active, end: 7200)))
        #expect(body([o], attempted: 1) == "Active for 02:00")
    }

    @Test func singleActivatedWithoutEndIsJustActive() {
        let o = outcome("a", .activated(assignment("a", .active)))
        #expect(body([o], attempted: 1) == "Active")
    }

    @Test func singleScheduledCountsDownToStart() {
        let o = outcome("a", .scheduled(assignment("a", .scheduled, start: 3 * 3600 + 30 * 60)))
        #expect(body([o], attempted: 1) == "Scheduled to start in 3 h 30 m")
    }

    @Test func singlePendingApprovalReadsAwaitingApproval() {
        let o = outcome("a", .pendingApproval(assignment("a", .pendingApproval)))
        #expect(body([o], attempted: 1) == "Awaiting approval")
    }

    @Test func singleFailureShowsTheUserMessage() {
        let o = outcome("a", .failed(.notEligible))
        #expect(body([o], attempted: 1) == "Failed: Not eligible for this role")
    }

    @Test func severalOutcomesAreCountedWithPlurals() {
        let outcomes = [
            outcome("a", .activated(assignment("a", .active, end: 3600))),
            outcome("b", .activated(assignment("b", .active, end: 3600))),
            outcome("c", .scheduled(assignment("c", .scheduled, start: 600))),
            outcome("d", .pendingApproval(assignment("d", .pendingApproval))),
            outcome("e", .failed(.notEligible)),
        ]
        #expect(body(outcomes, attempted: 5)
            == "2 roles activated, 1 scheduled, 1 awaiting approval, 1 failed: e: Not eligible for this role")
    }

    @Test func severalFailuresCollapseToTheFirstPlusACount() {
        let outcomes = [
            outcome("a", .failed(.notEligible)),
            outcome("b", .failed(.pendingApproval)),
            outcome("c", .failed(.consentRequired)),
        ]
        #expect(body(outcomes, attempted: 3) == "3 roles failed: a: Not eligible for this role and 2 more")
    }

    @Test func aSingleRoleIsSingularWhenItLeadsTheCounts() {
        let outcomes = [
            outcome("a", .activated(assignment("a", .active, end: 3600))),
            outcome("b", .scheduled(assignment("b", .scheduled, start: 600))),
        ]
        #expect(body(outcomes, attempted: 2) == "1 role activated, 1 scheduled")
    }
}

private extension RoleKey {
    /// The role definition id, used as a stand-in display name in these tests.
    var scopeName: String {
        if case .entraDirectory(let id, _) = scope { return id }
        return "?"
    }
}
