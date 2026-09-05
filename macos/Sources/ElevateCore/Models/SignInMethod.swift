import Foundation

/// How an account authenticates. First-party methods need no app registration or admin consent.
public enum SignInMethod: String, Codable, Hashable, Sendable, CaseIterable {
    case ownApp
    case azureCLI
    case azurePowerShell

    public var displayName: String {
        switch self {
        case .ownApp: "Own app registration"
        case .azureCLI: "Azure CLI app"
        case .azurePowerShell: "Azure PowerShell app"
        }
    }

    /// Fixed Microsoft client id, or nil when the user's own registration is used.
    public var clientId: String? {
        switch self {
        case .ownApp: nil
        case .azureCLI: "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
        case .azurePowerShell: "1950a258-227b-4e31-a9cf-717495945fc2"
        }
    }

    public var usesMSAL: Bool { self == .ownApp }

    /// Whether Microsoft pre-authorises this client for the Graph scope that activates Entra
    /// directory roles. Neither first-party app is: they can list PIM schedules but
    /// `RoleAssignmentSchedule.ReadWrite.Directory` is admin-consent only, so Entra roles are
    /// view-only with them unless an admin grants it to the enterprise app. Azure resource roles
    /// go through ARM (`user_impersonation`) and are unaffected.
    public var isPreauthorisedForEntraActivation: Bool { self == .ownApp }

    /// One-line statement of what the method cannot do, for the add-account dialog and headers.
    public var limitationSummary: String? {
        isPreauthorisedForEntraActivation ? nil
            : "Entra roles: view only. Azure resource roles: activate and deactivate."
    }

    /// Longer explanation shown on the Entra rows and headers of an account using this method.
    public var entraViewOnlyReason: String? {
        isPreauthorisedForEntraActivation ? nil
            : "The \(displayName) is not allowed to activate Entra roles (it lacks RoleAssignmentSchedule.ReadWrite.Directory). Azure resource roles still work. Add the account with your own app registration to activate Entra roles."
    }
}
