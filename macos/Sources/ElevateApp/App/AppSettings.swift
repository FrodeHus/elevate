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
    static let panelTabKey = "panelTab"
    static let collapsedActiveKey = "collapsedActive"

    private let defaults: UserDefaults

    var clientId: String {
        didSet { defaults.set(clientId, forKey: Self.clientIdKey) }
    }

    /// Last client id typed into "Custom app (loopback)" in Add account, so the next account
    /// from the same company app needs no retyping. Not a configuration value in its own right.
    var customClientId: String {
        didSet { defaults.set(customClientId, forKey: Self.customClientIdKey) }
    }

    /// The panel's last-used tab, so it reopens where the user left it.
    var panelTab: PanelTab {
        didSet { defaults.set(panelTab.rawValue, forKey: Self.panelTabKey) }
    }

    /// Whether the panel's "Active now" summary is collapsed; remembered between launches.
    var collapsedActive: Bool {
        didSet { defaults.set(collapsedActive, forKey: Self.collapsedActiveKey) }
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
        panelTab = PanelTab(rawValue: defaults.string(forKey: Self.panelTabKey) ?? "") ?? .roles
        collapsedActive = defaults.bool(forKey: Self.collapsedActiveKey)
    }

    var isConfigured: Bool { Self.isValidClientId(clientId) }

    static func isValidClientId(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return UUID(uuidString: trimmed) != nil && trimmed != "00000000-0000-0000-0000-000000000000"
    }
}
