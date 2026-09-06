import Foundation
import Observation
import ElevateCore

@MainActor
@Observable
final class AppModel {
    // Every stored property lives here: Swift extensions cannot add storage. The logic that owns
    // each group lives in the `AppModel+*.swift` file named beside it, which is also why several
    // members below are internal rather than `private`/`private(set)`.

    // MARK: Roles and assignments — AppModel+Refresh, AppModel+Activation

    // Persisted. Setter internal: mutated by +Accounts, +Refresh, +Activation and +Profiles.
    var state = AppState()
    // Session. Setters internal: mutated by the feature extensions listed above.
    var roles: [TenantKey: [EligibleRole]] = [:]
    var active: [RoleKey: ActiveAssignment] = [:]
    var busy: Set<TenantKey> = []
    var tenantErrors: [TenantKey: String] = [:]
    var progress: [RoleKey: ActivationOutcome.Result] = [:]
    /// Roles with an activation or deactivation request currently in flight; rows show a busy indicator.
    var inFlight: Set<RoleKey> = []

    // MARK: Approvals — AppModel+Approvals

    /// Requests awaiting this user's decision, per tenant and kind. Session only: approvals are
    /// read opportunistically on every refresh and a failed read keeps the previous list.
    var approvals: [TenantKey: [RoleScopeKind: [ApprovalRequest]]] = [:]
    /// Requests whose Approve/Deny is currently being sent; their rows show a spinner.
    var decisionInFlight: Set<String> = []
    /// Last decision failure per request id, shown on the row and in the sheet.
    var approvalErrors: [String: String] = [:]

    // MARK: Panel — AppModel+Panel

    var selectMode = false { didSet { if !selectMode { selection.removeAll(); editingProfileId = nil } } }
    /// Panel search. Not persisted; changing it drops the bulk selection since rows may disappear.
    var searchQuery = "" { didSet { if searchQuery != oldValue { selection.removeAll() } } }
    /// Collapsed state lives here, not in view @State: rows inside the lazy panel list are recreated as they scroll.
    var collapsedTenants: Set<TenantKey> = []
    var collapsedIdentities: Set<String> = []
    var selection: Set<RoleKey> = []
    var startupError: String?
    /// Transient, dismissible message (failed sign-in, unreadable state file). Never blocks the panel.
    var notice: String?
    var pendingExtend: RoleKey?

    // MARK: Refresh — AppModel+Refresh

    /// Tenants whose interactive sign-in the user dismissed this session; refreshes stay silent for them until Refresh or Retry discovery.
    var declinedTenants: Set<TenantKey> = []          // internal for AppModel+Accounts, AppModel+Refresh
    var bootstrapped = false                          // internal for AppModel+Refresh
    var lastRefresh: Date = .distantPast              // internal for AppModel+Refresh
    /// Policies are stable per role; fetching them again on every refresh is wasted quota.
    var policyCache: [RoleKey: RolePolicy] = [:]      // internal for AppModel+Refresh, AppModel+Activation
    private var refreshTimer: Task<Void, Never>?
    /// Coarse "now" for views that must not drive their own timers (the menu bar label); ticks every 30 s.
    private(set) var clock: Date = .now
    private var clockTimer: Task<Void, Never>?

    // MARK: Profiles — AppModel+Profiles

    var editingProfileId: UUID?
    /// Bumped each time the user asks to run a profile. `WindowGroup(for:)` refocuses an existing
    /// window instead of re-running `.onAppear`, so the Run sheet re-plans on a change here —
    /// and only then, never merely because the window regained focus.
    /// Setter internal: `requestRun` lives in AppModel+Profiles.
    var runRequests: [UUID: Int] = [:]

    // MARK: Operations — AppModel+Operations

    /// Set when the global hot key's profile needs input; `MenuBarLabel` opens the Run sheet for it.
    var pendingProfileRun: UUID?
    /// Why the global shortcut could not be registered, shown in Settings.
    var hotKeyError: String?
    /// One global hot key, created with the model and reconfigured by `applyHotKey()`.
    let hotKeys = HotKeyCenter()                      // internal for AppModel+Operations
    /// The last errors the user was shown, for "Copy diagnostics". Session only: a support
    /// report describes this launch, and a persisted log would be one more file holding
    /// service messages we cannot vet.
    /// Setter internal: `logError` lives in AppModel+Operations.
    var errorLog = ErrorLog()
    /// Why the last launch-at-login change failed, shown under the toggle.
    var launchAtLoginError: String?
    /// A newer release than the running build, once a check has found one and the user has not
    /// dismissed it. The panel shows a banner while it is set.
    /// Setter internal: `checkForUpdates`/`dismissUpdate` live in AppModel+Operations.
    var updateAvailable: (version: String, url: URL)?
    /// The one-line result of the last check, for the Settings button.
    /// Setter internal: `checkForUpdates` lives in AppModel+Operations.
    var updateCheckMessage: String?

    // MARK: Dependencies

    let settings: AppSettings
    private(set) var tokens: any TokenProviding
    private(set) var coordinator: ActivationCoordinator
    /// Approval readers/deciders, one per kind, rebuilt with the coordinator when the client id changes.
    private(set) var approvalProviders: [RoleScopeKind: any ApprovalProvider]
    private(set) var discovery: TenantDiscovery
    private let store: AppStateStore
    let notifier: any ExpiryNotifying                 // internal for AppModel+Refresh, +Approvals, +Activation
    private let network: NetworkMonitor
    let http: any HTTPClient                          // internal for AppModel+Operations
    private let anchor: AuthAnchorWindow?
    /// The pieces behind `tokens` when it is a `CompositeTokenProvider`, kept so `applyClientId`
    /// can swap the MSAL half without disturbing the first-party providers (and their keychain items).
    private var msal: MSALTokenProvider?
    let loopback: LoopbackProviderRegistry            // internal for AppModel+Accounts
    /// One interactive gate for every provider, so an MSAL webview and a browser sign-in queue
    /// instead of racing each other. `applyClientId` hands it to the replacement MSAL provider.
    private let gate: InteractiveGate
    /// Mutation order for saves, so a slow write cannot land after a newer one.
    private var saveGeneration: UInt64 = 0
    /// Bumped by `applyClientId`; in-flight refreshes started under an older client id
    /// check this before writing to state so they cannot repopulate what was just cleared.
    var configGeneration = 0                          // internal for every AppModel+* extension

    /// Set only by tests, which cannot change `BuildInfo.signingState` (it describes the running
    /// test host). nil in the app, where the signing state decides.
    private let ownAppViaLoopbackOverride: Bool?

    /// Whether the own-app registration signs in through the loopback PKCE flow with the Settings
    /// client id instead of MSAL. True on ad-hoc (unsigned) builds: MSAL keeps its token cache in
    /// the shared data-protection keychain group, which such a build has no entitlement to read,
    /// so the loopback flow is the only one that works — and it needs nothing from the signature.
    var ownAppViaLoopback: Bool { ownAppViaLoopbackOverride ?? (BuildInfo.signingState == .adHoc) }

    /// The loopback provider that stands in for MSAL on unsigned builds: the Settings client id,
    /// stamping its identities `.ownApp`. nil when MSAL is used or no client id is configured.
    /// Cached by the registry, so this is cheap and always follows `settings.clientId`.
    var ownAppLoopbackProvider: LoopbackTokenProvider? {
        Self.ownAppLoopbackProvider(loopback, settings: settings, enabled: ownAppViaLoopback)
    }

    private static func ownAppLoopbackProvider(_ registry: LoopbackProviderRegistry, settings: AppSettings,
                                               enabled: Bool) -> LoopbackTokenProvider? {
        guard enabled, settings.isConfigured else { return nil }
        return registry.provider(clientId: settings.clientId.trimmingCharacters(in: .whitespacesAndNewlines),
                                 reportedMethod: .ownApp)
    }

    /// True once the app has a usable client id and a way to sign in with it — MSAL on a signed
    /// build, the loopback flow on an unsigned one. The first-party methods work without it.
    var isConfigured: Bool { settings.isConfigured && (msal != nil || ownAppViaLoopback) }

    init(tokens: any TokenProviding, http: any HTTPClient, store: AppStateStore, notifier: any ExpiryNotifying,
         network: NetworkMonitor = NetworkMonitor(), settings: AppSettings = AppSettings(), anchor: AuthAnchorWindow? = nil,
         msal: MSALTokenProvider? = nil, loopback: LoopbackProviderRegistry? = nil,
         gate: InteractiveGate = InteractiveGate(), ownAppViaLoopbackOverride: Bool? = nil) {
        self.ownAppViaLoopbackOverride = ownAppViaLoopbackOverride
        self.tokens = tokens
        self.msal = msal
        self.loopback = loopback ?? LoopbackProviderRegistry(http: http, gate: gate)
        self.gate = gate
        self.http = http
        self.store = store
        self.notifier = notifier
        self.network = network
        self.settings = settings
        self.anchor = anchor
        coordinator = ActivationCoordinator(providers: [EntraDirectoryProvider(http: http, tokens: tokens), AzureResourceProvider(http: http, tokens: tokens), GroupProvider(http: http, tokens: tokens)], tokens: tokens)
        approvalProviders = Self.makeApprovalProviders(http: http, tokens: tokens)
        discovery = TenantDiscovery(http: http, tokens: tokens)
    }

    private static func makeApprovalProviders(http: any HTTPClient, tokens: any TokenProviding) -> [RoleScopeKind: any ApprovalProvider] {
        [.entraDirectory: EntraApprovalProvider(http: http, tokens: tokens),
         .group: GroupApprovalProvider(http: http, tokens: tokens),
         .azureResource: AzureApprovalProvider(http: http, tokens: tokens)]
    }

    /// Production wiring. The client id lives in `AppSettings`; when it is missing or unusable,
    /// the panel shows `SetupView` instead of a startup error.
    static func live() -> AppModel {
        let settings = AppSettings()
        let anchor = AuthAnchorWindow()
        let notifier = ExpiryNotifier()
        let http = URLSessionHTTPClient()
        // One shared gate across MSAL and the loopback providers, so no two interactive sign-ins
        // (webview or browser) can run at the same time.
        let gate = InteractiveGate()
        // On an unsigned build MSAL cannot work at all (its cache lives in a keychain access group
        // the build has no entitlement for), so it is never constructed and never reports an error;
        // the own-app registration goes through the loopback flow instead.
        let viaLoopback = BuildInfo.signingState == .adHoc
        var msal: MSALTokenProvider?
        var initError: Error?
        if settings.isConfigured, !viaLoopback {
            do {
                msal = try MSALTokenProvider(clientId: settings.clientId.trimmingCharacters(in: .whitespacesAndNewlines), redirectUri: AppSettings.redirectUri, anchor: anchor, gate: gate)
            } catch {
                initError = error
            }
        }
        // Loopback providers need no configuration; they exist whether or not MSAL does.
        let loopback = LoopbackProviderRegistry(http: http, gate: gate)
        let ownAppLoopback = ownAppLoopbackProvider(loopback, settings: settings, enabled: viaLoopback)
        let tokens = CompositeTokenProvider(msal: msal, loopback: loopback, ownAppLoopback: ownAppLoopback)
        let model = AppModel(tokens: tokens, http: http, store: AppStateStore(), notifier: notifier, settings: settings,
                             anchor: anchor, msal: msal, loopback: loopback, gate: gate)
        if let initError {
            model.notice = "Could not initialise sign-in with the saved client ID: \((initError as? PIMError)?.userMessage ?? initError.localizedDescription). Check it in Settings."
            model.logError("Sign-in setup: \((initError as? PIMError)?.userMessage ?? initError.localizedDescription)")
        }
        notifier.onExtend = { [weak model] key in model?.pendingExtend = key }
        notifier.onAuthorizationDenied = { [weak model] in
            model?.notice = "Notifications are off for Elevate; enable them in System Settings to get expiry alerts."
            model?.logError("Notifications are not authorised for Elevate")
        }
        return model
    }

    /// Saves a new client id. The token cache is per client — MSAL's on a signed build, the
    /// loopback keychain store on an unsigned one — so every *own-app* account is signed out and
    /// cleared; first-party accounts keep their own refresh tokens and stay.
    func applyClientId(_ raw: String) throws {
        let id = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard AppSettings.isValidClientId(id) else { throw PIMError.unexpected(status: 0, body: "Enter the application (client) ID as a GUID") }
        // Construct the new provider before mutating anything, so a throwing init leaves the
        // current client id, tokens and session state untouched. On an unsigned build there is no
        // MSAL provider to build: the replacement loopback provider is derived from the new id below.
        var replacement: MSALTokenProvider?
        if !ownAppViaLoopback {
            guard let anchor else { throw PIMError.unexpected(status: 0, body: "Sign-in is unavailable in this build") }
            replacement = try MSALTokenProvider(clientId: id, redirectUri: AppSettings.redirectUri, anchor: anchor, gate: gate)
        }
        let ownApp = state.identities.filter { $0.signInMethod == .ownApp }
        // The old client's cache is unusable under the new client id; drop it silently.
        // A webview sign-out here would only interrupt the user with a browser window.
        try? msal?.removeCachedAccounts(ownApp)
        if let previous = ownAppLoopbackProvider, !ownApp.isEmpty {
            // Same for the loopback refresh tokens, which are stored per client id.
            Task { for identity in ownApp { try? await previous.signOut(identity) } }
        }
        configGeneration += 1
        for identity in ownApp { forgetIdentity(identity.id) }
        lastRefresh = .distantPast
        selection = []; busy = []; inFlight = []
        decisionInFlight = []; approvalErrors = [:]
        pendingExtend = nil; selectMode = false
        persist()
        settings.clientId = id
        msal = replacement
        let composite = CompositeTokenProvider(msal: replacement, loopback: loopback, ownAppLoopback: ownAppLoopbackProvider)
        tokens = composite
        coordinator = ActivationCoordinator(providers: [EntraDirectoryProvider(http: http, tokens: composite), AzureResourceProvider(http: http, tokens: composite), GroupProvider(http: http, tokens: composite)], tokens: composite)
        approvalProviders = Self.makeApprovalProviders(http: http, tokens: composite)
        discovery = TenantDiscovery(http: http, tokens: composite)
        notice = nil
        startupError = nil
        Task { await self.rescheduleNotifications() }
    }

    /// Drops one identity and everything derived from it, in state and in memory.
    // internal for AppModel+Accounts
    func forgetIdentity(_ identityId: String) {
        state.removeIdentity(identityId)
        for key in roles.keys where key.identityId == identityId { roles[key] = nil }
        active = active.filter { $0.key.identityId != identityId }
        progress = progress.filter { $0.key.identityId != identityId }
        tenantErrors = tenantErrors.filter { $0.key.identityId != identityId }
        dropApprovals { $0.identityId == identityId }
        dropPolicies { $0.identityId == identityId }
    }

    // MARK: Derived

    var identities: [Identity] { state.identities }
    /// Accounts a client-id change would sign out; the first-party ones are unaffected.
    var ownAppIdentityCount: Int { state.identities.count { $0.signInMethod == .ownApp } }
    /// False when the machine has no usable network path; reads and requests are held back.
    var isOnline: Bool { network.isOnline }
    func tenants(for identityId: String) -> [TenantContext] { state.tenants(for: identityId) }
    func roles(for tenantKey: TenantKey) -> [EligibleRole] { roles[tenantKey] ?? [] }

    /// Why the Groups tab is empty by construction for this tenant, or nil when groups are read normally.
    func groupsUnavailableReason(for key: TenantKey) -> String? {
        guard let identity = identity(key.identityId) else { return nil }
        if !identity.signInMethod.isPreauthorisedForEntraActivation {
            return "The \(identity.signInMethod.displayName) supports Azure resource roles only; PIM for Groups needs your own or a custom app registration."
        }
        return tenant(key)?.groupsUnavailableReason
    }
    func role(for key: RoleKey) -> EligibleRole? { roles[key.tenantKey]?.first { $0.key == key } }
    func assignment(for key: RoleKey) -> ActiveAssignment? { active[key] }
    func remembered(for key: RoleKey) -> RoleMemory? { state.memory(for: key) }
    func identity(_ id: String) -> Identity? { state.identities.first { $0.id == id } }
    func tenant(_ key: TenantKey) -> TenantContext? { state.tenants.first { $0.id == key } }

    /// Only own-app accounts can be consented to: the first-party client ids are Microsoft's,
    /// already consented tenant-wide, and are not ours to request consent for.
    func adminConsentURL(identityId: String, tenantId: String) -> URL? {
        guard isConfigured, identity(identityId)?.signInMethod == .ownApp else { return nil }
        var components = URLComponents()
        components.scheme = "https"
        components.host = "login.microsoftonline.com"
        components.path = "/\(tenantId)/v2.0/adminconsent"
        components.queryItems = [
            URLQueryItem(name: "client_id", value: settings.clientId.trimmingCharacters(in: .whitespacesAndNewlines)),
            URLQueryItem(name: "scope", value: (GraphScopes.all + GroupScopes.all).joined(separator: " ")),
            URLQueryItem(name: "redirect_uri", value: "https://login.microsoftonline.com/common/oauth2/nativeclient"),
        ]
        return components.url
    }

    // MARK: Lifecycle

    func bootstrap() async {
        guard !bootstrapped else { return }
        bootstrapped = true
        do {
            state = try await store.load()
        } catch {
            // Never write over a file we could not read; move it aside first.
            _ = try? await store.quarantineCorruptFile()
            state = AppState()
            notice = "Saved state could not be read; it was moved to state.json.bak"
            logError("Saved state could not be read: \((error as? PIMError)?.userMessage ?? error.localizedDescription)")
        }
        // Reconcile with MSAL's cache: drop own-app identities MSAL no longer knows.
        if msal != nil, let known = try? await tokens.identities() {
            let ids = Set(known.map(\.id))
            for identity in state.identities where identity.signInMethod == .ownApp && !ids.contains(identity.id) {
                state.removeIdentity(identity.id)
            }
        }
        // First-party identities live only in `AppState`; they are real only while their refresh
        // token is still in the keychain. A Keychain read failure must not be mistaken for "no
        // token" — that would sign real accounts out on a transient error, so we fail open and
        // keep the identity, telling the user their state may be stale.
        var unreadable = false
        var droppedUPNs: [String] = []
        // On an unsigned build own-app identities are reconciled here too, against the loopback
        // store for the Settings client id — including accounts a signed build added through MSAL,
        // which have no loopback token and are correctly dropped.
        for identity in state.identities where identity.signInMethod != .ownApp || ownAppViaLoopback {
            let known = identity.signInMethod == .ownApp
                ? ownAppLoopbackProvider
                : loopback.provider(for: identity.signInMethod)
            guard let provider = known else { continue }
            switch await provider.refreshTokenState(for: identity.id) {
            case .some(false):
                state.removeIdentity(identity.id)
                droppedUPNs.append(identity.upn)
            case .some(true):
                break
            case .none:
                unreadable = true
            }
        }
        if !droppedUPNs.isEmpty {
            notice = "\(droppedUPNs.joined(separator: ", ")) was signed out because its saved sign-in is gone; add the account again."
            logError("Signed out (no saved sign-in): \(droppedUPNs.joined(separator: ", "))")
        } else if unreadable {
            notice = "Could not read saved sign-ins from the Keychain; your accounts were kept."
            logError("Could not read saved sign-ins from the Keychain")
        }
        persist()
        if isOnline { await refreshAll() }
        startTimer()
        applyHotKey()
        // Fire and forget: an update check must never hold up the first panel open.
        Task { await self.checkForUpdates() }
    }

    // MARK: Timers

    private func startClock() {
        clockTimer?.cancel()
        clockTimer = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(30))
                guard let self else { return }
                self.clock = .now
            }
        }
    }

    private func startTimer() {
        startClock()
        refreshTimer?.cancel()
        refreshTimer = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(60))
                guard let self else { return }
                // Pending approvals live in `active` too, and they need polling to flip to active.
                guard self.isOnline, !self.active.isEmpty else { continue }
                await self.refreshAll()
            }
        }
    }

    // MARK: Housekeeping

    /// Policies belong to the role they were fetched for; drop them when that role can no longer be trusted.
    // internal for AppModel+Accounts
    func dropPolicies(where matches: (RoleKey) -> Bool) {
        for key in policyCache.keys where matches(key) { policyCache[key] = nil }
    }

    /// Forgets the approvals of the matching tenants, along with their in-flight and error state,
    /// so a removed tenant or signed-out account leaves nothing in the pinned section.
    // internal for AppModel+Accounts
    func dropApprovals(where matches: (TenantKey) -> Bool) {
        for key in approvals.keys where matches(key) {
            for id in (approvals[key] ?? [:]).values.flatMap({ $0 }).map(\.id) {
                decisionInFlight.remove(id)
                approvalErrors[id] = nil
            }
            approvals[key] = nil
        }
    }

    // internal for every AppModel+* extension
    func persist() {
        saveGeneration += 1
        let snapshot = state
        let generation = saveGeneration
        Task { try? await store.save(snapshot, generation: generation) }
    }
}
