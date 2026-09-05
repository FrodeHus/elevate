import Foundation
import Observation

/// User-editable configuration. The client id is the only required value; it lives in UserDefaults, not in a bundled plist.
@MainActor
@Observable
final class AppSettings {
    static let bundleId = "no.reothor.elevate"
    /// Bundle ids used before the app was renamed; their UserDefaults are migrated on first launch.
    static let legacyBundleIds = ["no.frodehus.elevate", "no.frodehus.pimtray"]
    static var redirectUri: String { "msauth.\(bundleId)://auth" }
    static let clientIdKey = "clientId"
    static let customClientIdKey = "customLoopbackClientId"

    private let defaults: UserDefaults

    var clientId: String {
        didSet { defaults.set(clientId, forKey: Self.clientIdKey) }
    }

    /// Last client id typed into "Custom app (loopback)" in Add account, so the next account
    /// from the same company app needs no retyping. Not a configuration value in its own right.
    var customClientId: String {
        didSet { defaults.set(customClientId, forKey: Self.customClientIdKey) }
    }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        var stored = defaults.string(forKey: Self.clientIdKey) ?? ""
        if stored.isEmpty {
            for legacyId in Self.legacyBundleIds {
                if let legacy = UserDefaults(suiteName: legacyId)?.string(forKey: Self.clientIdKey), !legacy.isEmpty {
                    stored = legacy
                    defaults.set(legacy, forKey: Self.clientIdKey)
                    break
                }
            }
        }
        clientId = stored
        customClientId = defaults.string(forKey: Self.customClientIdKey) ?? ""
    }

    var isConfigured: Bool { Self.isValidClientId(clientId) }

    static func isValidClientId(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return UUID(uuidString: trimmed) != nil && trimmed != "00000000-0000-0000-0000-000000000000"
    }
}
