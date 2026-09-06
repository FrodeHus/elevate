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
    static let collapsedApprovalsKey = "collapsedApprovals"
    static let lastApprovalJustificationKey = "lastApprovalJustification"
    static let seenApprovalIdsKey = "seenApprovalIds"
    static let hotKeyKey = "hotKey"
    static let hotKeyProfileKey = "hotKeyProfileId"
    static let lastUpdateCheckKey = "lastUpdateCheck"
    static let dismissedUpdateVersionKey = "dismissedUpdateVersion"

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

    /// Whether the panel's pinned "Approvals" section is collapsed; remembered between launches.
    var collapsedApprovals: Bool {
        didSet { defaults.set(collapsedApprovals, forKey: Self.collapsedApprovalsKey) }
    }

    /// The justification typed into the last decision sheet, used to prefill the next one.
    var lastApprovalJustification: String {
        didSet { defaults.set(lastApprovalJustification, forKey: Self.lastApprovalJustificationKey) }
    }

    /// Ids of approval requests already notified about, as JSON, so a relaunch does not re-notify.
    /// Pruned after each refresh to the ids still pending.
    var seenApprovalIds: Set<String> {
        didSet {
            if let data = try? JSONEncoder().encode(seenApprovalIds) {
                defaults.set(data, forKey: Self.seenApprovalIdsKey)
            } else {
                defaults.removeObject(forKey: Self.seenApprovalIdsKey)
            }
        }
    }

    /// The global shortcut, stored as JSON. Nil means no shortcut is registered.
    var hotKey: HotKeyBinding? {
        didSet {
            if let hotKey, let data = try? JSONEncoder().encode(hotKey) {
                defaults.set(data, forKey: Self.hotKeyKey)
            } else {
                defaults.removeObject(forKey: Self.hotKeyKey)
            }
        }
    }

    /// The profile the global shortcut runs. Without it the shortcut stays unregistered.
    var hotKeyProfileId: UUID? {
        didSet {
            if let hotKeyProfileId {
                defaults.set(hotKeyProfileId.uuidString, forKey: Self.hotKeyProfileKey)
            } else {
                defaults.removeObject(forKey: Self.hotKeyProfileKey)
            }
        }
    }

    /// When the automatic update check last ran, so it can be throttled to once a day.
    /// Nil until the first check completes.
    var lastUpdateCheck: Date? {
        didSet {
            if let lastUpdateCheck {
                defaults.set(lastUpdateCheck, forKey: Self.lastUpdateCheckKey)
            } else {
                defaults.removeObject(forKey: Self.lastUpdateCheckKey)
            }
        }
    }

    /// The release tag the user dismissed in the panel; that version is never offered again.
    var dismissedUpdateVersion: String? {
        didSet {
            if let dismissedUpdateVersion {
                defaults.set(dismissedUpdateVersion, forKey: Self.dismissedUpdateVersionKey)
            } else {
                defaults.removeObject(forKey: Self.dismissedUpdateVersionKey)
            }
        }
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
        collapsedApprovals = defaults.bool(forKey: Self.collapsedApprovalsKey)
        lastApprovalJustification = defaults.string(forKey: Self.lastApprovalJustificationKey) ?? ""
        seenApprovalIds = (defaults.data(forKey: Self.seenApprovalIdsKey)).flatMap { try? JSONDecoder().decode(Set<String>.self, from: $0) } ?? []
        hotKey = (defaults.data(forKey: Self.hotKeyKey)).flatMap { try? JSONDecoder().decode(HotKeyBinding.self, from: $0) }
        hotKeyProfileId = (defaults.string(forKey: Self.hotKeyProfileKey)).flatMap(UUID.init(uuidString:))
        lastUpdateCheck = defaults.object(forKey: Self.lastUpdateCheckKey) as? Date
        dismissedUpdateVersion = defaults.string(forKey: Self.dismissedUpdateVersionKey)
    }

    var isConfigured: Bool { Self.isValidClientId(clientId) }

    static func isValidClientId(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return UUID(uuidString: trimmed) != nil && trimmed != "00000000-0000-0000-0000-000000000000"
    }
}
