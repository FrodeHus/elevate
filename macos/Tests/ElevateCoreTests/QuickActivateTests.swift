import Testing
import Foundation
@testable import ElevateCore

@Suite struct QuickActivateTests {
    let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
    func role(justification: Bool = true, ticket: Bool = false, approval: Bool = false, mfa: Bool = true, max: Int = 4) -> EligibleRole {
        EligibleRole(key: key, displayName: "R", source: .discovered,
                     policy: RolePolicy(defaultDuration: .seconds(3600), maximumDuration: .seconds(max * 3600), requiresJustification: justification,
                                        requiresTicket: ticket, requiresMFA: mfa, requiresApproval: approval, authenticationContext: "c1"))
    }
    let memory = RoleMemory(roleKey: RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/")), justification: "INC-1", lastDuration: .seconds(8 * 3600))

    @Test func readyUsesRememberedReasonAndCappedDuration() {
        guard case .ready(let reqs) = QuickActivate.decide(role: role(), memory: memory) else { Issue.record("expected ready"); return }
        #expect(reqs.count == 1 && reqs[0].justification == "INC-1" && reqs[0].duration == .seconds(4 * 3600))
        #expect(reqs[0].authenticationContext == "c1" && reqs[0].startDateTime == nil && reqs[0].ticket == nil)
    }

    @Test func dialogReasons() {
        #expect(QuickActivate.decide(role: role(), memory: nil) == .needsDialog("no remembered reason"))
        #expect(QuickActivate.decide(role: role(justification: false), memory: nil) != .needsDialog("no remembered reason"))
        #expect(QuickActivate.decide(role: role(ticket: true), memory: memory) == .needsDialog("ticket required"))
        #expect(QuickActivate.decide(role: role(approval: true), memory: memory) == .needsDialog("approval required"))
    }

    @Test func profileDecision() {
        let r = role()
        let ok = ProfilePlanItem(roleKey: key, role: r, duration: .seconds(3600), disposition: .activate)
        let skipped = ProfilePlanItem(roleKey: key, role: r, duration: .seconds(3600), disposition: .alreadyActive)
        guard case .ready(let reqs) = QuickActivate.decide(items: [ok, skipped], justification: "INC-2") else { Issue.record("expected ready"); return }
        #expect(reqs.count == 1 && reqs[0].justification == "INC-2")
        #expect(QuickActivate.decide(items: [ok], justification: nil) == .needsDialog("no remembered reason"))
        #expect(QuickActivate.decide(items: [ProfilePlanItem(roleKey: key, role: nil, duration: .seconds(60), disposition: .notLoaded)], justification: "x") == .needsDialog("roles still loading"))
        #expect(QuickActivate.decide(items: [ProfilePlanItem(roleKey: key, role: role(ticket: true), duration: .seconds(60), disposition: .activate)], justification: "x") == .needsDialog("ticket required"))
        #expect(QuickActivate.decide(items: [skipped], justification: "x") == .needsDialog("nothing to activate"))
    }
}
