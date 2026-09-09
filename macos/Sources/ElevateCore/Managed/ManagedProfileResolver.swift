import Foundation

/// The profiles a `ManagedProfileSet` resolved to, plus what could not be resolved.
public struct ManagedProfileResolution: Hashable, Sendable {
    /// Always `source == .managed`, with ids derived from the slugs.
    public var profiles: [ActivationProfile]
    /// Human-readable notes, e.g. "Prod incident: no account in tenant fabrikam.com".
    public var warnings: [String]

    public init(profiles: [ActivationProfile] = [], warnings: [String] = []) {
        self.profiles = profiles
        self.warnings = warnings
    }
}

/// Turns account-agnostic managed profiles into `ActivationProfile`s against the signed-in
/// accounts (design §7.2). Pure: everything it needs is passed in, so it is recomputed whenever
/// the accounts, tenants or eligible roles change.
public enum ManagedProfileResolver {
    /// - Parameters:
    ///   - tenantIds: managed tenant entry (as configured) → tenant id, from `ManagedTenantResolver`.
    ///   - tenants: every tenant context tracked by every signed-in account.
    ///   - roles: the eligible roles known so far, keyed as they are in the app.
    public static func resolve(_ set: ManagedProfileSet, tenantIds: [String: String],
                               tenants: [TenantContext], roles: [RoleKey: EligibleRole]) -> ManagedProfileResolution {
        var result = ManagedProfileResolution()
        for profile in set.profiles {
            var entries: [ActivationProfile.Entry] = []
            var seen: Set<RoleKey> = []
            for spec in profile.roles {
                guard let tenantId = tenantId(for: spec.tenant, tenantIds: tenantIds) else {
                    result.warnings.append("\(profile.name): could not resolve tenant \(spec.tenant)")
                    continue
                }
                let contexts = tenants.filter { $0.tenantId.lowercased() == tenantId }
                guard !contexts.isEmpty else {
                    result.warnings.append("\(profile.name): no account in tenant \(spec.tenant)")
                    continue
                }
                let matched = contexts.flatMap { context in
                    roles.values
                        .filter { $0.key.identityId == context.identityId && $0.key.tenantId == context.tenantId }
                        .filter { matches(spec, $0) }
                        .map(\.key)
                }
                // Nothing matched anywhere in the tenant: a placeholder per account, so the planner
                // marks it "not eligible" once the tenant has loaded and "not loaded" before that.
                let keys = matched.isEmpty
                    ? contexts.map { RoleKey(identityId: $0.identityId, tenantId: $0.tenantId, scope: scope(for: spec)) }
                    : matched
                for key in keys where seen.insert(key).inserted {
                    entries.append(ActivationProfile.Entry(roleKey: key, lastDuration: spec.duration))
                }
            }
            result.profiles.append(ActivationProfile(
                id: profile.profileId, name: profile.name, entries: ordered(entries, roles: roles),
                lastJustification: profile.reason, pinned: profile.pinned, source: .managed))
        }
        return result
    }

    /// A GUID entry is its own tenant id; anything else is a domain the resolver had to look up.
    private static func tenantId(for entry: String, tenantIds: [String: String]) -> String? {
        if UUID(uuidString: entry) != nil { return entry.lowercased() }
        return tenantIds[entry]?.lowercased()
    }

    /// Kind, then the scope constraints, then the role's name or the id the spec named it by.
    private static func matches(_ spec: ManagedProfileSet.Role, _ role: EligibleRole) -> Bool {
        switch role.key.scope {
        case let .entraDirectory(roleDefinitionId, directoryScopeId):
            guard spec.kind == .entraDirectory, same(directoryScopeId, spec.directoryScope) else { return false }
            return same(role.displayName, spec.role) || same(roleDefinitionId, spec.role)
        case let .azureResource(scope, roleDefinitionId):
            guard spec.kind == .azureResource, same(scope, spec.scope) else { return false }
            return same(role.displayName, spec.role) || same(tail(roleDefinitionId), spec.role.map(tail))
        case let .group(groupId, accessId):
            guard spec.kind == .group, accessId == spec.access else { return false }
            return same(role.displayName, spec.group) || same(groupId, spec.group)
        }
    }

    private static func scope(for spec: ManagedProfileSet.Role) -> RoleScope {
        switch spec.kind {
        case .entraDirectory: .entraDirectory(roleDefinitionId: spec.role ?? "", directoryScopeId: spec.directoryScope)
        case .azureResource: .azureResource(scope: spec.scope ?? "", roleDefinitionId: spec.role ?? "")
        case .group: .group(groupId: spec.group ?? "", accessId: spec.access)
        }
    }

    /// The same order `saveProfile` uses: account, then tenant, then kind, then name.
    private static func ordered(_ entries: [ActivationProfile.Entry],
                                roles: [RoleKey: EligibleRole]) -> [ActivationProfile.Entry] {
        entries.sorted { a, b in
            let x = a.roleKey, y = b.roleKey
            if x.identityId != y.identityId { return x.identityId < y.identityId }
            if x.tenantId != y.tenantId { return x.tenantId < y.tenantId }
            if x.scope.kind != y.scope.kind { return x.scope.kind.rawValue < y.scope.kind.rawValue }
            let nameA = roles[x]?.displayName ?? "", nameB = roles[y]?.displayName ?? ""
            if nameA != nameB { return nameA < nameB }
            return identifier(x.scope) < identifier(y.scope)   // a total order, so the sort is deterministic
        }
    }

    private static func identifier(_ scope: RoleScope) -> String {
        switch scope {
        case let .entraDirectory(roleDefinitionId, directoryScopeId): "\(directoryScopeId)|\(roleDefinitionId)"
        case let .azureResource(scope, roleDefinitionId): "\(scope)|\(roleDefinitionId)"
        case let .group(groupId, accessId): "\(groupId)|\(accessId.rawValue)"
        }
    }

    private static func tail(_ id: String) -> String {
        id.split(separator: "/").last.map(String.init) ?? id
    }

    private static func same(_ a: String, _ b: String?) -> Bool {
        guard let b else { return false }
        return a.caseInsensitiveCompare(b) == .orderedSame
    }
}
