import Foundation

/// Where a profile came from. User profiles live in `state.json`; managed ones are published by
/// the organization and resolved at runtime, so they are never persisted.
public enum ProfileSource: String, Codable, Hashable, Sendable { case user, managed }

/// A named set of roles and groups activated together, across accounts and tenants.
public struct ActivationProfile: Codable, Hashable, Sendable, Identifiable {
    public struct Entry: Codable, Hashable, Sendable {
        public var roleKey: RoleKey
        /// Duration used on the last run of this entry; nil until the profile has run.
        public var lastDuration: Duration?
        public init(roleKey: RoleKey, lastDuration: Duration? = nil) {
            self.roleKey = roleKey
            self.lastDuration = lastDuration
        }
    }

    public var id: UUID
    public var name: String
    public var entries: [Entry]
    /// Reason entered on the last run; prefilled next time.
    public var lastJustification: String?
    /// Shown as a chip in the panel. At most `ProfilePins.limit` profiles are pinned at a time.
    public var pinned: Bool
    /// `.managed` profiles are published by the organization: read-only in the UI and never saved.
    public var source: ProfileSource

    public init(id: UUID = UUID(), name: String, entries: [Entry], lastJustification: String? = nil,
                pinned: Bool = false, source: ProfileSource = .user) {
        self.id = id
        self.name = name
        self.entries = entries
        self.lastJustification = lastJustification
        self.pinned = pinned
        self.source = source
    }

    private enum CodingKeys: String, CodingKey {
        case id, name, entries, lastJustification, pinned, source
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(UUID.self, forKey: .id)
        name = try c.decode(String.self, forKey: .name)
        entries = try c.decode([Entry].self, forKey: .entries)
        lastJustification = try c.decodeIfPresent(String.self, forKey: .lastJustification)
        pinned = try c.decodeIfPresent(Bool.self, forKey: .pinned) ?? false
        source = try c.decodeIfPresent(ProfileSource.self, forKey: .source) ?? .user
    }

    /// `pinned` and `source` are written only when they are not the default, so files from before
    /// those fields existed round-trip unchanged.
    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(id, forKey: .id)
        try c.encode(name, forKey: .name)
        try c.encode(entries, forKey: .entries)
        try c.encodeIfPresent(lastJustification, forKey: .lastJustification)
        if pinned { try c.encode(true, forKey: .pinned) }
        if source != .user { try c.encode(source, forKey: .source) }
    }
}

public enum ProfilePins {
    /// How many profiles may be pinned to the panel; one row of chips that never wraps.
    public static let limit = 4
}

public enum ProfileSummary {
    /// "3 roles · 1 group" style caption for a chip. Entra and Azure count as roles.
    public static func caption(entries: [ActivationProfile.Entry]) -> String {
        let groups = entries.filter { $0.roleKey.scope.kind == .group }.count
        let roles = entries.count - groups
        var parts: [String] = []
        if roles > 0 { parts.append("\(roles) role\(roles == 1 ? "" : "s")") }
        if groups > 0 { parts.append("\(groups) group\(groups == 1 ? "" : "s")") }
        return parts.isEmpty ? "empty" : parts.joined(separator: " · ")
    }
}
