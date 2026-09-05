import Foundation
import Observation
import ElevateCore

@MainActor
@Observable
final class AppModel {
    // Persisted
    private(set) var state = AppState()
    // Session
    private(set) var roles: [TenantKey: [EligibleRole]] = [:]
    private(set) var active: [RoleKey: ActiveAssignment] = [:]
    private(set) var busy: Set<TenantKey> = []
    private(set) var tenantErrors: [TenantKey: String] = [:]
    private(set) var progress: [RoleKey: ActivationOutcome.Result] = [:]
    /// Roles with an activation or deactivation request currently in flight; rows show a busy indicator.
    private(set) var inFlight: Set<RoleKey> = []
    /// Requests awaiting this user's decision, per tenant and kind. Session only: approvals are
    /// read opportunistically on every refresh and a failed read keeps the previous list.
    private(set) var approvals: [TenantKey: [RoleScopeKind: [ApprovalRequest]]] = [:]
    /// Requests whose Approve/Deny is currently being sent; their rows show a spinner.
    private(set) var decisionInFlight: Set<String> = []
    /// Last decision failure per request id, shown on the row and in the sheet.
    private(set) var approvalErrors: [String: String] = [:]
    var selectMode = false { didSet { if !selectMode { selection.removeAll(); editingProfileId = nil } } }
    /// The list the panel shows; the bulk selection survives switching so a profile can span tabs.
    var panelTab: PanelTab {
        get { settings.panelTab }
        set { settings.panelTab = newValue }
    }

    var collapsedActive: Bool { settings.collapsedActive }
    func toggleActive() { settings.collapsedActive.toggle() }

    var collapsedApprovals: Bool { settings.collapsedApprovals }
    func toggleApprovals() { settings.collapsedApprovals.toggle() }

    /// Panel search. Not persisted; changing it drops the bulk selection since rows may disappear.
    var searchQuery = "" { didSet { if searchQuery != oldValue { selection.removeAll() } } }
    var isFiltering: Bool { PanelFilter.isActive(searchQuery) }

    static func kinds(for tab: PanelTab) -> Set<RoleScopeKind> {
        switch tab {
        case .roles: [.entraDirectory]
        case .azure: [.azureResource]
        case .groups: [.group]
        }
    }
    /// Collapsed state lives here, not in view @State: rows inside the lazy panel list are recreated as they scroll.
    var collapsedTenants: Set<TenantKey> = []
    var collapsedIdentities: Set<String> = []
    /// Tenants whose interactive sign-in the user dismissed this session; refreshes stay silent for them until Refresh or Retry discovery.
    private var declinedTenants: Set<TenantKey> = []
    private static func isDeclinedSignIn(_ e: PIMError) -> Bool {
        switch e {
        case .interactionRequired: true
        case .network(let m): m.localizedCaseInsensitiveContains("cancel") || m.localizedCaseInsensitiveContains("timed out")
        default: false
        }
    }
    func toggleTenant(_ key: TenantKey) { if collapsedTenants.contains(key) { collapsedTenants.remove(key) } else { collapsedTenants.insert(key) } }
    func toggleIdentity(_ id: String) { if collapsedIdentities.contains(id) { collapsedIdentities.remove(id) } else { collapsedIdentities.insert(id) } }
    var selection: Set<RoleKey> = []
    var selectionCount: Int { selection.count }
    /// Per-kind counts of the bulk selection, for the cross-tab hint in the bulk bar.
    var selectionBreakdown: (entra: Int, azure: Int, groups: Int) {
        var entra = 0, azure = 0, groups = 0
        for key in selection {
            switch key.scope.kind {
            case .entraDirectory: entra += 1
            case .azureResource: azure += 1
            case .group: groups += 1
            }
        }
        return (entra, azure, groups)
    }
    /// Noun for the bulk bar: roles and groups can be selected together across tabs.
    var selectionNoun: String {
        let kinds = Set(selection.map(\.scope.kind))
        if kinds.isEmpty || kinds == [.group] { return kinds.isEmpty ? "role" : "group" }
        return kinds.contains(.group) ? "item" : "role"
    }
    var startupError: String?
    /// Transient, dismissible message (failed sign-in, unreadable state file). Never blocks the panel.
    var notice: String?
    private var bootstrapped = false
    private var lastRefresh: Date = .distantPast
    var pendingExtend: RoleKey?
    /// Set when the global hot key's profile needs input; `MenuBarLabel` opens the Run sheet for it.
    var pendingProfileRun: UUID?
    /// Why the global shortcut could not be registered, shown in Settings.
    var hotKeyError: String?
    /// One global hot key, created with the model and reconfigured by `applyHotKey()`.
    private let hotKeys = HotKeyCenter()

    let settings: AppSettings
    private(set) var tokens: any TokenProviding
    private(set) var coordinator: ActivationCoordinator
    /// Approval readers/deciders, one per kind, rebuilt with the coordinator when the client id changes.
    private(set) var approvalProviders: [RoleScopeKind: any ApprovalProvider]
    private(set) var discovery: TenantDiscovery
    private let store: AppStateStore
    private let notifier: any ExpiryNotifying
    private let network: NetworkMonitor
    private let http: any HTTPClient
    private let anchor: AuthAnchorWindow?
    /// The pieces behind `tokens` when it is a `CompositeTokenProvider`, kept so `applyClientId`
    /// can swap the MSAL half without disturbing the first-party providers (and their keychain items).
    private var msal: MSALTokenProvider?
    private let loopback: LoopbackProviderRegistry
    /// One interactive gate for every provider, so an MSAL webview and a browser sign-in queue
    /// instead of racing each other. `applyClientId` hands it to the replacement MSAL provider.
    private let gate: InteractiveGate
    private var refreshTimer: Task<Void, Never>?
    /// Policies are stable per role; fetching them again on every refresh is wasted quota.
    private var policyCache: [RoleKey: RolePolicy] = [:]
    /// Mutation order for saves, so a slow write cannot land after a newer one.
    private var saveGeneration: UInt64 = 0
    /// Bumped by `applyClientId`; in-flight refreshes started under an older client id
    /// check this before writing to state so they cannot repopulate what was just cleared.
    private var configGeneration = 0

    /// True once the app has a usable client id and a working MSAL provider — that is, once the
    /// own-app sign-in method is available. The first-party methods work without it.
    var isConfigured: Bool { settings.isConfigured && msal != nil }

    /// Fixed sign-in methods offered by "Add account…" (a custom client id is typed there).
    /// `.ownApp` is listed even when unconfigured; the view disables it and explains why.
    var availableMethods: [SignInMethod] { SignInMethod.builtIn }

    /// The custom client id used last time, for prefilling the add-account dialog.
    var rememberedCustomClientId: String { settings.customClientId }

    /// Whether a method can be used right now. A custom method needs a well-formed client id.
    func isAvailable(_ method: SignInMethod) -> Bool {
        switch method {
        case .ownApp: isConfigured
        case .custom(let id): AppSettings.isValidClientId(id)
        default: method.clientId != nil
        }
    }

    init(tokens: any TokenProviding, http: any HTTPClient, store: AppStateStore, notifier: any ExpiryNotifying,
         network: NetworkMonitor = NetworkMonitor(), settings: AppSettings = AppSettings(), anchor: AuthAnchorWindow? = nil,
         msal: MSALTokenProvider? = nil, loopback: LoopbackProviderRegistry? = nil,
         gate: InteractiveGate = InteractiveGate()) {
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
        var msal: MSALTokenProvider?
        var initError: Error?
        if settings.isConfigured {
            do {
                msal = try MSALTokenProvider(clientId: settings.clientId.trimmingCharacters(in: .whitespacesAndNewlines), redirectUri: AppSettings.redirectUri, anchor: anchor, gate: gate)
            } catch {
                initError = error
            }
        }
        // Loopback providers need no configuration; they exist whether or not MSAL does.
        let loopback = LoopbackProviderRegistry(http: http, gate: gate)
        let tokens = CompositeTokenProvider(msal: msal, loopback: loopback)
        let model = AppModel(tokens: tokens, http: http, store: AppStateStore(), notifier: notifier, settings: settings,
                             anchor: anchor, msal: msal, loopback: loopback, gate: gate)
        if let initError {
            model.notice = "Could not initialise sign-in with the saved client ID: \((initError as? PIMError)?.userMessage ?? initError.localizedDescription). Check it in Settings."
        }
        notifier.onExtend = { [weak model] key in model?.pendingExtend = key }
        notifier.onAuthorizationDenied = { [weak model] in
            model?.notice = "Notifications are off for Elevate; enable them in System Settings to get expiry alerts."
        }
        return model
    }

    /// Saves a new client id. MSAL's token cache is per client, so every *own-app* account is
    /// signed out and cleared; first-party accounts keep their own refresh tokens and stay.
    func applyClientId(_ raw: String) throws {
        let id = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard AppSettings.isValidClientId(id) else { throw PIMError.unexpected(status: 0, body: "Enter the application (client) ID as a GUID") }
        guard let anchor else { throw PIMError.unexpected(status: 0, body: "Sign-in is unavailable in this build") }
        // Construct the new provider before mutating anything, so a throwing init leaves the
        // current client id, tokens and session state untouched.
        let replacement = try MSALTokenProvider(clientId: id, redirectUri: AppSettings.redirectUri, anchor: anchor, gate: gate)
        let ownApp = state.identities.filter { $0.signInMethod == .ownApp }
        // The old client's cache is unusable under the new client id; drop it silently.
        // A webview sign-out here would only interrupt the user with a browser window.
        try? msal?.removeCachedAccounts(ownApp)
        configGeneration += 1
        for identity in ownApp { forgetIdentity(identity.id) }
        lastRefresh = .distantPast
        selection = []; busy = []; inFlight = []
        decisionInFlight = []; approvalErrors = [:]
        pendingExtend = nil; selectMode = false
        persist()
        settings.clientId = id
        msal = replacement
        let composite = CompositeTokenProvider(msal: replacement, loopback: loopback)
        tokens = composite
        coordinator = ActivationCoordinator(providers: [EntraDirectoryProvider(http: http, tokens: composite), AzureResourceProvider(http: http, tokens: composite), GroupProvider(http: http, tokens: composite)], tokens: composite)
        approvalProviders = Self.makeApprovalProviders(http: http, tokens: composite)
        discovery = TenantDiscovery(http: http, tokens: composite)
        notice = nil
        startupError = nil
        Task { await self.rescheduleNotifications() }
    }

    /// Drops one identity and everything derived from it, in state and in memory.
    private func forgetIdentity(_ identityId: String) {
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
    private func matchesFilter(_ role: EligibleRole) -> Bool {
        guard isFiltering else { return true }
        let tenantName = tenant(role.key.tenantKey)?.displayName ?? role.key.tenantId
        let upn = identity(role.key.identityId)?.upn ?? ""
        return PanelFilter.matches(query: searchQuery, role: role, tenantName: tenantName, upn: upn)
    }

    func roles(for tenantKey: TenantKey, tab: PanelTab) -> [EligibleRole] {
        let kinds = Self.kinds(for: tab)
        return roles(for: tenantKey).filter { kinds.contains($0.key.scope.kind) && matchesFilter($0) }
    }

    /// While filtering, only tenants with a matching row in the current tab; otherwise all of them.
    func visibleTenants(for identityId: String) -> [TenantContext] {
        let all = tenants(for: identityId)
        guard isFiltering else { return all }
        return all.filter { !roles(for: $0.id, tab: panelTab).isEmpty }
    }

    var visibleIdentities: [Identity] {
        guard isFiltering else { return identities }
        return identities.filter { !visibleTenants(for: $0.id).isEmpty }
    }

    /// Name for the summary row; before the eligible list has loaded only the key is known.
    func summaryName(for key: RoleKey) -> String {
        if let r = role(for: key) { return r.displayName }
        switch key.scope {
        case .entraDirectory(let id, _): return id
        case .azureResource(let scope, let id): return "\(id.components(separatedBy: "/").last ?? id) @ \(scope)"
        case .group(let gid, let access): return "\(gid) (\(access == .owner ? "owner" : "member"))"
        }
    }

    /// Active assignments of `tab`'s kinds, for the tab labels' counts.
    func activeCount(for tab: PanelTab) -> Int {
        let kinds = Self.kinds(for: tab)
        return active.values.count { $0.status == .active && kinds.contains($0.roleKey.scope.kind) }
    }

    /// The "Active now" summary shows only the current tab's kinds; the tab labels carry the other counts.
    var activeAssignmentsOrdered: [ActiveAssignment] {
        let kinds = Self.kinds(for: panelTab)
        let ordered = ActiveSummary.order(active.values.filter { kinds.contains($0.roleKey.scope.kind) })
        guard isFiltering else { return ordered }
        return ordered.filter { a in
            if let r = role(for: a.roleKey) { return matchesFilter(r) }
            return PanelFilter.matches(query: searchQuery, text: summaryName(for: a.roleKey))
        }
    }

    /// Every pending request across accounts and tenants, oldest first, tenant name and id as
    /// tiebreaks so the order is stable between refreshes. Filtered by the panel search when active.
    var approvalsOrdered: [ApprovalRequest] {
        let ordered = allApprovals.sorted { a, b in
            let da = a.createdAt ?? .distantPast, db = b.createdAt ?? .distantPast
            if da != db { return da < db }
            let na = approvalTenantName(a), nb = approvalTenantName(b)
            if na != nb { return na < nb }
            return a.id < b.id
        }
        guard isFiltering else { return ordered }
        return ordered.filter { r in
            [r.targetName, r.requesterName, approvalTenantName(r)].contains { PanelFilter.matches(query: searchQuery, text: $0) }
        }
    }

    /// The menu bar glyph's condition: everything pending, never narrowed by the panel search.
    var pendingApprovalCount: Int { allApprovals.count }

    private var allApprovals: [ApprovalRequest] { approvals.values.flatMap { $0.values.flatMap { $0 } } }

    func approvalTenantName(_ request: ApprovalRequest) -> String {
        tenant(request.tenantKey)?.displayName ?? request.tenantKey.tenantId
    }

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
        for identity in state.identities where identity.signInMethod != .ownApp {
            guard let provider = loopback.provider(for: identity.signInMethod) else { continue }
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
        } else if unreadable {
            notice = "Could not read saved sign-ins from the Keychain; your accounts were kept."
        }
        persist()
        if isOnline { await refreshAll() }
        startTimer()
        applyHotKey()
    }

    // MARK: Global shortcut

    /// Re-registers the global shortcut from settings. Registering unregisters first, so calling
    /// this after every Settings change cannot leave a stale hot key behind.
    func applyHotKey() {
        hotKeys.unregister()
        hotKeyError = nil
        guard let binding = settings.hotKey, settings.hotKeyProfileId != nil else {
            hotKeys.onFire = nil
            return
        }
        hotKeys.onFire = { [weak self] in
            Task { @MainActor in
                guard let self, let id = self.settings.hotKeyProfileId else { return }
                if await self.quickRun(profileId: id) { return }
                // Needs a justification, ticket or duration: open the Run sheet instead.
                self.requestRun(id)
                self.pendingProfileRun = id
            }
        }
        do {
            try hotKeys.register(binding)
        } catch {
            hotKeyError = error.localizedDescription
        }
    }

    /// Coarse "now" for views that must not drive their own timers (the menu bar label); ticks every 30 s.
    private(set) var clock: Date = .now
    private var clockTimer: Task<Void, Never>?

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

    /// Policies belong to the role they were fetched for; drop them when that role can no longer be trusted.
    private func dropPolicies(where matches: (RoleKey) -> Bool) {
        for key in policyCache.keys where matches(key) { policyCache[key] = nil }
    }

    /// Forgets the approvals of the matching tenants, along with their in-flight and error state,
    /// so a removed tenant or signed-out account leaves nothing in the pinned section.
    private func dropApprovals(where matches: (TenantKey) -> Bool) {
        for key in approvals.keys where matches(key) {
            for id in (approvals[key] ?? [:]).values.flatMap({ $0 }).map(\.id) {
                decisionInFlight.remove(id)
                approvalErrors[id] = nil
            }
            approvals[key] = nil
        }
    }

    private func persist() {
        saveGeneration += 1
        let snapshot = state
        let generation = saveGeneration
        Task { try? await store.save(snapshot, generation: generation) }
    }

    // MARK: Accounts

    /// Signs in with `method` and adds the resulting account, its home tenant and its roles.
    /// Sets `notice` and leaves the state untouched when the sign-in fails. Returns whether an
    /// account was actually added (a saved-refresh-token warning still counts as success).
    @discardableResult
    func addAccount(method: SignInMethod = .ownApp) async -> Bool {
        guard isAvailable(method) else {
            switch method {
            case .ownApp: notice = "Complete initial setup first"
            case .custom: notice = "Enter the custom app's application (client) ID as a GUID"
            default: notice = "That sign-in method is unavailable"
            }
            return false
        }
        if case .custom(let id) = method { settings.customClientId = id }
        do {
            let identity = try await tokens.signIn(method: method)
            // The same account under a different method would fight over the same rows and tenants.
            if let existing = state.identities.first(where: { $0.id == identity.id }), existing.signInMethod != method {
                notice = "This account is already added with \(existing.signInMethod.displayName)"
                try? await tokens.signOut(identity)
                return false
            }
            if !state.identities.contains(where: { $0.id == identity.id }) {
                state.identities.append(identity)
            }
            if !method.usesMSAL, let failure = await loopback.provider(for: method)?.persistenceError() {
                notice = "Signed in, but the refresh token could not be saved to the Keychain: \(failure). You will be asked to sign in again after restart."
            }
            let homeKey = TenantKey(identityId: identity.id, tenantId: identity.homeTenantId)
            if tenant(homeKey) == nil {
                let name = (try? await discovery.tenantDisplayName(identity: identity, tenantId: identity.homeTenantId)) ?? identity.homeTenantId
                state.upsertTenant(TenantContext(identityId: identity.id, tenantId: identity.homeTenantId, displayName: name, source: .home))
            }
            persist()
            await refresh(homeKey)
            return true
        } catch {
            notice = (error as? PIMError)?.userMessage ?? error.localizedDescription
            return false
        }
    }

    func signOut(_ identity: Identity) {
        Task {
            try? await tokens.signOut(identity)
            forgetIdentity(identity.id)
            persist()
        }
    }

    // MARK: Tenants

    func addTenant(identityId: String, domainOrId: String) async throws {
        let generation = configGeneration
        guard let identity = self.identity(identityId) else { throw PIMError.unexpected(status: 0, body: "Unknown identity") }
        let tenantId = try await discovery.resolveTenantId(domainOrId: domainOrId)
        guard generation == configGeneration else { return }
        let key = TenantKey(identityId: identityId, tenantId: tenantId)
        guard tenant(key) == nil else { return }
        let name = (try? await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: tenantId, scopes: [GraphScopes.userRead]) { @Sendable in
            try await discovery.tenantDisplayName(identity: identity, tenantId: tenantId)
        }) ?? domainOrId
        guard generation == configGeneration else { return }
        state.upsertTenant(TenantContext(identityId: identityId, tenantId: tenantId, displayName: name, source: .manual))
        persist()
        await refresh(key)
    }

    func discoverTenants(identityId: String) async throws -> [DiscoveredTenant] {
        guard let identity = self.identity(identityId) else { return [] }
        return try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: identity.homeTenantId, scopes: ArmScopes.all) { @Sendable in
            try await discovery.discoverTenants(identity: identity)
        }
    }

    func trackTenants(identityId: String, tenants: [DiscoveredTenant]) async {
        let generation = configGeneration
        for t in tenants {
            let key = TenantKey(identityId: identityId, tenantId: t.tenantId)
            guard tenant(key) == nil else { continue }
            state.upsertTenant(TenantContext(identityId: identityId, tenantId: t.tenantId, displayName: t.displayName, source: .discovered))
        }
        persist()
        let keys = tenants.map { TenantKey(identityId: identityId, tenantId: $0.tenantId) }
        await withTaskGroup(of: Void.self) { group in
            for key in keys {
                group.addTask {
                    guard await self.configGeneration == generation else { return }
                    await self.refresh(key)
                }
            }
        }
    }

    func removeTenant(_ key: TenantKey) {
        declinedTenants.remove(key)
        state.removeTenant(key)
        roles[key] = nil
        active = active.filter { $0.key.tenantKey != key }
        dropApprovals { $0 == key }
        dropPolicies { $0.tenantKey == key }
        persist()
    }

    func retryDiscovery(_ key: TenantKey) async {
        declinedTenants.remove(key)
        guard var t = self.tenant(key) else { return }
        t.discoveryMode = .automatic
        t.lastDiscoveryError = nil
        t.azureUnavailableReason = nil
        t.groupsUnavailableReason = nil
        t.entraActivation = nil
        dropPolicies { $0.tenantKey == key }
        state.upsertTenant(t)
        persist()
        await refresh(key)
    }

    // MARK: Refresh

    /// Called when the menu bar panel opens. Runs in its own task so closing the panel cannot cancel it.
    func panelOpened() {
        // A stale filter must never survive a reopen: the panel always opens showing everything.
        searchQuery = ""
        guard bootstrapped, isOnline, !identities.isEmpty, Date().timeIntervalSince(lastRefresh) > 30 else { return }
        Task { await self.refreshAll() }
    }

    func refreshAll(userInitiated: Bool = false) async {
        if userInitiated { declinedTenants.removeAll() }
        lastRefresh = .now
        let generation = configGeneration
        let keys = state.tenants.map(\.id)
        await withTaskGroup(of: Void.self) { group in
            for key in keys {
                group.addTask {
                    guard await self.configGeneration == generation else { return }
                    await self.refresh(key)
                }
            }
        }
        guard generation == configGeneration else { return }
        pruneSeenApprovals()
    }

    func refresh(_ key: TenantKey, kinds requestedKinds: Set<RoleScopeKind>? = nil) async {
        let generation = configGeneration
        guard let identity = self.identity(key.identityId), var tenant = self.tenant(key) else { return }
        guard !busy.contains(key) else { return }
        busy.insert(key)
        defer { busy.remove(key) }
        // A kinds-restricted refresh re-reads only some providers, so it must not clear errors it cannot re-earn.
        if requestedKinds == nil { tenantErrors[key] = nil }

        // Runs a provider read; prompts at most once per tenant per session, never for a tenant that was removed meanwhile.
        func acquire<T: Sendable>(_ scopes: [String], _ op: @Sendable @escaping () async throws -> T) async throws -> T {
            guard self.tenant(key) != nil else { throw CancellationError() }
            if declinedTenants.contains(key) { return try await op() }
            do {
                return try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: scopes, operation: op)
            } catch let e as PIMError where Self.isDeclinedSignIn(e) {
                guard self.tenant(key) != nil else { throw CancellationError() }
                declinedTenants.insert(key)
                throw PIMError.signInDeclined
            }
        }

        // A tenant with no Azure at all is not worth a request per refresh; the breaker is cleared by Retry discovery.
        // A first-party sign-in (Azure CLI / PowerShell) has no Graph PIM scopes at all, so its
        // Entra reads fail every time: skip that provider outright and keep the account Azure-only.
        // Every kind this identity can read at all, before any per-refresh restriction.
        var eligibleKinds: [RoleScopeKind] = tenant.azureUnavailableReason == nil ? [.entraDirectory, .azureResource, .group] : [.entraDirectory, .group]
        if !identity.signInMethod.isPreauthorisedForEntraActivation { eligibleKinds.removeAll { $0 == .entraDirectory || $0 == .group } }
        if tenant.groupsUnavailableReason != nil { eligibleKinds.removeAll { $0 == .group } }
        // A kinds-restricted refresh reads only these providers.
        var kinds = eligibleKinds
        if let requestedKinds { kinds.removeAll { !requestedKinds.contains($0) } }
        let providers: [any PIMProvider] = kinds.compactMap { coordinator.provider(for: $0) }
        // Start from what we already know so a transient failure never blanks a provider's rows, and so a
        // kinds-restricted refresh keeps the rows of the kinds it does not re-read. Kinds this identity
        // cannot read at all (a first-party account's Entra rows, a consent-refused tenant's groups) still drop.
        var discoveredByKind: [RoleScopeKind: [EligibleRole]] = Dictionary(grouping: roles(for: key).filter { $0.source == .discovered && eligibleKinds.contains($0.key.scope.kind) }) { $0.key.scope.kind }
        var errors: [String] = []
        var consentBlocked = tenant.discoveryMode != .automatic
        var kindsWithActive: Set<RoleScopeKind> = []
        var current: [ActiveAssignment] = []
        var azureOff = false

        for provider in providers {
            let kind = provider.kind
            let isEntra = kind == .entraDirectory
            let isAzure = kind == .azureResource
            let isGroup = kind == .group
            let tenantSnapshot = tenant
            // Eligible roles. Entra honours the consent block; ARM consent is user-consentable.
            if !(isEntra && consentBlocked) {
                do {
                    let found = try await acquire(provider.scopes) { @Sendable in
                        try await provider.eligibleRoles(identity: identity, tenant: tenantSnapshot)
                    }
                    let withPolicies = await applyPolicies(to: found, identity: identity)
                    guard generation == configGeneration else { return }
                    discoveredByKind[kind] = withPolicies
                    if isEntra, let support = await probeEntraActivation(identity: identity, tenantId: key.tenantId),
                       support != tenant.entraActivation {
                        guard generation == configGeneration, self.tenant(key) != nil else { return }
                        tenant.entraActivation = support
                        state.upsertTenant(tenant)
                        persist()
                    }
                } catch PIMError.consentRequired where isEntra {
                    guard generation == configGeneration else { return }
                    discoveredByKind[kind] = []
                    consentBlocked = true
                    tenant.discoveryMode = .manualRoles
                    tenant.lastDiscoveryError = "Role discovery not permitted in this tenant. Configure known roles or ask an admin to consent."
                    state.upsertTenant(tenant)
                    persist()
                } catch let error as PIMError where isGroup && Self.isGroupConsentFailure(error) {
                    guard generation == configGeneration, self.tenant(key) != nil else { return }
                    discoveredByKind[kind] = []
                    latchGroupsOff(&tenant)
                    // Claim the kind so the tail filter drops group rows we read before consent was refused.
                    kindsWithActive.insert(kind)
                    continue   // skip the active read for this provider
                } catch is CancellationError {
                    return
                } catch PIMError.signInDeclined, PIMError.interactionRequired {
                    if !errors.contains(PIMError.signInDeclined.userMessage) { errors.append(PIMError.signInDeclined.userMessage) }
                } catch let error as PIMError where isAzure && Self.azureUnavailableReason(for: error) != nil {
                    guard generation == configGeneration else { return }
                    azureOff = true
                    tenant.azureUnavailableReason = Self.azureUnavailableReason(for: error)
                    state.upsertTenant(tenant)
                    persist()
                } catch {
                    errors.append("\(Self.label(kind)): \((error as? PIMError)?.userMessage ?? error.localizedDescription)")
                }
            }
            // Active assignments. Whatever Azure rows we already know stay as they are.
            if isAzure && azureOff { continue }
            do {
                let snapshot = tenant
                let found: [ActiveAssignment]
                if isEntra && consentBlocked {
                    found = try await provider.activeAssignments(identity: identity, tenant: snapshot)
                } else {
                    found = try await acquire(provider.scopes) { @Sendable in
                        try await provider.activeAssignments(identity: identity, tenant: snapshot)
                    }
                }
                guard generation == configGeneration else { return }
                current += found
                kindsWithActive.insert(kind)
            } catch PIMError.interactionRequired where isEntra && consentBlocked {
            } catch PIMError.consentRequired where isEntra && consentBlocked {
            } catch PIMError.signInDeclined, PIMError.interactionRequired {
                if !errors.contains(PIMError.signInDeclined.userMessage) { errors.append(PIMError.signInDeclined.userMessage) }
            } catch is CancellationError {
                return
            } catch let error as PIMError where isGroup && Self.isGroupConsentFailure(error) {
                guard generation == configGeneration, self.tenant(key) != nil else { return }
                latchGroupsOff(&tenant)
                // Claim the kind so the tail filter drops any group rows read before consent was refused.
                kindsWithActive.insert(kind)
            } catch let error as PIMError where isAzure && Self.azureUnavailableReason(for: error) != nil {
                guard generation == configGeneration else { return }
                azureOff = true
                tenant.azureUnavailableReason = Self.azureUnavailableReason(for: error)
                state.upsertTenant(tenant)
                persist()
            } catch {
                errors.append("\(Self.label(kind)): \((error as? PIMError)?.userMessage ?? error.localizedDescription)")
            }
        }

        // Approvals are opportunistic: a 403, a missing consent or a network failure leaves the
        // previous list for that tenant and kind untouched and surfaces nothing to the user.
        // `kinds` already excludes Entra and groups for a first-party sign-in, so those accounts
        // read only the Azure approvals.
        var readApprovals: [RoleScopeKind: [ApprovalRequest]] = [:]
        for kind in kinds where !(kind == .azureResource && azureOff) {
            guard let provider = approvalProviders[kind] else { continue }
            let snapshot = tenant
            if let found = try? await acquire(provider.scopes, { @Sendable in
                try await provider.pendingApprovals(identity: identity, tenant: snapshot)
            }) {
                readApprovals[kind] = found
            }
        }
        guard generation == configGeneration else { return }
        if self.tenant(key) != nil, !readApprovals.isEmpty {
            for (kind, list) in readApprovals { approvals[key, default: [:]][kind] = list }
            await announceNewApprovals()
        }

        guard generation == configGeneration else { return }
        let manual = ManualRoleSource.eligibleRoles(from: state.manualRoles, tenantKey: key).map { role -> EligibleRole in
            var r = role
            if let policy = policyCache[r.key] { r.policy = policy }
            return r
        }
        let discovered = discoveredByKind.values.flatMap { $0 }.sorted { $0.displayName < $1.displayName }
        roles[key] = ManualRoleSource.merge(discovered: discovered, manual: manual)
        // Replace only the kinds we successfully re-read; keep the rest.
        active = active.filter { !($0.key.tenantKey == key && kindsWithActive.contains($0.key.scope.kind)) }
        for a in current { active[a.roleKey] = a }
        if !errors.isEmpty { tenantErrors[key] = errors.joined(separator: " · ") }
        await rescheduleNotifications()
    }

    /// Notifies once per request the user has not seen before. Only adds here: a single tenant's
    /// refresh knows nothing about the tenants that have not been read yet this launch, so pruning
    /// here would forget their ids and re-notify them a moment later. `refreshAll` prunes instead.
    private func announceNewApprovals() async {
        let all = allApprovals
        for r in ApprovalDiff.newRequests(previousIds: settings.seenApprovalIds, current: all) {
            await notifier.notify(title: "Approval requested",
                                  body: "\(r.targetName) for \(r.requesterName), \(approvalTenantName(r))")
        }
        settings.seenApprovalIds.formUnion(all.map(\.id))
    }

    /// After a full sweep every tenant's list is current, so the seen set can be cut back to what is
    /// still pending; a request that is withdrawn and comes back notifies again.
    private func pruneSeenApprovals() {
        settings.seenApprovalIds.formIntersection(allApprovals.map(\.id))
    }

    // MARK: Approvals

    /// Sends one Approve or Deny. Returns true when the service accepted it; the caller closes its
    /// sheet on true and shows `approvalErrors[request.id]` on false.
    func decide(_ request: ApprovalRequest, approve: Bool, justification: String) async -> Bool {
        guard !decisionInFlight.contains(request.id) else { return false }
        guard let identity = self.identity(request.tenantKey.identityId) else {
            approvalErrors[request.id] = "That account is no longer signed in."
            return false
        }
        guard let provider = approvalProviders[request.kind] else {
            approvalErrors[request.id] = "This request cannot be decided from Elevate."
            return false
        }
        let generation = configGeneration
        decisionInFlight.insert(request.id)
        defer { decisionInFlight.remove(request.id) }
        do {
            try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: request.tenantKey.tenantId,
                                           scopes: provider.scopes) { @Sendable in
                try await provider.decide(request, approve: approve, justification: justification, identity: identity)
            }
            guard generation == configGeneration else { return false }
            // Drop the row now; the follow-up refresh re-lists it if a further approval stage remains.
            approvals[request.tenantKey]?[request.kind]?.removeAll { $0.id == request.id }
            approvalErrors[request.id] = nil
            settings.lastApprovalJustification = justification
            Task { await self.refresh(request.tenantKey, kinds: [request.kind]) }
            return true
        } catch {
            guard generation == configGeneration else { return false }
            approvalErrors[request.id] = (error as? PIMError)?.userMessage ?? error.localizedDescription
            return false
        }
    }

    /// Latches PIM for Groups off for this tenant. Callers must already have checked
    /// `generation == configGeneration` and that the tenant still exists.
    private func latchGroupsOff(_ tenant: inout TenantContext) {
        tenant.groupsUnavailableReason = "PIM for Groups is not permitted in this tenant until an admin consents to the group permissions."
        state.upsertTenant(tenant)
        persist()
    }

    /// A group read refused for permissions: the tenant has no PIM for Groups we can reach.
    /// A first-party or custom app without the group scopes answers 403 rather than a consent challenge.
    /// `GraphTransport.send` substitutes `.forbidden` for a 403 on non-own-app identities, so a missing
    /// group permission surfaces here as `.forbidden` rather than `.consentRequired`.
    private static func isGroupConsentFailure(_ error: PIMError) -> Bool {
        switch error {
        case .consentRequired, .forbidden: true
        default: false
        }
    }

    /// Spec §1: a tenant without Azure access shows no Azure rows and no error. These failures
    /// from an Azure *list* call mean "there is nothing here for this user", not "this refresh failed".
    private static func azureUnavailableReason(for error: PIMError) -> String? {
        switch error {
        case .policyViolation: "No Azure access in this tenant"
        case .interactionRequired, .consentRequired: "Azure sign-in was not completed"
        default: nil
        }
    }

    private static func label(_ kind: RoleScopeKind) -> String {
        switch kind {
        case .entraDirectory: "Entra"
        case .azureResource: "Azure"
        case .group: "Groups"
        }
    }

    /// Fills in policies, reusing the cache and fetching at most four at a time.
    /// A failed fetch keeps the cached policy when there is one, otherwise the manual default.
    private func applyPolicies(to roles: [EligibleRole], identity: Identity) async -> [EligibleRole] {
        let generation = configGeneration
        var pending = roles.filter { policyCache[$0.key] == nil }
        // Pull the next role with an available provider, skipping any that have none.
        func nextRunnable() -> (EligibleRole, any PIMProvider)? {
            while let role = pending.popLast() {
                if let provider = coordinator.provider(for: role.key.scope.kind) { return (role, provider) }
            }
            return nil
        }
        let fetched = await withTaskGroup(of: (RoleKey, RolePolicy?).self) { group in
            for _ in 0..<4 {
                guard let (role, provider) = nextRunnable() else { break }
                group.addTask { (role.key, try? await provider.policy(for: role, identity: identity)) }
            }
            var out: [RoleKey: RolePolicy] = [:]
            for await (roleKey, policy) in group {
                if let policy { out[roleKey] = policy }
                if let (role, provider) = nextRunnable() {
                    group.addTask { (role.key, try? await provider.policy(for: role, identity: identity)) }
                }
            }
            return out
        }
        guard generation == configGeneration else { return roles }
        for (roleKey, policy) in fetched { policyCache[roleKey] = policy }
        return roles.map { role in
            var r = role
            r.policy = policyCache[role.key] ?? .manualDefault
            return r
        }
        .sorted { $0.displayName < $1.displayName }
    }

    private func rescheduleNotifications() async {
        var names: [RoleKey: String] = [:]
        for list in roles.values { for r in list { names[r.key] = r.displayName } }
        var tenantNames: [TenantKey: String] = [:]
        for t in state.tenants { tenantNames[t.id] = t.displayName }
        await notifier.reschedule(assignments: Array(active.values), names: names, tenantNames: tenantNames)
    }

    // MARK: Entra activation capability

    /// Why Entra roles in this tenant are view-only, or nil when they can be activated.
    /// A tenant that has been probed answers from its token; otherwise the sign-in method's
    /// known capabilities decide, so a first-party account is view-only from the moment it is added.
    func entraViewOnlyReason(for key: TenantKey) -> String? {
        guard let identity = identity(key.identityId) else { return nil }
        if let support = tenant(key)?.entraActivation { return support.reason }
        return identity.signInMethod.entraViewOnlyReason
    }

    /// Whether the account may activate this role in its tenant. Azure and group roles always may;
    /// Entra roles depend on `entraViewOnlyReason(for:)`.
    func canActivate(_ key: RoleKey) -> Bool {
        key.scope.kind != .entraDirectory || entraViewOnlyReason(for: key.tenantKey) == nil
    }

    /// Reads the cached Graph token's `scp` claim. Silent only: never prompts, and nil when no
    /// token is at hand or it hides its scopes, in which case the caller keeps what it knew.
    private func probeEntraActivation(identity: Identity, tenantId: String) async -> EntraActivationSupport? {
        guard let token = try? await tokens.accessToken(identity: identity, tenantId: tenantId, scopes: GraphScopes.all),
              let permitted = AccessTokenClaims.permitsEntraActivation(token) else { return nil }
        if permitted { return .supported }
        let reason = identity.signInMethod.entraViewOnlyReason
            ?? "The app registration used for this account (\(identity.signInMethod.displayName)) was not granted RoleAssignmentSchedule.ReadWrite.Directory in this tenant, so it supports activation of Azure resource roles only here; Entra roles are listed but cannot be activated."
        return .unsupported(reason: reason)
    }

    // MARK: Activation

    /// Activates the requests. Roles that are already active are deactivated first so "Extend" works.
    /// Returns the coordinator's outcomes so callers can report on them; empty when the run was
    /// abandoned because the configuration changed under it.
    @discardableResult
    func activate(_ requests: [ActivationRequest]) async -> [ActivationOutcome] {
        let generation = configGeneration
        for r in requests { progress[r.roleKey] = nil; inFlight.insert(r.roleKey) }
        defer { for r in requests { inFlight.remove(r.roleKey) } }
        var deactivated: Set<RoleKey> = []
        var skipped: Set<RoleKey> = []
        for r in requests {
            guard let existing = active[r.roleKey], existing.status == .active,
                  let identity = self.identity(r.roleKey.identityId) else { continue }
            do {
                try await coordinator.deactivate(existing, identity: identity)
                guard generation == configGeneration else { return [] }
                active[r.roleKey] = nil
                deactivated.insert(r.roleKey)
            } catch {
                guard generation == configGeneration else { return [] }
                let message = (error as? PIMError)?.userMessage ?? error.localizedDescription
                tenantErrors[r.roleKey.tenantKey] = message
                progress[r.roleKey] = .failed(.unexpected(status: 0, body: "Could not deactivate before re-activating: \(message)"))
                skipped.insert(r.roleKey)
            }
        }
        let attempted = requests.filter { !skipped.contains($0.roleKey) }
        let outcomes = await coordinator.activate(attempted, identities: state.identities) { outcome in
            // This hop can land after the final loop below, which is authoritative; only fill a gap.
            Task { @MainActor in
                guard generation == self.configGeneration else { return }
                if self.progress[outcome.roleKey] == nil { self.progress[outcome.roleKey] = outcome.result }
            }
        }
        guard generation == configGeneration else { return [] }
        var consentBlocked: Set<TenantKey> = []
        for outcome in outcomes {
            progress[outcome.roleKey] = outcome.result
            guard let request = attempted.first(where: { $0.roleKey == outcome.roleKey }) else { continue }
            switch outcome.result {
            case .activated(let a), .pendingApproval(let a), .scheduled(let a):
                // A manual Azure role is keyed by role name; the provider answers with the resolved
                // definition id, so move the stored role, its memory and its row onto that key.
                if a.roleKey != request.roleKey { rekey(from: request.roleKey, to: a.roleKey) }
                active[a.roleKey] = a
                state.remember(roleKey: a.roleKey, justification: request.justification, duration: request.duration)
            case .failed(let error):
                active[request.roleKey] = nil
                if deactivated.contains(request.roleKey) {
                    progress[request.roleKey] = .failed(.unexpected(status: 0, body: "Deactivated, but re-activation failed: \(error.userMessage)"))
                }
                // Spec §8 step 4: an activation refused for consent puts the tenant in manual mode.
                if error == .consentRequired { consentBlocked.insert(request.roleKey.tenantKey) }
                // A first-party sign-in refused for the write scope: the tenant's Entra rows become view-only.
                if case .forbidden(let message) = error, request.roleKey.scope.kind == .entraDirectory,
                   var t = self.tenant(request.roleKey.tenantKey) {
                    t.entraActivation = .unsupported(reason: message)
                    state.upsertTenant(t)
                }
            }
        }
        for tenantKey in consentBlocked {
            guard var t = self.tenant(tenantKey) else { continue }
            t.discoveryMode = .manualRoles
            // Only an own-app registration can be consented to; the first-party client ids are
            // Microsoft's and are not ours to request consent for.
            let method = state.identities.first { $0.id == tenantKey.identityId }?.signInMethod ?? .ownApp
            t.lastDiscoveryError = method == .ownApp
                ? "Activation not permitted in this tenant until an admin consents."
                : "Activation not permitted in this tenant for the \(method.displayName); try your own app registration instead."
            state.upsertTenant(t)
        }
        persist()
        let changedGroupTenants = Set(outcomes.compactMap { o -> TenantKey? in
            guard o.roleKey.scope.kind == .group, case .activated = o.result else { return nil }
            return o.roleKey.tenantKey
        })
        refreshRolesAfterGroupChange(changedGroupTenants)
        selectMode = false
        await rescheduleNotifications()
        // Runs independently so a slow policy fetch cannot hold the activation spinner; it only
        // updates cached policy/role data afterwards, never `inFlight`.
        Task { await self.learnPoliciesForManualRoles(outcomes) }
        return outcomes
    }

    // MARK: Quick activate

    /// Option-click path. Returns false when the dialog is needed; the caller opens it.
    /// Nothing opens a dialog while offline or while the same role is already being activated:
    /// returning true says the click was handled.
    func quickActivate(_ key: RoleKey) async -> Bool {
        guard isOnline, !inFlight.contains(key) else { return true }
        guard let role = role(for: key) else { return false }
        guard case .ready(let requests) = QuickActivate.decide(role: role, memory: remembered(for: key)) else { return false }
        let outcomes = await activate(requests)
        await notifyOutcome(title: role.displayName, outcomes: outcomes, attempted: requests.count)
        return true
    }

    func quickRun(profileId: UUID) async -> Bool {
        guard isOnline else { return true }
        guard let profile = state.profile(id: profileId) else { return false }
        let items = plan(for: profileId)
        guard case .ready(let requests) = QuickActivate.decide(items: items, justification: profile.lastJustification) else { return false }
        guard !requests.contains(where: { inFlight.contains($0.roleKey) }) else { return true }
        let outcomes = await runProfile(id: profileId, items: items, justification: requests.first?.justification ?? "", ticket: nil)
        await notifyOutcome(title: profile.name, outcomes: outcomes, attempted: requests.count)
        return true
    }

    /// Reports on the outcomes the activation returned rather than on `progress`, which a later
    /// refresh may already have cleared or moved onto a rekeyed role. `attempted` is the number of
    /// requests the run set out to make, so an empty outcome list can be told apart from having had
    /// nothing to do at all.
    private func notifyOutcome(title: String, outcomes: [ActivationOutcome], attempted: Int) async {
        await notifier.notify(title: title,
                              body: ActivationSummary.body(outcomes: outcomes, attempted: attempted,
                                                           names: summaryName(for:)))
    }

    /// Moves a manual role from the key the user typed to the key the provider resolved it to.
    /// The row keeps `source: .manual` and its detail; `ManualRoleSource.merge` drops it once
    /// discovery returns the same scope and name.
    private func rekey(from old: RoleKey, to new: RoleKey) {
        let tenantKey = old.tenantKey
        guard tenantKey == new.tenantKey else { return }
        if let i = state.manualRoles.firstIndex(where: { $0.tenantKey == tenantKey && $0.scope == old.scope }) {
            state.manualRoles[i].scope = new.scope
        }
        if let remembered = state.memory(for: old) {
            state.memory.removeAll { $0.roleKey == old }
            state.remember(roleKey: new, justification: remembered.justification, duration: remembered.lastDuration)
        }
        if let i = roles[tenantKey]?.firstIndex(where: { $0.key == old }), let row = roles[tenantKey]?[i] {
            roles[tenantKey]?[i] = EligibleRole(key: new, displayName: row.displayName, detail: row.detail,
                                                source: row.source, policy: row.policy)
        }
        if let policy = policyCache[old] { policyCache[old] = nil; policyCache[new] = policy }
        if let p = progress[old] { progress[old] = nil; progress[new] = p }
        active[old] = nil
        for i in state.profiles.indices {
            for j in state.profiles[i].entries.indices where state.profiles[i].entries[j].roleKey == old {
                state.profiles[i].entries[j].roleKey = new
            }
        }
    }

    /// A manual role has no policy until Entra accepts an activation; that is the moment we can read one.
    private func learnPoliciesForManualRoles(_ outcomes: [ActivationOutcome]) async {
        let generation = configGeneration
        for outcome in outcomes {
            guard case .activated = outcome.result,
                  let role = self.role(for: outcome.roleKey), role.source == .manual,
                  let identity = self.identity(outcome.roleKey.identityId),
                  let provider = coordinator.provider(for: outcome.roleKey.scope.kind),
                  let policy = try? await provider.policy(for: role, identity: identity) else { continue }
            guard generation == configGeneration else { return }
            policyCache[outcome.roleKey] = policy
            let tenantKey = outcome.roleKey.tenantKey
            guard let index = roles[tenantKey]?.firstIndex(where: { $0.key == outcome.roleKey }) else { continue }
            roles[tenantKey]?[index].policy = policy
        }
    }

    /// Withdraws a request that is still waiting for an approver.
    func cancelPending(_ key: RoleKey) async {
        let generation = configGeneration
        inFlight.insert(key)
        defer { inFlight.remove(key) }
        guard let a = active[key], let identity = self.identity(key.identityId) else { return }
        do {
            try await coordinator.cancelPendingRequest(a, identity: identity)
            guard generation == configGeneration else { return }
            active[key] = nil
            await rescheduleNotifications()
        } catch {
            guard generation == configGeneration else { return }
            tenantErrors[key.tenantKey] = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }

    func deactivate(_ key: RoleKey) async {
        let generation = configGeneration
        inFlight.insert(key)
        defer { inFlight.remove(key) }
        guard let a = active[key], let identity = self.identity(key.identityId) else { return }
        do {
            // A booked-ahead activation is still only a request: withdraw it. Providers differ on
            // whether cancel is accepted once the schedule exists, so fall back to a deactivation.
            if a.status == .scheduled {
                do {
                    try await coordinator.cancelPendingRequest(a, identity: identity)
                } catch {
                    try await coordinator.deactivate(a, identity: identity)
                }
            } else {
                try await coordinator.deactivate(a, identity: identity)
            }
            guard generation == configGeneration else { return }
            active[key] = nil
            if key.scope.kind == .group { refreshRolesAfterGroupChange([key.tenantKey]) }
            await rescheduleNotifications()
        } catch {
            guard generation == configGeneration else { return }
            tenantErrors[key.tenantKey] = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }

    /// Membership changes carry roles with them; give the directory a moment, then re-read roles only.
    private func refreshRolesAfterGroupChange(_ tenantKeys: Set<TenantKey>) {
        guard !tenantKeys.isEmpty else { return }
        let generation = configGeneration
        Task {
            try? await Task.sleep(for: .seconds(5))
            guard generation == self.configGeneration else { return }
            // A refresh already in flight would make `refresh` return without re-reading; wait it out once.
            if tenantKeys.contains(where: { self.busy.contains($0) }) {
                try? await Task.sleep(for: .seconds(5))
                guard generation == self.configGeneration else { return }
            }
            for key in tenantKeys where self.tenant(key) != nil {
                await self.refresh(key, kinds: [.entraDirectory, .azureResource])
            }
        }
    }

    func clearProgress(_ keys: [RoleKey]) { for k in keys { progress[k] = nil } }

    func toggleSelection(_ key: RoleKey) {
        guard canActivate(key) else { return }
        if selection.contains(key) { selection.remove(key) } else { selection.insert(key) }
    }

    // MARK: Profiles

    var profiles: [ActivationProfile] { state.profiles }
    var editingProfileId: UUID?
    /// Bumped each time the user asks to run a profile. `WindowGroup(for:)` refocuses an existing
    /// window instead of re-running `.onAppear`, so the Run sheet re-plans on a change here —
    /// and only then, never merely because the window regained focus.
    private(set) var runRequests: [UUID: Int] = [:]

    func requestRun(_ id: UUID) { runRequests[id, default: 0] += 1 }

    private func orderedKeys(_ keys: [RoleKey]) -> [RoleKey] {
        // Stable, readable order: by account, then tenant, then kind, then name.
        keys.sorted { a, b in
            let ia = identity(a.identityId)?.upn ?? "", ib = identity(b.identityId)?.upn ?? ""
            if ia != ib { return ia < ib }
            let ta = tenant(a.tenantKey)?.displayName ?? "", tb = tenant(b.tenantKey)?.displayName ?? ""
            if ta != tb { return ta < tb }
            if a.scope.kind != b.scope.kind { return a.scope.kind.rawValue < b.scope.kind.rawValue }
            return (role(for: a)?.displayName ?? "") < (role(for: b)?.displayName ?? "")
        }
    }

    @discardableResult
    func saveProfile(name: String, keys: [RoleKey]) -> ActivationProfile {
        let entries = orderedKeys(keys).map { ActivationProfile.Entry(roleKey: $0, lastDuration: remembered(for: $0)?.lastDuration) }
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let profile = ActivationProfile(name: trimmed.isEmpty ? "Untitled profile" : trimmed, entries: entries)
        state.upsertProfile(profile); persist()
        return profile
    }

    func updateProfile(id: UUID, keys: [RoleKey]) {
        guard var p = state.profile(id: id) else { return }
        let old = Dictionary(p.entries.map { ($0.roleKey, $0) }, uniquingKeysWith: { _, b in b })
        p.entries = orderedKeys(keys).map { old[$0] ?? ActivationProfile.Entry(roleKey: $0, lastDuration: remembered(for: $0)?.lastDuration) }
        state.upsertProfile(p); persist()
    }

    func renameProfile(id: UUID, name: String) {
        guard var p = state.profile(id: id) else { return }
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        p.name = trimmed; state.upsertProfile(p); persist()
    }

    func deleteProfile(id: UUID) {
        state.removeProfile(id: id)
        persist()
        // The global shortcut pointed at a profile that no longer exists; drop the binding with it.
        if settings.hotKeyProfileId == id {
            settings.hotKeyProfileId = nil
            applyHotKey()
        }
    }
    func moveProfile(fromOffsets: IndexSet, toOffset: Int) { state.moveProfile(fromOffsets: fromOffsets, toOffset: toOffset); persist() }

    /// Edit = reopen the selection. The bulk bar offers "Update profile" while `editingProfileId` is set.
    func beginEditing(profileId: UUID) {
        guard let p = state.profile(id: profileId) else { return }
        selectMode = true
        selection = Set(p.entries.map(\.roleKey))
        editingProfileId = profileId
    }

    func plan(for profileId: UUID) -> [ProfilePlanItem] {
        guard let p = state.profile(id: profileId) else { return [] }
        var rolesByKey: [RoleKey: EligibleRole] = [:]
        for list in roles.values { for r in list { rolesByKey[r.key] = r } }
        let memoryByKey = Dictionary(state.memory.map { ($0.roleKey, $0) }, uniquingKeysWith: { _, b in b })
        // A tenant counts as loaded once it has a roles entry and is not mid-refresh; entries of a
        // tenant that is not loaded yet plan as `.notLoaded` rather than a wrong "not eligible".
        let loadedTenants = Set(roles.keys).subtracting(busy)
        return ProfilePlanner.plan(p, roles: rolesByKey, active: active, memory: memoryByKey,
                                   loadedTenants: loadedTenants)
    }

    /// Activates the plan's `.activate` items, then remembers the reason and each duration on the profile.
    /// Returns the outcomes of that activation so callers can report on them.
    @discardableResult
    func runProfile(id: UUID, items: [ProfilePlanItem], justification: String, ticket: TicketInfo?,
                    startDateTime: Date? = nil) async -> [ActivationOutcome] {
        let requests = items.filter { $0.disposition == .activate }.map {
            ActivationRequest(roleKey: $0.roleKey, duration: $0.duration, justification: justification, ticket: ticket,
                              authenticationContext: $0.role?.policy.authenticationContext,
                              startDateTime: startDateTime)
        }
        let outcomes = requests.isEmpty ? [] : await activate(requests)
        guard var p = state.profile(id: id) else { return outcomes }
        p.lastJustification = justification
        for item in items where item.disposition != .notEligible && item.disposition != .notLoaded {
            // `rekey` may have moved a manual Azure entry onto the key the provider resolved, so the
            // planned key can be gone. Fall back to the one active key of the same tenant carrying the
            // same display name; ambiguity means we leave the remembered duration alone.
            var index = p.entries.firstIndex { $0.roleKey == item.roleKey }
            if index == nil, active[item.roleKey] == nil, let name = item.role?.displayName {
                let candidates = active.keys.filter { candidate in
                    candidate.tenantKey == item.roleKey.tenantKey && role(for: candidate)?.displayName == name
                        && p.entries.contains { $0.roleKey == candidate }
                }
                if candidates.count == 1 { index = p.entries.firstIndex { $0.roleKey == candidates[0] } }
            }
            if let i = index { p.entries[i].lastDuration = item.duration }
        }
        state.upsertProfile(p); persist()
        return outcomes
    }

    // MARK: Manual roles

    func setManualRoles(_ manual: [ManualRole], for key: TenantKey) {
        state.manualRoles.removeAll { $0.tenantKey == key }
        state.manualRoles += manual
        persist()
        let discovered = roles(for: key).filter { $0.source == .discovered }
        roles[key] = ManualRoleSource.merge(discovered: discovered, manual: ManualRoleSource.eligibleRoles(from: manual, tenantKey: key))
    }

    func manualRoles(for key: TenantKey) -> [ManualRole] { state.manualRoles.filter { $0.tenantKey == key } }
}
