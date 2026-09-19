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
    var deactivationProgress: [RoleKey: DeactivationPhase] = [:]
    /// Assignments deactivated moments ago, kept so their "Active now" row can confirm in place.
    var recentlyDeactivated: [RoleKey: ActiveAssignment] = [:]
    var profileDeactivationProgress: [UUID: [RoleKey: DeactivationPhase]] = [:]
    /// Roles with an activation or deactivation request currently in flight; rows show a busy indicator.
    var inFlight: Set<RoleKey> = []

    // MARK: Propagation — AppModel+Propagation

    /// Roles probed since they were activated, and where each got to. A role stays here once it
    /// settles — `.ready` is what lets the activation sheet close on "Ready" rather than on the word
    /// PIM would have used. Only `.propagating` and `.unconfirmed` change a row; the other two read
    /// as a plain active row. Session only; never persisted.
    var propagation: [RoleKey: PropagationState] = [:]
    /// The probe running for each watched role, so it can be cancelled when the role goes away.
    /// The id tells a finishing watch from the one that replaced it.
    @ObservationIgnored var propagationWatches: [RoleKey: PropagationWatch] = [:]
    /// Probe pacing. Tests shorten it so they do not sit through the real interval.
    @ObservationIgnored var propagationFirstInterval = PropagationWatcher.defaultFirstInterval
    @ObservationIgnored var propagationMaxInterval = PropagationWatcher.defaultMaxInterval

    // MARK: Approvals — AppModel+Approvals

    /// Requests awaiting this user's decision, per tenant and kind. Session only: approvals are
    /// read opportunistically on every refresh and a failed read keeps the previous list.
    var approvals: [TenantKey: [RoleScopeKind: [ApprovalRequest]]] = [:]
    /// Requests whose Approve/Deny is currently being sent; their rows show a spinner.
    var decisionInFlight: Set<String> = []
    /// Last decision failure per request id, shown on the row and in the sheet.
    var approvalErrors: [String: String] = [:]

    // MARK: Access packages — AppModel+AccessPackages

    /// Last poll failure per tenant, shown in the access packages window.
    var accessPackageErrors: [TenantKey: String] = [:]
    /// Tenants with a poll in flight, so two triggers cannot overlap.
    var accessPackagesPolling: Set<TenantKey> = []
    /// Self-service entitlement calls, rebuilt with the coordinator when the client id changes.
    private(set) var accessPackageProvider: AccessPackageProvider
    private var accessPackageTimer: Task<Void, Never>?

    // MARK: Panel — AppModel+Panel

    var selectMode = false { didSet { if !selectMode { selection.removeAll() } } }
    /// Panel search. Not persisted; changing it drops the bulk selection since rows may disappear.
    var searchQuery = "" { didSet { if searchQuery != oldValue { selection.removeAll() } } }
    /// Collapsed state lives here, not in view @State: rows inside the lazy panel list are recreated as they scroll.
    var collapsedTenants: Set<TenantKey> = []
    var collapsedIdentities: Set<String> = []
    /// Collapsed Azure scope nodes, keyed by `scopeNodeKey(_:_:)`. In-memory like the other two,
    /// and scopes start open, so nothing is hidden on first sight.
    var collapsedScopes: Set<String> = []
    var selection: Set<RoleKey> = []
    var startupError: String?
    /// Transient, dismissible message (failed sign-in, unreadable state file). Never blocks the panel.
    var notice: String?
    var pendingExtend: RoleKey?

    // MARK: Refresh — AppModel+Refresh

    /// Tenants whose interactive sign-in the user dismissed this session; refreshes stay silent for them until Refresh or Retry discovery.
    var declinedTenants: Set<TenantKey> = []          // internal for AppModel+Accounts, AppModel+Refresh
    /// Accounts whose saved sign-in is gone (a refresh token deleted behind the app's back, or
    /// rejected for good by the service). They keep their tenants and configured roles; refreshes
    /// skip them until "Sign in again" succeeds or the user signs them out. Session only:
    /// `bootstrap()` recomputes it from the token stores on every launch.
    var signInNeeded: Set<String> = []                // internal for AppModel+Accounts, AppModel+Refresh
    /// Tenants whose silent token acquisition failed during a background refresh. Background
    /// work (timer, wake, launch, panel open) never prompts, so nothing opened a browser; the
    /// tenant keeps the rows it had and is flagged in the panel until a user-initiated refresh
    /// signs it in, or a later silent refresh succeeds on its own. Session only.
    var tenantsAwaitingSignIn: Set<TenantKey> = []   // internal for AppModel+Refresh
    var bootstrapped = false                          // internal for AppModel+Refresh
    var lastRefresh: Date = .distantPast              // internal for AppModel+Refresh
    /// Policies are stable per role; fetching them again on every refresh is wasted quota.
    var policyCache: [RoleKey: RolePolicy] = [:]      // internal for AppModel+Refresh, AppModel+Activation
    private var refreshTimer: Task<Void, Never>?
    /// Coarse "now" for views that must not drive their own timers (the menu bar label); ticks every 30 s.
    private(set) var clock: Date = .now
    private var clockTimer: Task<Void, Never>?

    // MARK: Profiles — AppModel+Profiles

    /// The profile "Edit…" asked the Profiles window to select. The window reads and clears it,
    /// on open and again when the existing window is refocused with a new request.
    var profileToEdit: UUID?
    /// The profile a chip or list menu asked to delete; the confirmation dialog reads it. Lives
    /// here because the menu and the dialog are on different views.
    var profileToDelete: UUID?
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
    /// Accounts whose cached Azure CLI, Azure PowerShell and kubelogin tokens the last Azure or
    /// group activation left behind, oldest first; the panel shows a hint for the first one.
    /// Managed by `noteTokenHint`/`dismissTokenHint` in AppModel+Activation.
    var tokenHintAccounts: [String] = []

    // MARK: Managed configuration — AppModel+Managed

    /// Tenant ids the organization's `AllowedTenants` permits, nil when the key is not in effect
    /// or an entry could not be resolved (the restriction is never applied on guesswork; the
    /// unresolved entry becomes a warning instead).
    /// Setter internal: filled by `resolveManagedTenants()` in AppModel+Managed.
    var allowedTenantIds: Set<String>?
    /// Tenant ids the organization's `PinnedTenants` resolved to, in the configured order.
    /// Setter internal: filled by `resolveManagedTenants()` in AppModel+Managed.
    var pinnedTenantIds: [String] = []
    /// Managed tenant entry, exactly as configured → the tenant id it resolved to.
    /// Setter internal: filled by `resolveManagedTenants()` in AppModel+Managed.
    var managedTenantIds: [String: String] = [:]
    /// One line per managed tenant entry that could not be resolved, shown in Settings.
    /// Setter internal: filled by `resolveManagedTenants()` in AppModel+Managed.
    var managedTenantWarnings: [String] = []
    /// False while the managed tenants still need resolving — there was no network path when
    /// `bootstrap()` tried. The reconnect handler runs the resolution once when the path returns.
    /// Setter internal: set by `resolveManagedTenants()` in AppModel+Managed.
    var managedTenantsResolved = false

    // MARK: Managed profiles — AppModel+ManagedProfiles

    /// The inline `ManagedProfiles` document, parsed once at init: managed preferences do not
    /// change under a running app, so parsing it on every read would be wasted work.
    /// Setter internal: filled in `init` through `AppModel+ManagedProfiles`.
    var inlineProfileSet: ManagedProfileSet = .empty
    /// The one warning a rejected inline document produces, kept beside the parsed set.
    var inlineProfileWarning: String?
    /// The last document fetched from `ManagedProfilesUrl`, or the cached copy of it. Nil until
    /// a fetch or a cache read has produced one.
    /// Setter internal: filled by `refreshManagedProfiles` in AppModel+ManagedProfiles.
    var fetchedProfileSet: ManagedProfileSet?
    /// Why the last fetch failed, as a Settings warning; cleared by the next success.
    /// Setter internal: set by `refreshManagedProfiles` in AppModel+ManagedProfiles.
    var managedProfileFetchWarning: String?
    /// Downloads and caches the published profile document next to `state.json`. It needs only
    /// `http` and the cache location, neither of which changes, so one instance serves the app's life.
    let profileFetcher: ManagedProfileFetcher     // internal for AppModel+ManagedProfiles

    // MARK: Dependencies

    let settings: AppSettings
    private(set) var tokens: any TokenProviding
    private(set) var coordinator: ActivationCoordinator
    /// Approval readers/deciders, one per kind, rebuilt with the coordinator when the client id changes.
    private(set) var approvalProviders: [RoleScopeKind: any ApprovalProvider]
    private(set) var discovery: TenantDiscovery
    /// Turns managed tenant entries (GUIDs or verified domains) into tenant ids, caching what it
    /// resolves. It needs only `http`, which never changes, so one instance serves the app's life
    /// — `applyClientId` has nothing to rebuild here.
    let tenantResolver: ManagedTenantResolver          // internal for AppModel+Managed
    private let store: AppStateStore
    let notifier: any ExpiryNotifying                 // internal for AppModel+Refresh, +Approvals, +Activation
    private let network: NetworkMonitor
    let http: any HTTPClient                          // internal for AppModel+Operations
    private let anchor: AuthAnchorWindow?
    /// The pieces behind `tokens` when it is a `CompositeTokenProvider`, kept so `applyClientId`
    /// can swap the MSAL half without disturbing the first-party providers (and their keychain items).
    private var msal: MSALTokenProvider?
    /// MSAL providers for pinned client ids; nil on unsigned builds and in tests.
    let pinnedMSAL: MSALProviderRegistry?             // internal for AppModel+Accounts
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

    /// True when the configured client id is the project-provided shared Elevate app registration.
    var usesSharedApp: Bool { settings.usesSharedClientId }

    /// The settings an organization pushed through MDM, for the views that show what is managed.
    var managed: ManagedConfiguration { settings.managed }

    /// The route `CompositeTokenProvider` uses for `.pinnedApp` accounts: MSAL on a signed build,
    /// the loopback flow (stamping `.pinnedApp`) on an unsigned one.
    nonisolated static func pinnedRoute(registry: MSALProviderRegistry?, loopback: LoopbackProviderRegistry,
                                        viaLoopback: Bool) -> @Sendable (String) throws -> any TokenProviding {
        { clientId in
            let method = SignInMethod.pinned(clientId)
            if viaLoopback {
                guard let id = method.clientId, let provider = loopback.provider(clientId: id, reportedMethod: method) else {
                    throw PIMError.unexpected(status: 0, body: "Enter the application (client) ID as a GUID")
                }
                return provider
            }
            guard let registry else { throw PIMError.unexpected(status: 0, body: "Sign-in is unavailable in this build") }
            return try registry.provider(clientId: clientId)
        }
    }

    /// The client id `method` signs in with: the Settings id for `.ownApp` (nil when it is not a
    /// valid GUID), the method's own id otherwise.
    func effectiveClientId(for method: SignInMethod) -> String? {
        guard method == .ownApp else { return method.clientId }
        let id = settings.clientId.trimmingCharacters(in: .whitespacesAndNewlines)
        return AppSettings.isValidClientId(id) ? id : nil
    }

    /// Forgets `identity`'s saved sign-in for its current method without a browser window.
    func discardCachedSignIn(_ identity: Identity) async {
        await discardCachedSignIn(identity, via: tokens)
    }

    /// Like `discardCachedSignIn(_:)`, through `provider` — a token provider captured before
    /// `applyClientId` could replace `tokens`.
    func discardCachedSignIn(_ identity: Identity, via provider: any TokenProviding) async {
        if let composite = provider as? CompositeTokenProvider {
            await composite.discardCachedSignIn(identity)
        } else {
            try? await provider.signOut(identity)
        }
    }

    init(tokens: any TokenProviding, http: any HTTPClient, store: AppStateStore, notifier: any ExpiryNotifying,
         network: NetworkMonitor = NetworkMonitor(), settings: AppSettings = AppSettings(), anchor: AuthAnchorWindow? = nil,
         msal: MSALTokenProvider? = nil, pinnedMSAL: MSALProviderRegistry? = nil, loopback: LoopbackProviderRegistry? = nil,
         gate: InteractiveGate = InteractiveGate(), ownAppViaLoopbackOverride: Bool? = nil) {
        self.ownAppViaLoopbackOverride = ownAppViaLoopbackOverride
        self.tokens = tokens
        self.msal = msal
        self.pinnedMSAL = pinnedMSAL
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
        accessPackageProvider = AccessPackageProvider(http: http, tokens: tokens)
        tenantResolver = ManagedTenantResolver(http: http)
        profileFetcher = ManagedProfileFetcher(http: http, cacheURL: store.directory.appendingPathComponent(Self.managedProfilesCacheFile))
        loadInlineProfiles()
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
        let pinnedMSAL = viaLoopback ? nil : MSALProviderRegistry(anchor: anchor, gate: gate)
        let tokens = CompositeTokenProvider(msal: msal, loopback: loopback, ownAppLoopback: ownAppLoopback,
                                            pinned: pinnedRoute(registry: pinnedMSAL, loopback: loopback, viaLoopback: viaLoopback))
        let model = AppModel(tokens: tokens, http: http, store: AppStateStore(), notifier: notifier, settings: settings,
                             anchor: anchor, msal: msal, pinnedMSAL: pinnedMSAL, loopback: loopback, gate: gate)
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
    /// asked to sign in again, keeping its tenants, roles and profiles; first-party accounts keep
    /// their own refresh tokens and stay.
    func applyClientId(_ raw: String) throws {
        guard !settings.isClientIdManaged else { throw PIMError.unexpected(status: 0, body: "The client ID is managed by your organization") }
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
        // `== .ownApp` deliberately excludes pinned accounts: only accounts following Settings are affected.
        let ownApp = state.identities.filter { $0.signInMethod == .ownApp }
        // The old client's cache is unusable under the new client id; drop it silently.
        // A webview sign-out here would only interrupt the user with a browser window.
        try? msal?.removeCachedAccounts(ownApp)
        if let previous = ownAppLoopbackProvider, !ownApp.isEmpty {
            // Same for the loopback refresh tokens, which are stored per client id.
            Task { for identity in ownApp { try? await previous.signOut(identity) } }
        }
        configGeneration += 1
        deactivationProgress.removeAll()
        recentlyDeactivated.removeAll()
        stopAllPropagationWatches()
        // The accounts keep their tenants, roles and profiles; they sign in again under the new id.
        for identity in ownApp { dropRuntime(identity.id) }
        signInNeeded.formUnion(ownApp.map(\.id))
        lastRefresh = .distantPast
        selection = []; busy = []; inFlight = []
        decisionInFlight = []; approvalErrors = [:]
        pendingExtend = nil; selectMode = false
        persist()
        settings.clientId = id
        msal = replacement
        let composite = CompositeTokenProvider(msal: replacement, loopback: loopback, ownAppLoopback: ownAppLoopbackProvider,
                                               pinned: Self.pinnedRoute(registry: pinnedMSAL, loopback: loopback, viaLoopback: ownAppViaLoopback))
        tokens = composite
        coordinator = ActivationCoordinator(providers: [EntraDirectoryProvider(http: http, tokens: composite), AzureResourceProvider(http: http, tokens: composite), GroupProvider(http: http, tokens: composite)], tokens: composite)
        approvalProviders = Self.makeApprovalProviders(http: http, tokens: composite)
        discovery = TenantDiscovery(http: http, tokens: composite)
        accessPackageProvider = AccessPackageProvider(http: http, tokens: composite)
        accessPackageErrors = [:]
        notice = nil
        startupError = nil
        Task { await self.rescheduleNotifications() }
    }

    /// Drops one identity and everything derived from it, in state and in memory.
    // internal for AppModel+Accounts
    func forgetIdentity(_ identityId: String) {
        state.removeIdentity(identityId)
        signInNeeded.remove(identityId)
        dropRuntime(identityId)
    }

    /// Drops what this session read or started for an account, keeping the account itself, its
    /// tenants, configured roles, profile entries and role memory. Used when its registration
    /// changes and everything read under the old one is stale.
    // internal for AppModel+Accounts
    func dropRuntime(_ identityId: String) {
        for key in roles.keys where key.identityId == identityId { roles[key] = nil }
        active = active.filter { $0.key.identityId != identityId }
        progress = progress.filter { $0.key.identityId != identityId }
        deactivationProgress = deactivationProgress.filter { $0.key.identityId != identityId }
        recentlyDeactivated = recentlyDeactivated.filter { $0.key.identityId != identityId }
        for (runId, phases) in profileDeactivationProgress {
            let kept = phases.filter { $0.key.identityId != identityId }
            profileDeactivationProgress[runId] = kept.isEmpty ? nil : kept
        }
        tenantErrors = tenantErrors.filter { $0.key.identityId != identityId }
        tenantsAwaitingSignIn = tenantsAwaitingSignIn.filter { $0.identityId != identityId }
        declinedTenants = declinedTenants.filter { $0.identityId != identityId }
        dropApprovals { $0.identityId == identityId }
        dropPolicies { $0.identityId == identityId }
    }

    // MARK: Derived

    var identities: [Identity] { state.identities }
    /// Accounts that follow the Settings client id and would need to sign in again after it changes.
    var ownAppIdentityCount: Int { state.identities.count { $0.signInMethod == .ownApp } }
    /// False when the machine has no usable network path; reads and requests are held back.
    var isOnline: Bool { network.isOnline }
    func tenants(for identityId: String) -> [TenantContext] { state.tenants(for: identityId) }
    func roles(for tenantKey: TenantKey) -> [EligibleRole] { roles[tenantKey] ?? [] }

    /// Why the Groups tab is empty by construction for this tenant, or nil when groups are read normally.
    func groupsUnavailableReason(for key: TenantKey) -> String? {
        guard let identity = identity(key.identityId) else { return nil }
        if !identity.signInMethod.isPreauthorisedForEntraActivation {
            return "The \(identity.signInMethod.displayName) supports Azure resource roles only; PIM for Groups needs your own or another app registration."
        }
        return tenant(key)?.groupsUnavailableReason
    }
    func role(for key: RoleKey) -> EligibleRole? { roles[key.tenantKey]?.first { $0.key == key } }
    func assignment(for key: RoleKey) -> ActiveAssignment? { active[key] }
    func isNewRole(_ key: RoleKey) -> Bool { state.roleTracker(key.tenantKey).isNew(key) }
    func remembered(for key: RoleKey) -> RoleMemory? { state.memory(for: key) }
    func identity(_ id: String) -> Identity? { state.identities.first { $0.id == id } }
    func tenant(_ key: TenantKey) -> TenantContext? { state.tenants.first { $0.id == key } }

    /// Only Entra app registration accounts can be consented to: the first-party client ids are
    /// Microsoft's, already consented tenant-wide, and are not ours to request consent for.
    func adminConsentURL(identityId: String, tenantId: String) -> URL? {
        guard let method = identity(identityId)?.signInMethod, method.isOwnApp else { return nil }
        if method == .ownApp, !isConfigured { return nil }
        guard let clientId = effectiveClientId(for: method) else { return nil }
        return adminConsentURL(tenantSegment: tenantId, clientId: clientId)
    }

    func sharedAppAdminConsentURL() -> URL? {
        guard isConfigured, usesSharedApp else { return nil }
        return adminConsentURL(tenantSegment: "organizations", clientId: AppSettings.sharedClientId)
    }

    private func adminConsentURL(tenantSegment: String, clientId: String) -> URL? {
        var components = URLComponents()
        components.scheme = "https"
        components.host = "login.microsoftonline.com"
        components.path = "/\(tenantSegment)/v2.0/adminconsent"
        // The shared app registration has a Web redirect URI on the product site that explains
        // the consent result; own registrations keep the loopback-friendly `nativeclient` URI.
        let shared = clientId.caseInsensitiveCompare(AppSettings.sharedClientId) == .orderedSame
        let redirectURI = shared
            ? AppSettings.sharedConsentRedirectURI
            : "https://login.microsoftonline.com/common/oauth2/nativeclient"
        components.queryItems = [
            URLQueryItem(name: "client_id", value: clientId),
            URLQueryItem(name: "scope", value: (GraphScopes.all + GroupScopes.all + EntitlementScopes.all).joined(separator: " ")),
            URLQueryItem(name: "redirect_uri", value: redirectURI),
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
        // Reconcile with MSAL's cache: an own-app identity MSAL no longer knows must sign in again.
        // The account, its tenants and its configured roles stay; a lost token may be transient
        // (a cleared cache, a revoked session) and is not the user asking to remove the account.
        var needsSignIn: [Identity] = []
        if msal != nil, let known = try? await tokens.identities() {
            let ids = Set(known.map(\.id))
            for identity in state.identities where identity.signInMethod == .ownApp && !ids.contains(identity.id) {
                needsSignIn.append(identity)
            }
        }
        // Pinned accounts on a signed build: each registration has its own MSAL cache.
        if let pinnedMSAL, !ownAppViaLoopback {
            for identity in state.identities {
                guard case .pinnedApp(let id) = identity.signInMethod,
                      let known = try? await pinnedMSAL.provider(clientId: id).identities() else { continue }
                if !known.contains(where: { $0.id == identity.id }) { needsSignIn.append(identity) }
            }
        }
        // First-party identities live only in `AppState`; they are usable only while their refresh
        // token is still in the keychain. A Keychain read failure must not be mistaken for "no
        // token" — that would flag real accounts on a transient error, so we fail open and
        // keep the identity as is, telling the user their state may be stale.
        var unreadable = false
        // On an unsigned build own-app identities are reconciled here too, against the loopback
        // store for the Settings client id — including accounts a signed build added through MSAL,
        // which have no loopback token and are correctly dropped.
        for identity in state.identities where !identity.signInMethod.usesMSAL || ownAppViaLoopback {
            guard let provider = loopbackStore(for: identity.signInMethod) else { continue }
            switch await provider.refreshTokenState(for: identity.id) {
            case .some(false):
                needsSignIn.append(identity)
            case .some(true):
                break
            case .none:
                unreadable = true
            }
        }
        signInNeeded = Set(needsSignIn.map(\.id))
        if !needsSignIn.isEmpty {
            let upns = needsSignIn.map(\.upn).joined(separator: ", ")
            notice = "\(upns) needs to sign in again: its saved sign-in is gone. Use Sign in on the account, or sign it out to remove it."
            logError("Sign-in needed (no saved sign-in): \(upns)")
        } else if unreadable {
            notice = "Could not read saved sign-ins from the Keychain; your accounts were kept."
            logError("Could not read saved sign-ins from the Keychain")
        }
        persist()
        // Neither the timer nor the hot key needs the network, and both are started first so a
        // slow tenant lookup cannot delay the shortcut the user may already be pressing.
        startTimer()
        applyHotKey()
        watchNetwork()
        // Managed tenants before the refresh: resolving may remove tenants the organization no
        // longer permits and add pinned ones, and `refreshAll()` should see the final list. It
        // never throws; offline it resolves nothing and `watchNetwork()` retries it once the
        // path comes back.
        await resolveManagedTenants()
        // The published profile document, from the cache and then over the network. It restricts
        // nothing, so a failure here is a warning in Settings and never blocks the refresh.
        await refreshManagedProfiles()
        if isOnline { await refreshAll() }
        // Fire and forget: an update check must never hold up the first panel open.
        Task { await self.checkForUpdates() }
    }

    /// Picks up the work held back while the machine had no network path, once it comes back:
    /// the managed tenants first, if they never resolved, so the refresh sees the final tenant list.
    private func watchNetwork() {
        network.onChange = { [weak self] online in
            guard online, let self else { return }
            Task { @MainActor in
                if !self.managedTenantsResolved { await self.resolveManagedTenants() }
                await self.refreshManagedProfiles()
                await self.refreshAll()
            }
        }
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
        accessPackageTimer?.cancel()
        accessPackageTimer = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(Self.accessPackageBackgroundInterval))
                guard let self else { return }
                guard self.isOnline else { continue }
                // Both are background reads on the same slow cadence; the profile fetch throttles
                // itself to once a day, so riding this tick costs nothing extra.
                await self.refreshManagedProfiles()
                await self.pollAccessPackagesIfDue()
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
