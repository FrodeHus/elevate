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
    /// policy carries `AuthenticationContext_EndUser_Assignment`. It asks for the context alone:
    /// MSAL merges the client capability in from its own configuration, and a second `xms_cc` here
    /// would collide with it. The loopback providers, which have no MSAL to do that, send
    /// `clientCapabilities` on their own acquisitions instead.
    public static func authenticationContext(_ id: String) -> String {
        let escaped = id.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        return #"{"access_token":{"acrs":{"essential":true,"value":""# + escaped + #""}}}"#
    }

    /// The capability that says this client understands a claims challenge and will re-acquire
    /// against it. Without it Entra omits the `xms_cc` claim, and PIM refuses to honour an
    /// authentication context the token plainly carries: it answers the activation with
    /// `RoleAssignmentRequestAcrsValidationFailed` and re-issues the same challenge for as long as
    /// the client keeps re-minting tokens. MSAL declares it from the client configuration; the
    /// loopback providers, which speak to the token endpoint themselves, send it as a claims
    /// request on every acquisition.
    public static let clientCapability = "cp1"

    /// The claims request that declares nothing but the client capability, for a token acquisition
    /// that carries no challenge of its own.
    public static let clientCapabilities = #"{"access_token":{"xms_cc":{"values":[""# + clientCapability + #""]}}}"#
}
