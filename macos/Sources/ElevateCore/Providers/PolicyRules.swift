import Foundation

/// The end-user rules of a PIM role management policy. Graph (Entra, groups) and ARM (Azure resources)
/// wrap them in different envelopes but the rules themselves — and how they map to a `RolePolicy` — are identical.
public enum PolicyRules {
    public struct Rule: Decodable {
        let id: String
        let isExpirationRequired: Bool?
        let maximumDuration: String?
        let enabledRules: [String]?
        let setting: ApprovalSetting?
        let isEnabled: Bool?
        let claimValue: String?
        public struct ApprovalSetting: Decodable { let isApprovalRequired: Bool? }
    }

    /// Folds the end-user rules onto `.manualDefault`; rules we do not understand are ignored.
    public static func apply(_ rules: [Rule]) -> RolePolicy {
        var policy = RolePolicy.manualDefault
        for rule in rules {
            switch rule.id {
            case "Expiration_EndUser_Assignment":
                if let d = rule.maximumDuration.flatMap(ISO8601Duration.parse) {
                    policy.maximumDuration = d
                    policy.defaultDuration = d
                }
            case "Enablement_EndUser_Assignment":
                let enabled = Set(rule.enabledRules ?? [])
                policy.requiresJustification = enabled.contains("Justification")
                policy.requiresTicket = enabled.contains("Ticketing")
                policy.requiresMFA = enabled.contains("MultiFactorAuthentication")
            case "Approval_EndUser_Assignment":
                policy.requiresApproval = rule.setting?.isApprovalRequired ?? false
            case "AuthenticationContext_EndUser_Assignment":
                if rule.isEnabled == true, let claim = rule.claimValue, !claim.isEmpty { policy.authenticationContext = claim }
            default:
                break
            }
        }
        return policy
    }
}
