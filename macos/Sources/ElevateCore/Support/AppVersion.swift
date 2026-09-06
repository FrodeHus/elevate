import Foundation

/// A semantic version parsed from a tag or bundle version string.
///
/// Accepts an optional leading `v`, a required `major.minor` (patch defaults
/// to 0 when omitted), and ignores build metadata introduced by a `+`
/// (e.g. `1.2.3+45`).
public struct AppVersion: Comparable, Equatable, Sendable {
    public let major: Int
    public let minor: Int
    public let patch: Int

    public init(major: Int, minor: Int, patch: Int) {
        self.major = major
        self.minor = minor
        self.patch = patch
    }

    public init?(_ text: String) {
        var s = Substring(text)
        if s.first == "v" || s.first == "V" {
            s = s.dropFirst()
        }
        // Drop build metadata after "+".
        if let plusIndex = s.firstIndex(of: "+") {
            s = s[s.startIndex..<plusIndex]
        }
        guard !s.isEmpty else { return nil }

        let parts = s.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 2 || parts.count == 3 else { return nil }

        guard let major = Int(parts[0]), let minor = Int(parts[1]) else { return nil }
        let patch: Int
        if parts.count == 3 {
            guard let p = Int(parts[2]) else { return nil }
            patch = p
        } else {
            patch = 0
        }

        self.major = major
        self.minor = minor
        self.patch = patch
    }

    public static func < (lhs: AppVersion, rhs: AppVersion) -> Bool {
        (lhs.major, lhs.minor, lhs.patch) < (rhs.major, rhs.minor, rhs.patch)
    }

    /// True when `latestTag` parses to a version greater than `current`.
    /// False when either fails to parse, or when they are equal or `latestTag` is older.
    public static func isNewer(latestTag: String, current: String) -> Bool {
        guard let latest = AppVersion(latestTag), let current = AppVersion(current) else {
            return false
        }
        return latest > current
    }
}
