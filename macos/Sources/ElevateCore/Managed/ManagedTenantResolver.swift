import Foundation

/// The outcome of resolving managed tenant entries: the entries that became tenant ids, keyed by
/// the entry exactly as it was configured, and the entries that could not be resolved, in order.
public struct ManagedTenantResolution: Sendable, Hashable {
    /// Entry (as given) → lower-cased tenant id.
    public var ids: [String: String]
    /// Entries no tenant id could be found for, in the order they were given.
    public var unresolved: [String]

    public init(ids: [String: String] = [:], unresolved: [String] = []) {
        self.ids = ids
        self.unresolved = unresolved
    }
}

/// Turns managed tenant entries — tenant GUIDs or verified domains — into tenant ids, using the
/// unauthenticated OpenID configuration endpoint for domains. Successful lookups are cached for
/// the life of the resolver, so a domain is fetched once no matter how often it is asked for; a
/// failure is not cached, so a transient one can be retried on the next call.
public actor ManagedTenantResolver {
    private let http: any HTTPClient
    private var cache: [String: String] = [:]

    public init(http: any HTTPClient) {
        self.http = http
    }

    public func resolve(_ entries: [String]) async -> ManagedTenantResolution {
        var result = ManagedTenantResolution()
        for entry in entries {
            // Keyed by the entry as given so every spelling resolves, cached by the lower-cased
            // one so two spellings of the same domain cost a single lookup.
            let key = entry.lowercased()
            if let cached = cache[key] {
                result.ids[entry] = cached
                continue
            }
            guard let id = try? await TenantDiscovery.tenantId(domainOrId: entry, http: http) else {
                if !result.unresolved.contains(entry) { result.unresolved.append(entry) }
                continue
            }
            cache[key] = id
            result.ids[entry] = id
        }
        return result
    }
}
