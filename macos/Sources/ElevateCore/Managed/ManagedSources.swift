import Foundation

/// The keys an MDM administrator can push through managed preferences, and their raw string
/// representation as used in the `com.apple.ManagedClient.preferences` payload.
public enum ManagedKey: String, CaseIterable, Hashable, Sendable {
    case clientId = "ClientId"
    case disableUpdateCheck = "DisableUpdateCheck"
    case allowedSignInMethods = "AllowedSignInMethods"
    case allowedTenants = "AllowedTenants"
    case pinnedTenants = "PinnedTenants"
    case managedProfiles = "ManagedProfiles"
    case managedProfilesUrl = "ManagedProfilesUrl"
}

/// Something that can supply raw managed-configuration values by key, without knowing anything
/// about how those values are validated or combined. `ManagedConfiguration.load(from:)` does the
/// validation; a source is just a typed lookup.
public protocol ManagedConfigurationSource: Sendable {
    func string(_ key: ManagedKey) -> String?
    func bool(_ key: ManagedKey) -> Bool?
    func list(_ key: ManagedKey) -> [String]?
    var origin: String { get }
}

/// Shared coercions used by every source: a bool from a `Bool`, an `NSNumber`, or the strings
/// "true"/"1"; a list from an array of strings or a comma-separated string.
enum ManagedValue {
    static func bool(_ value: Any?) -> Bool? {
        switch value {
        case let b as Bool: b
        case let n as NSNumber: n.boolValue
        case let s as String: ["true", "1"].contains(s.lowercased()) ? true : (["false", "0"].contains(s.lowercased()) ? false : nil)
        default: nil
        }
    }

    static func list(_ value: Any?) -> [String]? {
        switch value {
        case let a as [String]: a
        case let a as [Any]: a.compactMap { $0 as? String }
        case let s as String: s.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
        default: nil
        }
    }
}

/// A managed configuration source backed by an in-memory dictionary — used in tests, and as the
/// shape a plist- or JSON-decoded payload would take.
///
/// The values dictionary is typed `[String: Any]` rather than `[String: any Sendable]`: an
/// untyped empty array literal (as in `["AllowedSignInMethods": []]`) cannot satisfy an
/// `any Sendable` value type under Swift 6's strict concurrency checking. The struct is
/// `@unchecked Sendable` instead — its values are only ever read, never mutated, after `init`.
public struct DictionaryManagedSource: ManagedConfigurationSource, @unchecked Sendable {
    private let values: [String: Any]
    public let origin: String

    public init(_ values: [String: Any], origin: String = "test") {
        self.values = values
        self.origin = origin
    }

    public func string(_ key: ManagedKey) -> String? {
        switch values[key.rawValue] {
        case let s as String: s
        case let n as NSNumber: n.stringValue
        default: nil
        }
    }

    public func bool(_ key: ManagedKey) -> Bool? { ManagedValue.bool(values[key.rawValue]) }
    public func list(_ key: ManagedKey) -> [String]? { ManagedValue.list(values[key.rawValue]) }
}

/// The real managed configuration source: values pushed by MDM into `UserDefaults` as forced
/// preferences. Only forced keys are read — a value the user or app wrote themselves under the
/// same key is not managed configuration and must not be treated as such.
public struct ManagedPreferences: ManagedConfigurationSource, @unchecked Sendable {
    // UserDefaults is not Sendable, but Apple documents it as thread-safe, so this is safe to
    // share across isolation domains.
    private let defaults: UserDefaults
    public let origin = "managed preferences"

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    private func forced(_ key: ManagedKey) -> Any? {
        guard defaults.objectIsForced(forKey: key.rawValue) else { return nil }
        return defaults.object(forKey: key.rawValue)
    }

    public func string(_ key: ManagedKey) -> String? {
        switch forced(key) {
        case let s as String: s
        case let n as NSNumber: n.stringValue
        default: nil
        }
    }

    public func bool(_ key: ManagedKey) -> Bool? { ManagedValue.bool(forced(key)) }
    public func list(_ key: ManagedKey) -> [String]? { ManagedValue.list(forced(key)) }
}
