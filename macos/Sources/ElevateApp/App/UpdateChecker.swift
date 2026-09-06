import Foundation
import ElevateCore

/// Reads the latest published GitHub release for Elevate.
///
/// Deliberately tiny and stateless: it knows how to ask GitHub for one release and how to
/// read the two fields we care about. Deciding whether that release is newer than the running
/// build, and whether we should ask at all, belongs to `AppModel`.
struct UpdateChecker: Sendable {
    /// The unauthenticated "latest release" endpoint. It answers 404 until the first release
    /// is published, which is not an error — there is simply nothing to offer yet.
    static let latestReleaseURL = URL(string: "https://api.github.com/repos/FrodeHus/elevate/releases/latest")!

    let http: any HTTPClient
    var url: URL = UpdateChecker.latestReleaseURL

    private struct Release: Decodable {
        let tag_name: String
        let html_url: URL
    }

    /// The latest release's tag and web page, or nil when the repository has no releases yet.
    /// Throws on any other failure (offline, rate limited, unreadable body).
    func latest() async throws -> (tag: String, url: URL)? {
        let request = HTTPRequest(method: "GET", url: url,
                                  headers: ["Accept": "application/vnd.github+json",
                                            "User-Agent": "Elevate"])
        let response = try await http.send(request)
        if response.status == 404 { return nil }
        guard (200..<300).contains(response.status) else {
            throw PIMError.unexpected(status: response.status, body: response.bodyText)
        }
        do {
            let release = try JSONDecoder().decode(Release.self, from: response.body)
            return (release.tag_name, release.html_url)
        } catch {
            throw PIMError.unexpected(status: response.status, body: "Could not read the release information from GitHub")
        }
    }
}
