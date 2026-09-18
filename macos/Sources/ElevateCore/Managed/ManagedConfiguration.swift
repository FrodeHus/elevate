import Foundation

/// Settings pushed by an organization's MDM, merged from whichever `ManagedConfigurationSource`
/// the app is configured with. Every field is optional or defaulted so an app with no managed
/// configuration behaves exactly as it would without this type existing at all.
public struct ManagedConfiguration: Hashable, Sendable {
    public var clientId: String?
    public var disableUpdateCheck: Bool
    public var allowedSignInMethods: Set<SignInMethodKind>?
    public var allowedTenants: [String]?
    public var pinnedTenants: [String]
    /// Raw JSON text for the managed profiles document; parsed elsewhere.
    public var managedProfilesDocument: String?
    public var managedProfilesUrl: URL?
    /// The organization's display name, 1-32 characters. Gates the three keys below it.
    public var organizationName: String?
    public var organizationTitleStyle: OrganizationTitleStyle?
    public var organizationSupportUrl: URL?
    public var organizationSupportEmail: String?
    /// The keys that carried a valid, in-effect value, in `ManagedKey.allCases` order.
    public var keysInEffect: [ManagedKey]
    /// Human-readable notes about values that were present but rejected.
    public var warnings: [String]
    /// The source's `origin`, set only when at least one key is in effect.
    public var origin: String?

    public init() {
        clientId = nil
        disableUpdateCheck = false
        allowedSignInMethods = nil
        allowedTenants = nil
        pinnedTenants = []
        managedProfilesDocument = nil
        managedProfilesUrl = nil
        organizationName = nil
        organizationTitleStyle = nil
        organizationSupportUrl = nil
        organizationSupportEmail = nil
        keysInEffect = []
        warnings = []
        origin = nil
    }

    /// The configuration with nothing managed — equivalent to `ManagedConfiguration()`.
    public static let none = ManagedConfiguration()

    public var isEmpty: Bool { keysInEffect.isEmpty }

    /// Reads and validates every managed key from `source`, collecting warnings for values that
    /// were present but rejected. Keys with no value, or with a value that fails validation, are
    /// simply absent from the result — they do not affect `keysInEffect` or `origin`.
    public static func load(from source: any ManagedConfigurationSource) -> ManagedConfiguration {
        var config = ManagedConfiguration()

        if let raw = source.string(.clientId) {
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if let uuid = UUID(uuidString: trimmed), uuid != UUID(uuidString: "00000000-0000-0000-0000-000000000000") {
                config.clientId = trimmed.lowercased()
                config.keysInEffect.append(.clientId)
            } else {
                config.warnings.append("ClientId: '\(raw)' is not a valid GUID")
            }
        }

        if let flag = source.bool(.disableUpdateCheck) {
            config.disableUpdateCheck = flag
            config.keysInEffect.append(.disableUpdateCheck)
        }

        if let names = source.list(.allowedSignInMethods), !names.isEmpty {
            var kinds: Set<SignInMethodKind> = []
            for name in names {
                if let kind = SignInMethodKind.allCases.first(where: { $0.rawValue.lowercased() == name.lowercased() }) {
                    kinds.insert(kind)
                } else {
                    config.warnings.append("AllowedSignInMethods: unknown method '\(name)' ignored")
                }
            }
            if !kinds.isEmpty {
                config.allowedSignInMethods = kinds
                config.keysInEffect.append(.allowedSignInMethods)
            }
        }

        if let tenants = source.list(.allowedTenants) {
            let cleaned = Self.cleanedTenantList(tenants)
            if !cleaned.isEmpty {
                config.allowedTenants = cleaned
                config.keysInEffect.append(.allowedTenants)
            }
        }

        if let tenants = source.list(.pinnedTenants) {
            let cleaned = Self.cleanedTenantList(tenants)
            if !cleaned.isEmpty {
                config.pinnedTenants = cleaned
                config.keysInEffect.append(.pinnedTenants)
            }
        }

        if let raw = source.string(.managedProfiles) {
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty {
                config.managedProfilesDocument = raw
                config.keysInEffect.append(.managedProfiles)
            }
        }

        if let raw = source.string(.managedProfilesUrl) {
            if let url = URL(string: raw), url.scheme == "https" {
                config.managedProfilesUrl = url
                config.keysInEffect.append(.managedProfilesUrl)
            } else {
                config.warnings.append("ManagedProfilesUrl: only https URLs are accepted")
            }
        }

        if let raw = source.string(.organizationName) {
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if trimmed.isEmpty {
                config.warnings.append("OrganizationName: a blank name is ignored")
            } else if trimmed.count > 32 {
                config.warnings.append("OrganizationName: '\(trimmed)' is \(trimmed.count) characters; the maximum is 32")
            } else {
                config.organizationName = trimmed
                config.keysInEffect.append(.organizationName)
            }
        }

        // The three keys below decorate the name; without one they have nothing to attach to.
        let hasName = config.organizationName != nil

        if let raw = source.string(.organizationTitleStyle) {
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if !hasName {
                config.warnings.append("OrganizationTitleStyle: ignored because OrganizationName is not set")
            } else if let style = OrganizationTitleStyle.allCases.first(where: { $0.rawValue.lowercased() == trimmed.lowercased() }) {
                config.organizationTitleStyle = style
                config.keysInEffect.append(.organizationTitleStyle)
            } else {
                config.warnings.append("OrganizationTitleStyle: '\(raw)' is not one of by, managedBy, none")
            }
        }

        if let raw = source.string(.organizationSupportUrl) {
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if !hasName {
                config.warnings.append("OrganizationSupportUrl: ignored because OrganizationName is not set")
            } else if let url = URL(string: trimmed), url.scheme == "https" {
                config.organizationSupportUrl = url
                config.keysInEffect.append(.organizationSupportUrl)
            } else {
                config.warnings.append("OrganizationSupportUrl: only https URLs are accepted")
            }
        }

        if let raw = source.string(.organizationSupportEmail) {
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if !hasName {
                config.warnings.append("OrganizationSupportEmail: ignored because OrganizationName is not set")
            } else if trimmed.count >= 3, trimmed.contains("@"), !trimmed.contains(where: \.isWhitespace) {
                config.organizationSupportEmail = trimmed
                config.keysInEffect.append(.organizationSupportEmail)
            } else {
                config.warnings.append("OrganizationSupportEmail: '\(raw)' is not an email address")
            }
        }

        if !config.keysInEffect.isEmpty {
            config.origin = source.origin
        }

        return config
    }

    /// Trims, lower-cases, drops blanks, and de-duplicates while preserving order.
    private static func cleanedTenantList(_ raw: [String]) -> [String] {
        var seen = Set<String>()
        var result: [String] = []
        for entry in raw {
            let cleaned = entry.trimmingCharacters(in: .whitespaces).lowercased()
            guard !cleaned.isEmpty, !seen.contains(cleaned) else { continue }
            seen.insert(cleaned)
            result.append(cleaned)
        }
        return result
    }
}
