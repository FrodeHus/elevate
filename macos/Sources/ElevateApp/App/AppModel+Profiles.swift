import Foundation
import ElevateCore

@MainActor
extension AppModel {
    // MARK: Profiles

    var profiles: [ActivationProfile] { state.profiles }
    func profile(id: UUID) -> ActivationProfile? { state.profile(id: id) }

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

    /// Profiles shown as chips in the panel, in list order.
    var pinnedProfiles: [ActivationProfile] { state.pinnedProfiles }

    /// Pins or unpins a profile. Returns false, changing nothing, when the pinned row is full
    /// (`ProfilePins.limit`); the caller says so instead of silently ignoring the click.
    @discardableResult
    func setPinned(id: UUID, _ pinned: Bool) -> Bool {
        guard state.setPinned(id: id, pinned) else { return false }
        persist()
        return true
    }

    // MARK: In-place editing (the Profiles window)

    /// An empty profile to fill in the Profiles window; the caller selects it there.
    @discardableResult
    func newProfile() -> ActivationProfile {
        let p = ActivationProfile(name: "New profile", entries: [])
        state.upsertProfile(p); persist()
        return p
    }

    /// Adds roles to a profile, keeping the entries it already has (and their durations) and the
    /// stable order `saveProfile` uses. Keys already present are ignored.
    func addProfileEntries(id: UUID, keys: [RoleKey]) {
        guard let p = state.profile(id: id) else { return }
        let existing = Set(p.entries.map(\.roleKey))
        let added = keys.filter { !existing.contains($0) }
        guard !added.isEmpty else { return }
        updateProfile(id: id, keys: p.entries.map(\.roleKey) + added)
    }

    func removeProfileEntry(id: UUID, key: RoleKey) {
        guard var p = state.profile(id: id) else { return }
        p.entries.removeAll { $0.roleKey == key }
        state.upsertProfile(p); persist()
    }

    /// The duration the next run proposes for one entry; nil falls back to memory or the policy.
    func setProfileEntryDuration(id: UUID, key: RoleKey, duration: Duration?) {
        guard var p = state.profile(id: id), let i = p.entries.firstIndex(where: { $0.roleKey == key }) else { return }
        p.entries[i].lastDuration = duration
        state.upsertProfile(p); persist()
    }

    /// Which profile the global shortcut runs; nil unbinds it. The key itself is recorded in Settings.
    func setHotKeyProfile(_ id: UUID?) {
        settings.hotKeyProfileId = id
        applyHotKey()
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
