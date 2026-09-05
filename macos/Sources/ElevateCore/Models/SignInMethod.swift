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
}
