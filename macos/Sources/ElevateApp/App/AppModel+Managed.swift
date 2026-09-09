import Foundation
import ElevateCore

/// What an organization's managed configuration permits, applied to the running app: which
/// sign-in methods may be used, which tenants may be tracked, and which ones must be.
@MainActor
extension AppModel {
    // MARK: Sign-in methods

    /// Whether `method` may be used at all under the managed configuration. A method that is not
    /// permitted is never offered, never signed in with, and never signed in again with.
    func isMethodAllowed(_ method: SignInMethod) -> Bool { ManagedPolicy.isAllowed(method, by: managed) }

    static let disallowedMethodNotice = "That sign-in method is not permitted by your organization"
    static let disallowedMethodCaption = "Sign-in method no longer permitted by your organization"

    // MARK: Tenants

    /// Whether `tenantId` may be tracked. Unrestricted while `allowedTenantIds` is nil. A pinned
    /// tenant is always allowed, even outside the allow-list: the organization pinning it says it
    /// wants it tracked, so a manual add must not refuse it and a bulk track must not drop it.
    func isTenantAllowed(_ tenantId: String) -> Bool {
        if pinnedTenantIds.contains(where: { $0.caseInsensitiveCompare(tenantId) == .orderedSame }) {
            return true
        }
        return ManagedPolicy.isTenantAllowed(tenantId, allowedIds: allowedTenantIds)
    }

    /// Whether the organization pins this tenant, in which case the user cannot remove it.
    func isPinnedTenant(_ key: TenantKey) -> Bool {
        pinnedTenantIds.contains { $0.caseInsensitiveCompare(key.tenantId) == .orderedSame }
    }

    static let pinnedTenantNotice = "This tenant is pinned by your organization"
    static let disallowedTenantMessage = "This tenant is not permitted by your organization"

    /// Every managed tenant entry that needs a tenant id, in configured order without duplicates.
    /// One place on purpose: the tenants named by managed profiles join the list here.
    private var managedTenantEntries: [String] {
        var seen = Set<String>()
        var entries: [String] = []
        let profileTenants = managedProfileSet.profiles.flatMap { $0.roles.map(\.tenant) }
        for entry in (managed.allowedTenants ?? []) + managed.pinnedTenants + profileTenants where !seen.contains(entry) {
            seen.insert(entry)
            entries.append(entry)
        }
        return entries
    }

    /// True while a managed tenant entry still needs a lookup — a domain named by a profile that
    /// arrived with the fetched document, say. GUID entries are their own id and never count.
    var hasUnresolvedManagedTenantEntries: Bool {
        managedTenantEntries.contains { UUID(uuidString: $0) == nil && managedTenantIds[$0] == nil }
    }

    /// Resolves the managed tenant entries to tenant ids, then applies them: tenants the
    /// organization does not permit are dropped (the accounts' home tenants excepted — that is
    /// where the account lives), and pinned tenants are tracked for every account. Never throws:
    /// an entry that could not be resolved becomes a warning and restricts nothing.
    /// Resolving needs the network — the entries are looked up over HTTP — so offline it does
    /// nothing at all and leaves `managedTenantsResolved` false; applying half a configuration
    /// would drop tenants on a guess. `AppModel.watchNetwork()` runs it once the path comes back.
    func resolveManagedTenants() async {
        let entries = managedTenantEntries
        guard !entries.isEmpty else {
            managedTenantsResolved = true
            return
        }
        guard isOnline else {
            managedTenantsResolved = false
            return
        }
        let resolution = await tenantResolver.resolve(entries)
        managedTenantIds = resolution.ids

        let allowedEntries = managed.allowedTenants ?? []
        var warnings: [String] = []
        for entry in allowedEntries where resolution.unresolved.contains(entry) {
            warnings.append("AllowedTenants: could not resolve '\(entry)'")
        }
        for entry in managed.pinnedTenants where resolution.unresolved.contains(entry) {
            warnings.append("PinnedTenants: could not resolve '\(entry)'")
        }
        managedTenantWarnings = warnings

        // A restriction is applied whole or not at all: with one entry unresolved the app cannot
        // tell whether a tenant is on the list, and locking the user out on a guess is worse than
        // not applying it. Pinning is per entry, so the ones that resolved still apply.
        if allowedEntries.isEmpty || allowedEntries.contains(where: { resolution.ids[$0] == nil }) {
            allowedTenantIds = nil
        } else {
            allowedTenantIds = Set(allowedEntries.compactMap { resolution.ids[$0] })
        }
        pinnedTenantIds = managed.pinnedTenants.compactMap { resolution.ids[$0] }

        managedTenantsResolved = true
        removeDisallowedTenants()
        for identity in identities { await trackPinnedTenants(identityId: identity.id) }
    }

    /// Drops tracked tenants the organization no longer permits, telling the user which ones went.
    /// A pinned tenant is never dropped even when it is off the allow-list: `PinnedTenants` says
    /// the organization wants it tracked, and removing it here would only have it re-added below,
    /// wiping its roles and approvals on every launch.
    private func removeDisallowedTenants() {
        guard allowedTenantIds != nil else { return }
        let removed = state.tenants.filter {
            $0.source != .home && !isTenantAllowed($0.tenantId)
        }
        guard !removed.isEmpty else { return }
        for tenant in removed { forgetTenant(tenant.id) }
        logError("Removed tenants not permitted by your organization: \(removed.map(\.displayName).joined(separator: ", "))")
        persist()
    }

    /// Tracks every pinned tenant for `identityId` that is not tracked already, then reads it.
    /// Called for every account after the managed tenants resolve, and for a new account once its
    /// home tenant is in place.
    func trackPinnedTenants(identityId: String) async {
        guard !pinnedTenantIds.isEmpty, let identity = self.identity(identityId) else { return }
        let generation = configGeneration
        let discovery = self.discovery
        for tenantId in pinnedTenantIds {
            let key = TenantKey(identityId: identityId, tenantId: tenantId)
            guard tenant(key) == nil else { continue }
            // Without a Graph name the entry as the organization wrote it is the best label there is.
            let entry = managed.pinnedTenants.first { managedTenantIds[$0] == tenantId } ?? tenantId
            let name = (try? await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: tenantId,
                                                        scopes: [GraphScopes.userRead]) { @Sendable in
                try await discovery.tenantDisplayName(identity: identity, tenantId: tenantId)
            }) ?? entry
            guard generation == configGeneration, tenant(key) == nil else { continue }
            state.upsertTenant(TenantContext(identityId: identityId, tenantId: tenantId, displayName: name, source: .discovered))
            persist()
            await refresh(key)
        }
    }
}
