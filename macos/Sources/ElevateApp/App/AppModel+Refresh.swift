import Foundation
import ElevateCore

@MainActor
extension AppModel {
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
        for kind in kinds where !(kind == .azureResource && azureOff) && !(kind == .entraDirectory && consentBlocked) {
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
        if !errors.isEmpty {
            let message = errors.joined(separator: " · ")
            tenantErrors[key] = message
            logError("\(tenant.displayName): \(message)")
        }
        await rescheduleNotifications()
    }

    // MARK: Refresh helpers

    private static func isDeclinedSignIn(_ e: PIMError) -> Bool {
        switch e {
        case .interactionRequired: true
        case .network(let m): m.localizedCaseInsensitiveContains("cancel") || m.localizedCaseInsensitiveContains("timed out")
        default: false
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

    // MARK: Notifications

    // internal for AppModel (applyClientId) and AppModel+Activation
    func rescheduleNotifications() async {
        var names: [RoleKey: String] = [:]
        for list in roles.values { for r in list { names[r.key] = r.displayName } }
        var tenantNames: [TenantKey: String] = [:]
        for t in state.tenants { tenantNames[t.id] = t.displayName }
        await notifier.reschedule(assignments: Array(active.values), names: names, tenantNames: tenantNames)
    }
}
