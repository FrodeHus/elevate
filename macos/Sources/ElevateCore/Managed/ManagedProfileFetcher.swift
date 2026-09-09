import Foundation

/// Downloads the managed profile document an organization publishes over HTTPS and keeps the last
/// good body next to `state.json`, so a failed fetch falls back to what worked last (design §7.1).
public struct ManagedProfileFetcher: Sendable {
    /// A published document larger than this is refused rather than parsed.
    public static let maxBytes = 1_048_576

    private let http: any HTTPClient
    private let cacheURL: URL

    public init(http: any HTTPClient, cacheURL: URL) {
        self.http = http
        self.cacheURL = cacheURL
    }

    /// The cached document, or nil when there is no cache or it cannot be parsed.
    public func cached() -> ManagedProfileSet? {
        guard let data = try? Data(contentsOf: cacheURL) else { return nil }
        return try? ManagedProfileSet.parse(data)
    }

    /// Fetches, parses and caches the document. The cache is written only after a successful parse,
    /// so a bad response never replaces a good cached copy.
    public func fetch(from url: URL) async throws -> ManagedProfileSet {
        guard url.scheme?.lowercased() == "https" else {
            throw ManagedProfileError.invalid("managed profiles URL must be https")
        }
        let response = try await http.send(HTTPRequest(method: "GET", url: url, headers: ["Accept": "application/json"]))
        guard response.status == 200 else {
            throw ManagedProfileError.invalid("managed profiles fetch failed with HTTP \(response.status)")
        }
        guard response.body.count <= Self.maxBytes else {
            throw ManagedProfileError.invalid("managed profiles document is larger than 1 MB")
        }
        let set = try ManagedProfileSet.parse(response.body)
        write(response.body)
        return set
    }

    /// `.atomic` writes a temporary file and renames it, so a crash mid-write cannot truncate the
    /// cache. Best effort: a cache that cannot be written costs one fetch next launch, nothing more.
    private func write(_ data: Data) {
        try? FileManager.default.createDirectory(at: cacheURL.deletingLastPathComponent(),
                                                 withIntermediateDirectories: true)
        try? data.write(to: cacheURL, options: .atomic)
    }
}
