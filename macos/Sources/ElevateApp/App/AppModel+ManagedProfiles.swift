import Foundation
import ElevateCore

/// Profiles an organization publishes (design §7): the inline `ManagedProfiles` document merged
/// with the one fetched from `ManagedProfilesUrl`, resolved against the accounts actually signed
/// in. They are recomputed from observable state and never written to `state.json`.
@MainActor
extension AppModel {
    /// The published document is fetched at most once a day; the cached copy stands in between.
    static let managedProfilesInterval: TimeInterval = 24 * 3600
    /// The published-profile fetch runs on the bootstrap path, so it cannot be left to
    /// `URLSession`'s 60 s default: a slow or hanging endpoint would delay the first role refresh
    /// by a minute. Bounded here instead; the cached set (if any) stands in on timeout. Matches the
    /// CLI's `ElevateSession.FetchTimeout`.
    static let managedProfilesFetchTimeout: TimeInterval = 5
    /// The cache file, kept next to `state.json`.
    static let managedProfilesCacheFile = "managed-profiles.json"

    // MARK: The set

    /// Parses the inline document once, at init: managed preferences do not change under a
    /// running app, and a rejected document becomes a single warning rather than an error.
    // internal: called from `AppModel.init`
    func loadInlineProfiles() {
        guard let document = managed.managedProfilesDocument else { return }
        do {
            inlineProfileSet = try ManagedProfileSet.parse(document)
        } catch {
            inlineProfileSet = .empty
            inlineProfileWarning = "ManagedProfiles: \(Self.message(for: error))"
        }
    }

    /// The inline document merged with the fetched one, the fetched profile winning on a shared id.
    var managedProfileSet: ManagedProfileSet {
        guard let fetched = fetchedProfileSet else { return inlineProfileSet }
        return inlineProfileSet.merged(with: fetched)
    }

    /// Where the fetched document is cached.
    var managedProfilesCacheURL: URL { profileFetcher.cacheURL }

    /// When the published document was last fetched successfully; nil until the first one lands.
    var managedProfilesFetchedAt: Date? { settings.managedProfilesFetchedAt }

    // MARK: Resolution

    /// The published profiles against the accounts, tenants and eligible roles known right now,
    /// plus what could not be resolved. Recomputed on every read, so it always follows the state
    /// the views observe.
    var managedProfileResolution: ManagedProfileResolution {
        guard !managedProfileSet.profiles.isEmpty else { return ManagedProfileResolution() }
        var rolesByKey: [RoleKey: EligibleRole] = [:]
        for list in roles.values { for role in list { rolesByKey[role.key] = role } }
        return ManagedProfileResolver.resolve(managedProfileSet, tenantIds: managedTenantIds,
                                              tenants: state.tenants, roles: rolesByKey)
    }

    var managedProfiles: [ActivationProfile] { managedProfileResolution.profiles }

    /// Everything Settings shows about the published profiles: the document that could not be
    /// parsed, the fetch that failed, and what the resolution could not match.
    var managedProfileWarnings: [String] {
        [inlineProfileWarning, managedProfileFetchWarning].compactMap { $0 } + managedProfileResolution.warnings
    }

    /// Whether this id belongs to a published profile — true even when it resolved to nothing,
    /// so an unresolved managed profile is never mistaken for a user profile and edited.
    func isManagedProfile(_ id: UUID) -> Bool {
        managedProfileSet.profiles.contains { $0.profileId == id }
    }

    // MARK: Fetching

    /// Loads the cached document, then fetches a fresh one when it is due (or `force` says so).
    /// A failed fetch keeps whatever was cached and leaves a warning behind: a published profile
    /// that momentarily cannot be downloaded should not disappear from the panel.
    func refreshManagedProfiles(force: Bool = false) async {
        guard let url = managed.managedProfilesUrl else { return }
        // The cache first, so even a launch whose fetch fails has the last good copy.
        if fetchedProfileSet == nil, let cached = profileFetcher.cached() { fetchedProfileSet = cached }
        guard isOnline else { return }
        if !force {
            if let fetchedAt = managedProfilesFetchedAt,
               abs(Date().timeIntervalSince(fetchedAt)) < Self.managedProfilesInterval { return }
        }
        do {
            fetchedProfileSet = try await fetchManagedProfiles(from: url)
            settings.managedProfilesFetchedAt = .now
            managedProfileFetchWarning = nil
            // The document just downloaded may name a tenant by a domain nobody has looked up yet;
            // without this its roles would stay unresolved until the next launch.
            if hasUnresolvedManagedTenantEntries { await resolveManagedTenants() }
        } catch is CancellationError {
            return
        } catch {
            let message = Self.message(for: error)
            managedProfileFetchWarning = "ManagedProfilesUrl: \(message)"
            logError("Managed profiles: \(message)")
        }
    }

    /// Races the fetch against a deadline and cancels the loser, so a black-holed
    /// `ManagedProfilesUrl` costs the launch five seconds rather than a minute. The timeout is
    /// reported as an ordinary fetch failure, so the cached set stands and one warning is left.
    private func fetchManagedProfiles(from url: URL) async throws -> ManagedProfileSet {
        let fetcher = profileFetcher
        let timeout = Self.managedProfilesFetchTimeout
        return try await withThrowingTaskGroup(of: ManagedProfileSet?.self) { group in
            group.addTask { try await fetcher.fetch(from: url) }
            group.addTask {
                try await Task.sleep(for: .seconds(timeout))
                return nil
            }
            defer { group.cancelAll() }
            guard let first = try await group.next() else { throw CancellationError() }
            guard let set = first else {
                throw ManagedProfileError.invalid("timed out after \(Int(timeout)) s")
            }
            return set
        }
    }

    /// The user-facing text of whatever went wrong, whichever error type carried it.
    private static func message(for error: Error) -> String {
        if case let ManagedProfileError.invalid(message) = error { return message }
        return (error as? PIMError)?.userMessage ?? error.localizedDescription
    }
}
