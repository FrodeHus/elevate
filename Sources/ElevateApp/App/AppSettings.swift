import Foundation
import Observation

/// User-editable configuration. The client id is the only required value; it lives in UserDefaults, not in a bundled plist.
@MainActor
@Observable
final class AppSettings {
    static let bundleId = "no.frodehus.elevate"
    /// Bundle id used before the app was renamed; its UserDefaults are migrated on first launch.
    static let legacyBundleId = "no.frodehus.pimtray"
    static var redirectUri: String { "msauth.\(bundleId)://auth" }
    static let clientIdKey = "clientId"

    private let defaults: UserDefaults

    var clientId: String {
        didSet { defaults.set(clientId, forKey: Self.clientIdKey) }
    }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        var stored = defaults.string(forKey: Self.clientIdKey) ?? ""
        if stored.isEmpty, let legacy = UserDefaults(suiteName: Self.legacyBundleId)?.string(forKey: Self.clientIdKey), !legacy.isEmpty {
            stored = legacy
            defaults.set(legacy, forKey: Self.clientIdKey)
        }
        clientId = stored
    }

    var isConfigured: Bool { Self.isValidClientId(clientId) }

    static func isValidClientId(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return UUID(uuidString: trimmed) != nil && trimmed != "00000000-0000-0000-0000-000000000000"
    }
}
