import Foundation

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

    public init(id: UUID = UUID(), name: String, entries: [Entry], lastJustification: String? = nil) {
        self.id = id
        self.name = name
        self.entries = entries
        self.lastJustification = lastJustification
    }
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
