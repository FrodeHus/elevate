import Foundation

/// How an account authenticates. First-party methods need no app registration or admin consent;
/// `custom` is any other public-client registration (for example a company-wide PIM app that
/// knows nothing about macOS) used through the same loopback browser flow.
public enum SignInMethod: Hashable, Sendable {
    case ownApp
    case azureCLI
    case azurePowerShell
    case custom(clientId: String)

    /// The methods offered as fixed choices; `custom` needs a client id typed by the user.
    public static let builtIn: [SignInMethod] = [.ownApp, .azureCLI, .azurePowerShell]

    public var displayName: String {
        switch self {
        case .ownApp: "Own app registration"
        case .azureCLI: "Azure CLI app"
        case .azurePowerShell: "Azure PowerShell app"
        case .custom: "Custom app (loopback)"
        }
    }

    /// Client id used through the loopback flow, or nil when the user's own MSAL registration is used.
    public var clientId: String? {
        switch self {
        case .ownApp: nil
        case .azureCLI: "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
        case .azurePowerShell: "1950a258-227b-4e31-a9cf-717495945fc2"
        case .custom(let id): id
        }
    }

    public var usesMSAL: Bool { self == .ownApp }
    public var isCustom: Bool { if case .custom = self { true } else { false } }

    /// Whether the client is known to carry the Graph scope that activates Entra directory roles.
    /// Neither Microsoft first-party app is: they can list PIM schedules but
    /// `RoleAssignmentSchedule.ReadWrite.Directory` is admin-consent only, so Entra roles are
    /// view-only with them unless an admin grants it to the enterprise app. A custom app is
    /// assumed capable until its token says otherwise. Azure resource roles go through ARM
    /// (`user_impersonation`) and are unaffected either way.
    public var isPreauthorisedForEntraActivation: Bool {
        switch self {
        case .ownApp, .custom: true
        case .azureCLI, .azurePowerShell: false
        }
    }

    /// One-line statement of what the method can do, for the add-account dialog and headers.
    public var limitationSummary: String? {
        isPreauthorisedForEntraActivation ? nil
            : "Supports Azure resource roles only. Entra roles are neither read nor activated."
    }

    /// Longer explanation shown on the Entra rows and headers of an account using this method.
    public var entraViewOnlyReason: String? {
        isPreauthorisedForEntraActivation ? nil
            : "This account was added with the \(displayName), which supports Azure resource roles only: Microsoft grants it no Graph PIM permissions, so Elevate does not read or activate Entra roles for it. Add the account with your own or a custom app registration for Entra roles."
    }
}

// Stored as a single string so existing state files keep decoding: the fixed methods by name,
// a custom one as "custom:<client id>".
extension SignInMethod: Codable {
    public var storageKey: String {
        switch self {
        case .ownApp: "ownApp"
        case .azureCLI: "azureCLI"
        case .azurePowerShell: "azurePowerShell"
        case .custom(let id): "custom:\(id)"
        }
    }

    public init?(storageKey: String) {
        switch storageKey {
        case "ownApp": self = .ownApp
        case "azureCLI": self = .azureCLI
        case "azurePowerShell": self = .azurePowerShell
        default:
            guard storageKey.hasPrefix("custom:") else { return nil }
            let id = String(storageKey.dropFirst("custom:".count))
            guard !id.isEmpty else { return nil }
            self = .custom(clientId: id)
        }
    }

    public init(from decoder: Decoder) throws {
        let key = try decoder.singleValueContainer().decode(String.self)
        guard let method = SignInMethod(storageKey: key) else {
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Unknown sign-in method \(key)"))
        }
        self = method
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        try c.encode(storageKey)
    }
}
