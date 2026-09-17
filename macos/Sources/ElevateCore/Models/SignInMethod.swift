import Foundation

/// The case of a `SignInMethod` without its associated data, for contexts that need a
/// `Hashable`/`Sendable` identifier for the method rather than the method itself — for example
/// managed configuration's allow-list of permitted sign-in methods.
public enum SignInMethodKind: String, CaseIterable, Hashable, Sendable {
    case ownApp, azureCLI, azurePowerShell, custom
}

/// How an account authenticates. First-party methods need no app registration or admin consent;
/// `custom` is any other public-client registration (for example a company-wide PIM app that
/// knows nothing about macOS) used through the same loopback browser flow.
public enum SignInMethod: Hashable, Sendable {
    case ownApp
    /// An Elevate-equivalent registration of the account's own, independent of the Settings
    /// client id. Build it with `pinned(_:)`, which normalises the id.
    case pinnedApp(clientId: String)
    case azureCLI
    case azurePowerShell
    case custom(clientId: String)

    /// The methods offered as fixed choices; `custom` needs a client id typed by the user.
    public static let builtIn: [SignInMethod] = [.ownApp, .azureCLI, .azurePowerShell]

    /// A pinned own-app method with `clientId` trimmed and lower-cased, so the same GUID typed
    /// in another case cannot become a second method or a second keychain item.
    public static func pinned(_ clientId: String) -> SignInMethod {
        .pinnedApp(clientId: normalizedClientId(clientId))
    }

    public static func normalizedClientId(_ raw: String) -> String {
        raw.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    }

    public var displayName: String {
        switch self {
        case .ownApp, .pinnedApp: "Entra app registration"
        case .azureCLI: "Azure CLI app"
        case .azurePowerShell: "Azure PowerShell app"
        case .custom: "Company app (client ID)"
        }
    }

    /// `displayName`, plus the start of the client id for a pinned registration so two
    /// "Entra app registration" accounts can be told apart.
    public var detailedName: String {
        guard case .pinnedApp(let id) = self else { return displayName }
        return "\(displayName) (\(id.prefix(8))…)"
    }

    /// Client id of the registration, or nil for `.ownApp`, which uses the Settings client id.
    public var clientId: String? {
        switch self {
        case .ownApp: nil
        case .pinnedApp(let id): id
        case .azureCLI: "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
        case .azurePowerShell: "1950a258-227b-4e31-a9cf-717495945fc2"
        case .custom(let id): id
        }
    }

    /// Either form of the Entra app registration: the Settings one or a pinned one.
    public var isOwnApp: Bool {
        switch self {
        case .ownApp, .pinnedApp: true
        default: false
        }
    }

    public var isPinned: Bool { if case .pinnedApp = self { true } else { false } }

    public var usesMSAL: Bool { isOwnApp }

    /// Whether the account signs in as the Azure CLI or Azure PowerShell app, whose token cache the
    /// tool itself reads: an activation through Elevate then provably leaves that tool a stale token.
    public var sharesToolTokenCache: Bool { self == .azureCLI || self == .azurePowerShell }
    public var isCustom: Bool { if case .custom = self { true } else { false } }

    /// The case of this method without its associated data, for comparisons like managed
    /// configuration's allow-list that only cares which kind of method was used.
    public var kind: SignInMethodKind {
        switch self {
        case .ownApp, .pinnedApp: .ownApp
        case .azureCLI: .azureCLI
        case .azurePowerShell: .azurePowerShell
        case .custom: .custom
        }
    }

    /// Whether the client is known to carry the Graph scope that activates Entra directory roles.
    /// Neither Microsoft first-party app is: they can list PIM schedules but
    /// `RoleAssignmentSchedule.ReadWrite.Directory` is admin-consent only, so Entra roles are
    /// view-only with them unless an admin grants it to the enterprise app. A custom app is
    /// assumed capable until its token says otherwise. Azure resource roles go through ARM
    /// (`user_impersonation`) and are unaffected either way.
    public var isPreauthorisedForEntraActivation: Bool {
        switch self {
        case .ownApp, .pinnedApp, .custom: true
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
// a pinned own-app registration as "ownApp:<client id>", a custom one as "custom:<client id>".
extension SignInMethod: Codable {
    public var storageKey: String {
        switch self {
        case .ownApp: "ownApp"
        case .pinnedApp(let id): "ownApp:\(Self.normalizedClientId(id))"
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
            if storageKey.hasPrefix("ownApp:") {
                let id = String(storageKey.dropFirst("ownApp:".count))
                guard !Self.normalizedClientId(id).isEmpty else { return nil }
                self = .pinned(id)
            } else if storageKey.hasPrefix("custom:") {
                let id = String(storageKey.dropFirst("custom:".count))
                guard !Self.normalizedClientId(id).isEmpty else { return nil }
                self = .custom(clientId: id)
            } else {
                return nil
            }
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
