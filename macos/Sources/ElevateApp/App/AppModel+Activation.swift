import Foundation
import ElevateCore

@MainActor
extension AppModel {
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
    // internal for AppModel+Refresh
    func probeEntraActivation(identity: Identity, tenantId: String) async -> EntraActivationSupport? {
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
                logError("\(summaryName(for: r.roleKey)): could not deactivate before re-activating: \(message)")
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
                logError("\(summaryName(for: request.roleKey)): \(error.userMessage)")
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

    // MARK: Activation helpers

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

    // MARK: Deactivation

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
            let message = (error as? PIMError)?.userMessage ?? error.localizedDescription
            tenantErrors[key.tenantKey] = message
            logError("\(summaryName(for: key)): \(message)")
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
            let message = (error as? PIMError)?.userMessage ?? error.localizedDescription
            tenantErrors[key.tenantKey] = message
            logError("\(summaryName(for: key)): \(message)")
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
}
