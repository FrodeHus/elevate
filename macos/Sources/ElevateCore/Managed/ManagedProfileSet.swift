import CryptoKit
import Foundation

/// A managed profile document was rejected; the message names the profile and field.
public enum ManagedProfileError: Error, Hashable, Sendable {
    case invalid(String)
}

/// The organization-published profile document (design §7.1). Roles are named by tenant and role
/// rather than bound to account ids, so one document serves every machine; `ManagedProfileResolver`
/// turns the set into `ActivationProfile`s against the accounts that are actually signed in.
public struct ManagedProfileSet: Hashable, Sendable {
    /// One role in a managed profile. Which fields matter depends on `kind`.
    public struct Role: Hashable, Sendable {
        public var kind: RoleScopeKind
        /// A tenant id or a verified domain, exactly as configured.
        public var tenant: String
        /// Entra: display name or role template id. Azure: display name or role definition GUID.
        public var role: String?
        /// Azure only: the ARM scope, compared case-insensitively.
        public var scope: String?
        /// Entra only; defaults to "/".
        public var directoryScope: String
        /// Group only: display name or object id.
        public var group: String?
        /// Group only; defaults to `.member`.
        public var access: GroupAccess
        /// Proposed duration for the entry, capped by the role's policy as usual.
        public var duration: Duration?

        public init(kind: RoleScopeKind, tenant: String, role: String? = nil, scope: String? = nil,
                    directoryScope: String = "/", group: String? = nil, access: GroupAccess = .member,
                    duration: Duration? = nil) {
            self.kind = kind
            self.tenant = tenant
            self.role = role
            self.scope = scope
            self.directoryScope = directoryScope
            self.group = group
            self.access = access
            self.duration = duration
        }
    }

    /// One published profile. `id` is the administrator's slug; `profileId` is the UUID every
    /// machine derives from it, so a hot-key binding survives a reinstall.
    public struct Profile: Hashable, Sendable, Identifiable {
        public var id: String
        public var name: String
        public var reason: String?
        public var pinned: Bool
        public var roles: [Role]

        public init(id: String, name: String, reason: String? = nil, pinned: Bool = false, roles: [Role] = []) {
            self.id = id
            self.name = name
            self.reason = reason
            self.pinned = pinned
            self.roles = roles
        }

        public var profileId: UUID { ManagedProfileSet.profileId(slug: id) }
    }

    public var profiles: [Profile]

    public init(profiles: [Profile] = []) {
        self.profiles = profiles
    }

    public static let empty = ManagedProfileSet()

    /// The only document version this build understands.
    public static let version = 1

    /// RFC 4122's DNS namespace, so the derived ids are reproducible outside this app.
    private static let dnsNamespace: [UInt8] = [
        0x6b, 0xa7, 0xb8, 0x10, 0x9d, 0xad, 0x11, 0xd1, 0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8,
    ]

    /// UUID v5 of `managed-profile:<slug>` in the DNS namespace: stable across machines and installs.
    public static func profileId(slug: String) -> UUID {
        var input = Data(dnsNamespace)
        input.append(Data("managed-profile:\(slug)".utf8))
        var bytes = [UInt8](Insecure.SHA1.hash(data: input).prefix(16))
        bytes[6] = (bytes[6] & 0x0F) | 0x50   // version 5
        bytes[8] = (bytes[8] & 0x3F) | 0x80   // RFC 4122 variant
        return UUID(uuid: (bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7],
                           bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]))
    }

    /// Profiles in `other` replace same-id ones in place; new ones are appended in order.
    public func merged(with other: ManagedProfileSet) -> ManagedProfileSet {
        let replacements = Dictionary(other.profiles.map { ($0.id, $0) }, uniquingKeysWith: { _, b in b })
        var result = profiles.map { replacements[$0.id] ?? $0 }
        let existing = Set(profiles.map(\.id))
        result.append(contentsOf: other.profiles.filter { !existing.contains($0.id) })
        return ManagedProfileSet(profiles: result)
    }

    public static func parse(_ text: String) throws -> ManagedProfileSet {
        try parse(Data(text.utf8))
    }

    /// Parses the §7.1 document. Unknown fields are ignored; every shape error names the profile
    /// and the field, so an administrator can fix the document from the message alone.
    public static func parse(_ data: Data) throws -> ManagedProfileSet {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            throw ManagedProfileError.invalid("not a JSON object")
        }
        if let version = root["version"] {
            guard number(version)?.intValue == Self.version else {
                throw ManagedProfileError.invalid("version \(describe(version)) is not supported")
            }
        }

        let raw = root["profiles"] as? [Any] ?? []
        var profiles: [Profile] = []
        var seen: Set<String> = []
        for (index, item) in raw.enumerated() {
            guard let object = item as? [String: Any] else {
                throw ManagedProfileError.invalid("profile \(index + 1): not a JSON object")
            }
            let profile = try parseProfile(object, index: index)
            guard seen.insert(profile.id).inserted else {
                throw ManagedProfileError.invalid("profile '\(profile.id)': duplicate id")
            }
            profiles.append(profile)
        }
        return ManagedProfileSet(profiles: profiles)
    }

    private static func parseProfile(_ object: [String: Any], index: Int) throws -> Profile {
        guard let id = string(object["id"]), !id.isEmpty else {
            throw ManagedProfileError.invalid("profile \(index + 1): id is required")
        }
        guard id.wholeMatch(of: /[a-z0-9-]{1,64}/) != nil else {
            throw ManagedProfileError.invalid("profile '\(id)': id must match [a-z0-9-]{1,64}")
        }
        guard let name = string(object["name"]), !name.isEmpty else {
            throw ManagedProfileError.invalid("profile '\(id)': name is required")
        }
        let roles = try (object["roles"] as? [Any] ?? []).enumerated().map { roleIndex, item -> Role in
            guard let role = item as? [String: Any] else {
                throw ManagedProfileError.invalid("profile '\(id)' role \(roleIndex + 1): not a JSON object")
            }
            return try parseRole(role, profile: id, index: roleIndex)
        }
        return Profile(id: id, name: name, reason: string(object["reason"]),
                       pinned: bool(object["pinned"]), roles: roles)
    }

    private static func parseRole(_ object: [String: Any], profile: String, index: Int) throws -> Role {
        func fail(_ message: String) -> ManagedProfileError {
            .invalid("profile '\(profile)' role \(index + 1): \(message)")
        }
        guard let rawKind = string(object["kind"]) else { throw fail("kind is required") }
        guard let kind = RoleScopeKind(rawValue: rawKind) else { throw fail("unknown kind '\(rawKind)'") }
        guard let tenant = string(object["tenant"]), !tenant.isEmpty else { throw fail("tenant is required") }

        var duration: Duration?
        if let text = string(object["duration"]) {
            guard let parsed = ISO8601Duration.parse(text) else { throw fail("'\(text)' is not a valid duration") }
            duration = parsed
        }
        var access = GroupAccess.member
        if let text = string(object["access"]) {
            guard let parsed = GroupAccess(rawValue: text) else { throw fail("unknown access '\(text)'") }
            access = parsed
        }

        let role = string(object["role"])
        let scope = string(object["scope"])
        let group = string(object["group"])
        switch kind {
        case .entraDirectory:
            guard role?.isEmpty == false else { throw fail("role is required") }
        case .azureResource:
            guard role?.isEmpty == false else { throw fail("role is required") }
            guard scope?.isEmpty == false else { throw fail("scope is required for azureResource") }
        case .group:
            guard group?.isEmpty == false else { throw fail("group is required") }
        }

        return Role(kind: kind, tenant: tenant, role: role, scope: scope,
                    directoryScope: string(object["directoryScope"]) ?? "/",
                    group: group, access: access, duration: duration)
    }

    /// JSON strings only: a number or bool in a string field is a shape error, not a value.
    private static func string(_ value: Any?) -> String? {
        guard let value, !(value is NSNull) else { return nil }
        return value as? String
    }

    /// JSON booleans only; JSONSerialization models both as `NSNumber`, so the type id separates them.
    private static func bool(_ value: Any?) -> Bool {
        guard let value = value as? NSNumber, CFGetTypeID(value) == CFBooleanGetTypeID() else { return false }
        return value.boolValue
    }

    /// JSON numbers only, so `true` is not read as the number 1.
    private static func number(_ value: Any?) -> NSNumber? {
        guard let value = value as? NSNumber, CFGetTypeID(value) != CFBooleanGetTypeID() else { return nil }
        return value
    }

    private static func describe(_ value: Any) -> String {
        if let number = number(value) { return "\(number.intValue)" }
        return "\(value)"
    }
}
