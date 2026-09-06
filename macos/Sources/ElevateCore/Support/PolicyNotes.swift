import Foundation

/// Short labels describing what a role's activation policy will ask of the user, in the order
/// they matter at activation time: approval first (the outcome changes), then step-up prompts.
public enum PolicyNotes {
    public static let approval = "approval"
    public static let mfa = "MFA"
    public static let conditionalAccess = "Conditional Access"

    public static func labels(for policy: RolePolicy) -> [String] {
        var out: [String] = []
        if policy.requiresApproval { out.append(approval) }
        if policy.requiresMFA { out.append(mfa) }
        if policy.authenticationContext != nil { out.append(conditionalAccess) }
        return out
    }

    /// One-line caption for a row, or nil when the policy asks nothing extra.
    public static func caption(for policy: RolePolicy) -> String? {
        let l = labels(for: policy)
        return l.isEmpty ? nil : l.joined(separator: " · ")
    }

    /// Tooltip explaining each label.
    public static func explanation(for policy: RolePolicy) -> String? {
        var lines: [String] = []
        if policy.requiresApproval { lines.append("An approver must accept the request before the role becomes active.") }
        if policy.requiresMFA { lines.append("Multi-factor authentication is required; you may be asked to sign in again.") }
        if let ctx = policy.authenticationContext {
            lines.append("A Conditional Access policy is attached (authentication context \(ctx)); Entra may require a step-up sign-in on activation.")
        }
        return lines.isEmpty ? nil : lines.joined(separator: "\n")
    }

    /// The verb for the primary action: a request that waits for someone else is not an activation.
    public static func actionTitle(for policy: RolePolicy) -> String {
        policy.requiresApproval ? "Request" : "Activate"
    }
}
