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
    var selectMode = false { didSet { if !selectMode { selection.removeAll() } } }
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
    var startupError: String?
    /// Transient, dismissible message (failed sign-in, unreadable state file). Never blocks the panel.
    var notice: String?
    private var bootstrapped = false
    private var lastRefresh: Date = .distantPast
    var pendingExtend: RoleKey?

    let settings: AppSettings
    private(set) var tokens: any TokenProviding
    private(set) var coordinator: ActivationCoordinator
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
        coordinator = ActivationCoordinator(providers: [EntraDirectoryProvider(http: http, tokens: tokens), AzureResourceProvider(http: http, tokens: tokens), GroupProvider()], tokens: tokens)
        discovery = TenantDiscovery(http: http, tokens: tokens)
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
        pendingExtend = nil; selectMode = false
        persist()
        settings.clientId = id
        msal = replacement
        let composite = CompositeTokenProvider(msal: replacement, loopback: loopback)
        tokens = composite
        coordinator = ActivationCoordinator(providers: [EntraDirectoryProvider(http: http, tokens: composite), AzureResourceProvider(http: http, tokens: composite), GroupProvider()], tokens: composite)
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
        dropPolicies { $0.identityId == identityId }
    }

    // MARK: Derived

    var identities: [Identity] { state.identities }
    /// Accounts a client-id change would sign out; the first-party ones are unaffected.
    var ownAppIdentityCount: Int { state.identities.count { $0.signInMethod == .ownApp } }
    /// False when the machine has no usable network path; reads and requests are held back.
    var isOnline: Bool { network.isOnline }
    var activeCount: Int { active.values.filter { $0.status == .active }.count }
    func tenants(for identityId: String) -> [TenantContext] { state.tenants(for: identityId) }
    func roles(for tenantKey: TenantKey) -> [EligibleRole] { roles[tenantKey] ?? [] }
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
            URLQueryItem(name: "scope", value: GraphScopes.all.joined(separator: " ")),
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
    }

    private func startTimer() {
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
        dropPolicies { $0.tenantKey == key }
        persist()
    }

    func retryDiscovery(_ key: TenantKey) async {
        declinedTenants.remove(key)
        guard var t = self.tenant(key) else { return }
        t.discoveryMode = .automatic
        t.lastDiscoveryError = nil
        t.azureUnavailableReason = nil
        t.entraActivation = nil
        dropPolicies { $0.tenantKey == key }
        state.upsertTenant(t)
        persist()
        await refresh(key)
    }

    // MARK: Refresh

    /// Called when the menu bar panel opens. Runs in its own task so closing the panel cannot cancel it.
    func panelOpened() {
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
    }

    func refresh(_ key: TenantKey) async {
        let generation = configGeneration
        guard let identity = self.identity(key.identityId), var tenant = self.tenant(key) else { return }
        guard !busy.contains(key) else { return }
        busy.insert(key)
        defer { busy.remove(key) }
        tenantErrors[key] = nil

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
        var kinds: [RoleScopeKind] = tenant.azureUnavailableReason == nil ? [.entraDirectory, .azureResource] : [.entraDirectory]
        if !identity.signInMethod.isPreauthorisedForEntraActivation { kinds.removeAll { $0 == .entraDirectory } }
        let providers: [any PIMProvider] = kinds.compactMap { coordinator.provider(for: $0) }
        // Start from what we already know so a transient failure never blanks a provider's rows.
        var discoveredByKind: [RoleScopeKind: [EligibleRole]] = Dictionary(grouping: roles(for: key).filter { $0.source == .discovered && kinds.contains($0.key.scope.kind) }) { $0.key.scope.kind }
        var errors: [String] = []
        var consentBlocked = tenant.discoveryMode != .automatic
        var kindsWithActive: Set<RoleScopeKind> = []
        var current: [ActiveAssignment] = []
        var azureOff = false

        for provider in providers {
            let kind = provider.kind
            let isEntra = kind == .entraDirectory
            let isAzure = kind == .azureResource
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
    func activate(_ requests: [ActivationRequest]) async {
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
                guard generation == configGeneration else { return }
                active[r.roleKey] = nil
                deactivated.insert(r.roleKey)
            } catch {
                guard generation == configGeneration else { return }
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
        guard generation == configGeneration else { return }
        var consentBlocked: Set<TenantKey> = []
        for outcome in outcomes {
            progress[outcome.roleKey] = outcome.result
            guard let request = attempted.first(where: { $0.roleKey == outcome.roleKey }) else { continue }
            switch outcome.result {
            case .activated(let a), .pendingApproval(let a):
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
        selectMode = false
        await rescheduleNotifications()
        // Runs independently so a slow policy fetch cannot hold the activation spinner; it only
        // updates cached policy/role data afterwards, never `inFlight`.
        Task { await self.learnPoliciesForManualRoles(outcomes) }
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
            try await coordinator.deactivate(a, identity: identity)
            guard generation == configGeneration else { return }
            active[key] = nil
            await rescheduleNotifications()
        } catch {
            guard generation == configGeneration else { return }
            tenantErrors[key.tenantKey] = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }

    func clearProgress(_ keys: [RoleKey]) { for k in keys { progress[k] = nil } }

    func toggleSelection(_ key: RoleKey) {
        guard canActivate(key) else { return }
        if selection.contains(key) { selection.remove(key) } else { selection.insert(key) }
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
