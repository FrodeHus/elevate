import Testing
import Foundation
@testable import ElevateCore

struct PolicyNotesTests {
    private func policy(approval: Bool = false, mfa: Bool = false, context: String? = nil) -> RolePolicy {
        RolePolicy(defaultDuration: .seconds(3600), maximumDuration: .seconds(28800), requiresJustification: true,
                   requiresTicket: false, requiresMFA: mfa, requiresApproval: approval, authenticationContext: context)
    }

    @Test func plainPolicyHasNoNotes() {
        #expect(PolicyNotes.labels(for: policy()).isEmpty)
        #expect(PolicyNotes.caption(for: policy()) == nil)
        #expect(PolicyNotes.explanation(for: policy()) == nil)
        #expect(PolicyNotes.actionTitle(for: policy()) == "Activate")
    }

    @Test func labelsAreOrderedApprovalThenStepUps() {
        let p = policy(approval: true, mfa: true, context: "c1")
        #expect(PolicyNotes.labels(for: p) == ["approval", "MFA", "Conditional Access"])
        #expect(PolicyNotes.caption(for: p) == "approval · MFA · Conditional Access")
        #expect(PolicyNotes.actionTitle(for: p) == "Request")
    }

    @Test func conditionalAccessExplanationNamesTheContext() {
        let text = PolicyNotes.explanation(for: policy(context: "c1"))
        #expect(text?.contains("authentication context c1") == true)
        #expect(text?.contains("approver") == false)
    }
}
