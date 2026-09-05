import Foundation

public enum ClaimsChallenge {
    /// Extracts the base64url `claims` parameter from a `WWW-Authenticate` header and returns the decoded JSON.
    public static func parse(wwwAuthenticate header: String) -> String? {
        guard let m = header.firstMatch(of: /claims="([^"]+)"/) else { return nil }
        var b64 = String(m.1).replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while b64.count % 4 != 0 { b64 += "=" }
        guard let data = Data(base64Encoded: b64) else { return nil }
        return String(decoding: data, as: UTF8.self)
    }

    /// Claims request that makes Entra re-verify the user with multi-factor authentication, for a
    /// PIM `MfaRule` refusal (a 400, so the service sends no challenge header of its own).
    public static let multiFactor = #"{"access_token":{"amr":{"values":["mfa"]}}}"#

    /// Claims request for a Conditional Access authentication context (`acrs`), for roles whose
    /// policy carries `AuthenticationContext_EndUser_Assignment`.
    public static func authenticationContext(_ id: String) -> String {
        let escaped = id.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        return #"{"access_token":{"acrs":{"essential":true,"value":""# + escaped + #""}}}"#
    }
}
