import Foundation
import Observation
import PimTrayCore

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
    private var refreshTimer: Task<Void, Never>?
    /// Policies are stable per role; fetching them again on every refresh is wasted quota.
    private var policyCache: [RoleKey: RolePolicy] = [:]
    /// Mutation order for saves, so a slow write cannot land after a newer one.
    private var saveGeneration: UInt64 = 0
    /// Bumped by `applyClientId`; in-flight refreshes started under an older client id
    /// check this before writing to state so they cannot repopulate what was just cleared.
    private var configGeneration = 0

    /// True once the app has a usable client id and a working token provider.
    var isConfigured: Bool { settings.isConfigured && !(tokens is UnavailableTokenProvider) }

    init(tokens: any TokenProviding, http: any HTTPClient, store: AppStateStore, notifier: any ExpiryNotifying,
         network: NetworkMonitor = NetworkMonitor(), settings: AppSettings = AppSettings(), anchor: AuthAnchorWindow? = nil) {
        self.tokens = tokens
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
        let tokens: any TokenProviding
        var initError: Error?
        if settings.isConfigured {
            do {
                tokens = try MSALTokenProvider(clientId: settings.clientId.trimmingCharacters(in: .whitespacesAndNewlines), redirectUri: AppSettings.redirectUri, anchor: anchor)
            } catch {
                tokens = UnavailableTokenProvider()
                initError = error
            }
        } else {
            tokens = UnavailableTokenProvider()
        }
        let model = AppModel(tokens: tokens, http: URLSessionHTTPClient(), store: AppStateStore(), notifier: notifier, settings: settings, anchor: anchor)
        if let initError {
            model.notice = "Could not initialise sign-in with the saved client ID: \((initError as? PIMError)?.userMessage ?? initError.localizedDescription). Check it in Settings."
        }
        notifier.onExtend = { [weak model] key in model?.pendingExtend = key }
        notifier.onAuthorizationDenied = { [weak model] in
            model?.notice = "Notifications are off for PimTray; enable them in System Settings to get expiry alerts."
        }
        return model
    }

    /// Saves a new client id. Because MSAL's token cache is per client, every account is signed out.
    func applyClientId(_ raw: String) throws {
        let id = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard AppSettings.isValidClientId(id) else { throw PIMError.unexpected(status: 0, body: "Enter the application (client) ID as a GUID") }
        guard let anchor else { throw PIMError.unexpected(status: 0, body: "Sign-in is unavailable in this build") }
        // Construct the new provider before mutating anything, so a throwing init leaves the
        // current client id, tokens and session state untouched.
        let msal = try MSALTokenProvider(clientId: id, redirectUri: AppSettings.redirectUri, anchor: anchor)
        let old = tokens
        let identity = state.identities
        Task { for identity in identity { try? await old.signOut(identity) } }
        configGeneration += 1
        state = AppState()
        roles = [:]; active = [:]; policyCache = [:]; tenantErrors = [:]; selection = []
        busy = []; inFlight = []; progress = [:]; lastRefresh = .distantPast
        persist()
        settings.clientId = id
        tokens = msal
        coordinator = ActivationCoordinator(providers: [EntraDirectoryProvider(http: http, tokens: msal), AzureResourceProvider(http: http, tokens: msal), GroupProvider()], tokens: msal)
        discovery = TenantDiscovery(http: http, tokens: msal)
        notice = nil
        startupError = nil
        Task { await self.notifier.reschedule(assignments: [], names: [:], tenantNames: [:]) }
    }

    // MARK: Derived

    var identities: [Identity] { state.identities }
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

    func adminConsentURL(tenantId: String) -> URL? {
        guard isConfigured else { return nil }
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
        // Reconcile with MSAL's cache: drop identities MSAL no longer knows.
        if let known = try? await tokens.identities() {
            let ids = Set(known.map(\.id))
            for identity in state.identities where !ids.contains(identity.id) { state.removeIdentity(identity.id) }
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

    func addAccount() async {
        guard isConfigured else { notice = "Complete initial setup first"; return }
        do {
            let identity = try await tokens.signIn()
            if !state.identities.contains(where: { $0.id == identity.id }) {
                state.identities.append(identity)
            }
            let homeKey = TenantKey(identityId: identity.id, tenantId: identity.homeTenantId)
            if tenant(homeKey) == nil {
                let name = (try? await discovery.tenantDisplayName(identity: identity, tenantId: identity.homeTenantId)) ?? identity.homeTenantId
                state.upsertTenant(TenantContext(identityId: identity.id, tenantId: identity.homeTenantId, displayName: name, source: .home))
            }
            persist()
            await refresh(homeKey)
        } catch {
            notice = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }

    func signOut(_ identity: Identity) {
        Task {
            try? await tokens.signOut(identity)
            state.removeIdentity(identity.id)
            for key in roles.keys where key.identityId == identity.id { roles[key] = nil }
            active = active.filter { $0.key.identityId != identity.id }
            dropPolicies { $0.identityId == identity.id }
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
        state.removeTenant(key)
        roles[key] = nil
        active = active.filter { $0.key.tenantKey != key }
        dropPolicies { $0.tenantKey == key }
        persist()
    }

    func retryDiscovery(_ key: TenantKey) async {
        guard var t = self.tenant(key) else { return }
        t.discoveryMode = .automatic
        t.lastDiscoveryError = nil
        t.azureUnavailableReason = nil
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

    func refreshAll() async {
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

        // A tenant with no Azure at all is not worth a request per refresh; the breaker is cleared by Retry discovery.
        let kinds: [RoleScopeKind] = tenant.azureUnavailableReason == nil ? [.entraDirectory, .azureResource] : [.entraDirectory]
        let providers: [any PIMProvider] = kinds.compactMap { coordinator.provider(for: $0) }
        // Start from what we already know so a transient failure never blanks a provider's rows.
        var discoveredByKind: [RoleScopeKind: [EligibleRole]] = Dictionary(grouping: roles(for: key).filter { $0.source == .discovered }) { $0.key.scope.kind }
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
                    let found = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
                        try await provider.eligibleRoles(identity: identity, tenant: tenantSnapshot)
                    }
                    let withPolicies = await applyPolicies(to: found, identity: identity)
                    guard generation == configGeneration else { return }
                    discoveredByKind[kind] = withPolicies
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
                    found = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
                        try await provider.activeAssignments(identity: identity, tenant: snapshot)
                    }
                }
                guard generation == configGeneration else { return }
                current += found
                kindsWithActive.insert(kind)
            } catch PIMError.interactionRequired where isEntra && consentBlocked {
            } catch PIMError.consentRequired where isEntra && consentBlocked {
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
                active[r.roleKey] = nil
                deactivated.insert(r.roleKey)
            } catch {
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
                if self.progress[outcome.roleKey] == nil { self.progress[outcome.roleKey] = outcome.result }
            }
        }
        guard generation == configGeneration else { return }
        var consentBlocked: Set<TenantKey> = []
        for outcome in outcomes {
            progress[outcome.roleKey] = outcome.result
            guard let request = attempted.first(where: { $0.roleKey == outcome.roleKey }) else { continue }
            switch outcome.result {
            case .activated(let a):
                active[a.roleKey] = a
                state.remember(roleKey: request.roleKey, justification: request.justification, duration: request.duration)
            case .pendingApproval(let a):
                active[a.roleKey] = a
                state.remember(roleKey: request.roleKey, justification: request.justification, duration: request.duration)
            case .failed(let error):
                active[request.roleKey] = nil
                if deactivated.contains(request.roleKey) {
                    progress[request.roleKey] = .failed(.unexpected(status: 0, body: "Deactivated, but re-activation failed: \(error.userMessage)"))
                }
                // Spec §8 step 4: an activation refused for consent puts the tenant in manual mode.
                if error == .consentRequired { consentBlocked.insert(request.roleKey.tenantKey) }
            }
        }
        for tenantKey in consentBlocked {
            guard var t = self.tenant(tenantKey) else { continue }
            t.discoveryMode = .manualRoles
            t.lastDiscoveryError = "Activation not permitted in this tenant until an admin consents."
            state.upsertTenant(t)
        }
        persist()
        selectMode = false
        await rescheduleNotifications()
        // Runs independently so a slow policy fetch cannot hold the activation spinner; it only
        // updates cached policy/role data afterwards, never `inFlight`.
        Task { await self.learnPoliciesForManualRoles(outcomes) }
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

/// Used only when configuration failed to load; every call fails with a clear message.
struct UnavailableTokenProvider: TokenProviding {
    func signIn() async throws -> Identity { throw PIMError.unexpected(status: 0, body: "Configuration missing") }
    func signOut(_ identity: Identity) async throws {}
    func identities() async throws -> [Identity] { [] }
    func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String { throw PIMError.unexpected(status: 0, body: "Configuration missing") }
    func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String { throw PIMError.unexpected(status: 0, body: "Configuration missing") }
}
