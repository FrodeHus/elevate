import Foundation
import Observation
import ElevateCore

/// User-editable configuration. The client id is the only required value; it lives in UserDefaults, not in a bundled plist.
@MainActor
@Observable
final class AppSettings {
    static let bundleId = "no.reothor.elevate"
    /// Bundle ids used before the app was renamed; their UserDefaults are migrated on first launch.
    static let legacyBundleIds = ["no.frodehus.elevate", "no.frodehus.pimtray"]
    static var redirectUri: String { "msauth.\(bundleId)://auth" }
    static let clientIdKey = "clientId"
    /// The client id of the Elevate app registration the project provides, for organizations that
    /// would rather use it than register their own. Public knowledge — not a secret — but still
    /// never printed in diagnostics, where only "shared Elevate app" appears.
    static let sharedClientId = "c9011cc5-7422-4630-a432-73ff4df5834e"
    /// Admin consent `redirect_uri` for the shared Elevate app registration: a Web redirect
    /// registered on the shared app, so the admin lands on a page that explains what just
    /// happened instead of Microsoft's "this is not the right page" screen for `nativeclient`.
    static let sharedConsentRedirectURI = "https://elevate.reothor.no/consent.html"
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
    static let dismissedTokenHintAccountsKey = "dismissedTokenHintAccounts"
    static let managedProfilesFetchedAtKey = "managedProfilesFetchedAt"

    private let defaults: UserDefaults

    /// The settings an organization pushes through MDM. Read once at launch: managed preferences
    /// do not change under a running app in any way we need to follow live.
    let managed: ManagedConfiguration

    /// The user's own client id, as typed in Settings. Managed configuration overrides what
    /// `clientId` reports but never touches this value, so removing the MDM payload brings the
    /// user's own id back.
    var storedClientId: String {
        didSet { defaults.set(storedClientId, forKey: Self.clientIdKey) }
    }

    /// The client id in effect: the managed one when an administrator pushed one, otherwise the
    /// user's. Writes are ignored while it is managed — `AppModel.applyClientId` throws instead,
    /// so the user gets a reason rather than a silently discarded edit.
    var clientId: String {
        get { managed.clientId ?? storedClientId }
        set { if !isClientIdManaged { storedClientId = newValue } }
    }

    /// True when the client id comes from managed configuration and cannot be edited here.
    var isClientIdManaged: Bool { managed.clientId != nil }

    /// True when the organization turned the update check off; the app then never calls GitHub.
    var updateCheckDisabled: Bool { managed.disableUpdateCheck }

    /// Last client id typed into "Company app (client ID)" in Add account, so the next account
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

    /// Ids of the accounts for which the stale-token hint after an Azure or group activation was
    /// dismissed. Same key as the CLI's settings file, so the wording of the choice matches.
    var dismissedTokenHintAccounts: Set<String> {
        didSet {
            if dismissedTokenHintAccounts.isEmpty {
                defaults.removeObject(forKey: Self.dismissedTokenHintAccountsKey)
            } else {
                defaults.set(dismissedTokenHintAccounts.sorted(), forKey: Self.dismissedTokenHintAccountsKey)
            }
        }
    }

    /// When the organization's published profile document was last fetched, so the once-a-day
    /// throttle survives a relaunch. Nil until the first successful fetch.
    var managedProfilesFetchedAt: Date? {
        didSet {
            if let managedProfilesFetchedAt {
                defaults.set(managedProfilesFetchedAt, forKey: Self.managedProfilesFetchedAtKey)
            } else {
                defaults.removeObject(forKey: Self.managedProfilesFetchedAtKey)
            }
        }
    }

    init(defaults: UserDefaults = .standard, managed: ManagedConfiguration? = nil) {
        self.defaults = defaults
        self.managed = managed ?? ManagedConfiguration.load(from: ManagedPreferences(defaults: defaults))
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
        storedClientId = stored
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
        dismissedTokenHintAccounts = Set(defaults.stringArray(forKey: Self.dismissedTokenHintAccountsKey) ?? [])
        managedProfilesFetchedAt = defaults.object(forKey: Self.managedProfilesFetchedAtKey) as? Date
    }

    var isConfigured: Bool { Self.isValidClientId(clientId) }

    /// True when the client id in effect is the project-provided shared Elevate app registration,
    /// regardless of case or surrounding whitespace.
    var usesSharedClientId: Bool {
        clientId.trimmingCharacters(in: .whitespacesAndNewlines).caseInsensitiveCompare(Self.sharedClientId) == .orderedSame
    }

    static func isValidClientId(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return UUID(uuidString: trimmed) != nil && trimmed != "00000000-0000-0000-0000-000000000000"
    }
}
