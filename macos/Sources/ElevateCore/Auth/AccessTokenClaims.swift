import Foundation

/// The few access-token claims Elevate inspects. The token is Graph's, not ours to validate; we
/// only read `scp` to learn what the sign-in method was actually granted in this tenant.
public enum AccessTokenClaims {
    static func payload(_ accessToken: String) -> [String: Any]? {
        let parts = accessToken.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count >= 2, let data = Data(base64URLEncoded: String(parts[1])) else { return nil }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    /// Delegated scopes in the token's `scp` claim, or nil when the token is opaque or unparsable.
    public static func grantedScopes(_ accessToken: String) -> Set<String>? {
        guard let scp = payload(accessToken)?["scp"] as? String else { return nil }
        return Set(scp.split(separator: " ").map(String.init))
    }

    /// The caller's object id in the token's tenant (`oid`), or nil when the token is opaque.
    public static func objectId(_ accessToken: String) -> String? {
        payload(accessToken)?["oid"] as? String
    }

    /// The directory role template ids the token carries (`wids`), lower-cased, or nil when the
    /// token is opaque or has no such claim. Entra emits `wids` for tenant-wide directory roles
    /// only: a role scoped to an administrative unit or an application never appears here, so its
    /// absence is not evidence that the role is missing — see `carriesDirectoryRole`.
    public static func directoryRoles(_ accessToken: String) -> Set<String>? {
        idSet(accessToken, claim: "wids")
    }

    /// The group object ids the token carries (`groups`), lower-cased, or nil when the token is
    /// opaque or the registration does not emit group claims — which is the default, and why a
    /// caller falls back to asking Graph.
    public static func groupMemberships(_ accessToken: String) -> Set<String>? {
        idSet(accessToken, claim: "groups")
    }

    /// Whether the token grants `roleTemplateId`. nil when the token is opaque, so the caller
    /// reports that it cannot tell rather than that the role is missing.
    public static func carriesDirectoryRole(_ accessToken: String, roleTemplateId: String) -> Bool? {
        directoryRoles(accessToken)?.contains(roleTemplateId.lowercased())
    }

    /// Whether the token carries `groupId` in `groups`; nil when there is no such claim.
    public static func carriesGroup(_ accessToken: String, groupId: String) -> Bool? {
        groupMemberships(accessToken)?.contains(groupId.lowercased())
    }

    /// A claim holding an array of GUIDs, lower-cased so the comparison does not depend on how the
    /// service happened to case them. Non-string members are ignored.
    private static func idSet(_ accessToken: String, claim: String) -> Set<String>? {
        guard let values = payload(accessToken)?[claim] as? [Any] else { return nil }
        return Set(values.compactMap { ($0 as? String)?.lowercased() })
    }

    /// Scopes any one of which lets the caller self-activate Entra directory roles.
    public static let entraActivationScopes: Set<String> = [
        "RoleAssignmentSchedule.ReadWrite.Directory",
        "RoleManagement.ReadWrite.Directory",
        "PrivilegedAccess.ReadWrite.AzureAD",
    ]

    /// Whether a Graph token carries a scope that permits Entra role activation.
    /// nil when the token does not expose its scopes, so the caller keeps its prior assumption.
    public static func permitsEntraActivation(_ accessToken: String) -> Bool? {
        guard let scopes = grantedScopes(accessToken) else { return nil }
        return !scopes.isDisjoint(with: entraActivationScopes)
    }

    /// Whether a Graph token carries the self-service entitlement management scope.
    /// nil when the token does not expose its scopes.
    public static func permitsEntitlementSelfService(_ accessToken: String) -> Bool? {
        guard let scopes = grantedScopes(accessToken) else { return nil }
        return scopes.contains(EntitlementScopes.claim)
    }
}
