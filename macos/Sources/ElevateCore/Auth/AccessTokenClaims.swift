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
}
