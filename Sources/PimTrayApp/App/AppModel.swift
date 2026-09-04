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
    var selectMode = false { didSet { if !selectMode { selection.removeAll() } } }
    var selection: Set<RoleKey> = []
    var fatalError: String?
    var pendingExtend: RoleKey?

    private let tokens: any TokenProviding
    private let coordinator: ActivationCoordinator
    private let discovery: TenantDiscovery
    private let store: AppStateStore
    private let notifier: any ExpiryNotifying
    private var refreshTimer: Task<Void, Never>?

    init(tokens: any TokenProviding, http: any HTTPClient, store: AppStateStore, notifier: any ExpiryNotifying) {
        self.tokens = tokens
        self.store = store
        self.notifier = notifier
        coordinator = ActivationCoordinator(providers: [EntraDirectoryProvider(http: http, tokens: tokens), AzureResourceProvider(), GroupProvider()], tokens: tokens)
        discovery = TenantDiscovery(http: http, tokens: tokens)
    }

    /// Production wiring. Errors surface through `fatalError` so the panel can show them.
    static func live() -> AppModel {
        let anchor = AuthAnchorWindow()
        do {
            let config = try AppConfig.load()
            let tokens = try MSALTokenProvider(clientId: config.clientId, redirectUri: config.redirectUri, anchor: anchor)
            let notifier = ExpiryNotifier()
            let model = AppModel(tokens: tokens, http: URLSessionHTTPClient(), store: AppStateStore(), notifier: notifier)
            notifier.onExtend = { [weak model] key in model?.pendingExtend = key }
            return model
        } catch {
            let model = AppModel(tokens: UnavailableTokenProvider(), http: URLSessionHTTPClient(), store: AppStateStore(), notifier: NoopNotifier())
            model.fatalError = error.localizedDescription
            return model
        }
    }

    // MARK: Derived

    var identities: [Identity] { state.identities }
    var activeCount: Int { active.values.filter { $0.status == .active }.count }
    func tenants(for identityId: String) -> [TenantContext] { state.tenants(for: identityId) }
    func roles(for tenantKey: TenantKey) -> [EligibleRole] { roles[tenantKey] ?? [] }
    func role(for key: RoleKey) -> EligibleRole? { roles[key.tenantKey]?.first { $0.key == key } }
    func assignment(for key: RoleKey) -> ActiveAssignment? { active[key] }
    func remembered(for key: RoleKey) -> RoleMemory? { state.memory(for: key) }
    func identity(_ id: String) -> Identity? { state.identities.first { $0.id == id } }
    func tenant(_ key: TenantKey) -> TenantContext? { state.tenants.first { $0.id == key } }

    func adminConsentURL(tenantId: String) -> URL? {
        guard let config = try? AppConfig.load() else { return nil }
        return URL(string: "https://login.microsoftonline.com/\(tenantId)/adminconsent?client_id=\(config.clientId)")
    }

    // MARK: Lifecycle

    func bootstrap() async {
        if let loaded = try? await store.load() { state = loaded }
        // Reconcile with MSAL's cache: drop identities MSAL no longer knows.
        if let known = try? await tokens.identities() {
            let ids = Set(known.map(\.id))
            for identity in state.identities where !ids.contains(identity.id) { state.removeIdentity(identity.id) }
        }
        persist()
        await refreshAll()
        startTimer()
    }

    private func startTimer() {
        refreshTimer?.cancel()
        refreshTimer = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(60))
                guard let self, self.activeCount > 0 else { continue }
                await self.refreshAll()
            }
        }
    }

    private func persist() {
        let snapshot = state
        Task { try? await store.save(snapshot) }
    }

    // MARK: Accounts

    func addAccount() async {
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
            fatalError = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }

    func signOut(_ identity: Identity) {
        Task {
            try? await tokens.signOut(identity)
            state.removeIdentity(identity.id)
            for key in roles.keys where key.identityId == identity.id { roles[key] = nil }
            active = active.filter { $0.key.identityId != identity.id }
            persist()
        }
    }

    // MARK: Tenants

    func addTenant(identityId: String, domainOrId: String) async throws {
        guard let identity = self.identity(identityId) else { throw PIMError.unexpected(status: 0, body: "Unknown identity") }
        let tenantId = try await discovery.resolveTenantId(domainOrId: domainOrId)
        let key = TenantKey(identityId: identityId, tenantId: tenantId)
        guard tenant(key) == nil else { return }
        let name = (try? await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: tenantId, scopes: [GraphScopes.userRead]) { @Sendable in
            try await discovery.tenantDisplayName(identity: identity, tenantId: tenantId)
        }) ?? domainOrId
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
        for t in tenants {
            let key = TenantKey(identityId: identityId, tenantId: t.tenantId)
            guard tenant(key) == nil else { continue }
            state.upsertTenant(TenantContext(identityId: identityId, tenantId: t.tenantId, displayName: t.displayName, source: .discovered))
        }
        persist()
        let keys = tenants.map { TenantKey(identityId: identityId, tenantId: $0.tenantId) }
        await withTaskGroup(of: Void.self) { group in
            for key in keys {
                group.addTask { await self.refresh(key) }
            }
        }
    }

    func removeTenant(_ key: TenantKey) {
        state.removeTenant(key)
        roles[key] = nil
        active = active.filter { $0.key.tenantKey != key }
        persist()
    }

    func retryDiscovery(_ key: TenantKey) async {
        guard var t = self.tenant(key) else { return }
        t.discoveryMode = .automatic
        t.lastDiscoveryError = nil
        state.upsertTenant(t)
        persist()
        await refresh(key)
    }

    // MARK: Refresh

    func refreshAll() async {
        let keys = state.tenants.map(\.id)
        await withTaskGroup(of: Void.self) { group in
            for key in keys {
                group.addTask { await self.refresh(key) }
            }
        }
    }

    func refresh(_ key: TenantKey) async {
        guard let identity = self.identity(key.identityId), var tenant = self.tenant(key),
              let provider = coordinator.provider(for: .entraDirectory) else { return }
        busy.insert(key)
        defer { busy.remove(key) }
        tenantErrors[key] = nil

        var discovered: [EligibleRole] = []
        if tenant.discoveryMode == .automatic {
            let tenantSnapshot = tenant
            do {
                discovered = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
                    try await provider.eligibleRoles(identity: identity, tenant: tenantSnapshot)
                }
                discovered = await withTaskGroup(of: EligibleRole.self) { group in
                    for role in discovered {
                        group.addTask {
                            var r = role
                            r.policy = (try? await provider.policy(for: role, identity: identity)) ?? .manualDefault
                            return r
                        }
                    }
                    var out: [EligibleRole] = []
                    for await r in group { out.append(r) }
                    return out.sorted { $0.displayName < $1.displayName }
                }
            } catch PIMError.consentRequired {
                tenant.discoveryMode = .manualRoles
                tenant.lastDiscoveryError = "Role discovery not permitted in this tenant. Configure known roles or ask an admin to consent."
                state.upsertTenant(tenant)
                persist()
            } catch {
                tenantErrors[key] = (error as? PIMError)?.userMessage ?? error.localizedDescription
            }
        }
        let manual = ManualRoleSource.eligibleRoles(from: state.manualRoles, tenantKey: key)
        roles[key] = ManualRoleSource.merge(discovered: discovered, manual: manual)

        do {
            let tenantSnapshot = tenant
            let current = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
                try await provider.activeAssignments(identity: identity, tenant: tenantSnapshot)
            }
            active = active.filter { $0.key.tenantKey != key }
            for a in current { active[a.roleKey] = a }
            await rescheduleNotifications()
        } catch {
            if tenantErrors[key] == nil { tenantErrors[key] = (error as? PIMError)?.userMessage ?? error.localizedDescription }
        }
    }

    private func rescheduleNotifications() async {
        var names: [RoleKey: String] = [:]
        for list in roles.values { for r in list { names[r.key] = r.displayName } }
        await notifier.reschedule(assignments: Array(active.values), names: names)
    }

    // MARK: Activation

    /// Activates the requests. Roles that are already active are deactivated first so "Extend" works.
    func activate(_ requests: [ActivationRequest]) async {
        for r in requests { progress[r.roleKey] = nil }
        for r in requests {
            if let existing = active[r.roleKey], existing.status == .active, let identity = self.identity(r.roleKey.identityId) {
                try? await coordinator.deactivate(existing, identity: identity)
            }
        }
        let outcomes = await coordinator.activate(requests, identities: state.identities) { outcome in
            Task { @MainActor in self.progress[outcome.roleKey] = outcome.result }
        }
        for outcome in outcomes {
            progress[outcome.roleKey] = outcome.result
            guard let request = requests.first(where: { $0.roleKey == outcome.roleKey }) else { continue }
            switch outcome.result {
            case .activated(let a):
                active[a.roleKey] = a
                state.remember(roleKey: request.roleKey, justification: request.justification, duration: request.duration)
            case .pendingApproval:
                active[request.roleKey] = ActiveAssignment(roleKey: request.roleKey, assignmentId: nil, startDateTime: .now, endDateTime: nil, status: .pendingApproval)
                state.remember(roleKey: request.roleKey, justification: request.justification, duration: request.duration)
            case .failed:
                break
            }
        }
        persist()
        selectMode = false
        await rescheduleNotifications()
    }

    func deactivate(_ key: RoleKey) async {
        guard let a = active[key], let identity = self.identity(key.identityId) else { return }
        do {
            try await coordinator.deactivate(a, identity: identity)
            active[key] = nil
            await rescheduleNotifications()
        } catch {
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
