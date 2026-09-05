import Foundation

/// Decides whether an activation can go ahead without the dialog, and builds the requests when it can.
public enum QuickActivate {
    public enum Decision: Equatable, Sendable { case ready([ActivationRequest]), needsDialog(String) }

    public static func decide(role: EligibleRole, memory: RoleMemory?) -> Decision {
        let p = role.policy
        if p.requiresTicket { return .needsDialog("ticket required") }
        if p.requiresApproval { return .needsDialog("approval required") }
        let reason = memory?.justification.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if p.requiresJustification && reason.isEmpty { return .needsDialog("no remembered reason") }
        let duration = min(memory?.lastDuration ?? p.defaultDuration, p.maximumDuration)
        return .ready([ActivationRequest(roleKey: role.key, duration: duration, justification: reason, ticket: nil,
                                         authenticationContext: p.authenticationContext)])
    }

    public static func decide(items: [ProfilePlanItem], justification: String?) -> Decision {
        if items.contains(where: { $0.disposition == .notLoaded }) { return .needsDialog("roles still loading") }
        let toRun = items.filter { $0.disposition == .activate }
        if toRun.isEmpty { return .needsDialog("nothing to activate") }
        if toRun.contains(where: { $0.role?.policy.requiresTicket == true }) { return .needsDialog("ticket required") }
        let reason = justification?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if reason.isEmpty && toRun.contains(where: { ($0.role?.policy.requiresJustification ?? true) }) { return .needsDialog("no remembered reason") }
        return .ready(toRun.map { ActivationRequest(roleKey: $0.roleKey, duration: $0.duration, justification: reason, ticket: nil,
                                                    authenticationContext: $0.role?.policy.authenticationContext) })
    }
}
