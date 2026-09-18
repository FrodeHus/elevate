import Foundation

/// Builds a JWT-shaped token carrying the given claims, so a test can drive claim reads.
enum Jwt {
    static func with(_ claims: [String: Any]) -> String {
        let body = try! JSONSerialization.data(withJSONObject: claims)
        let b64 = body.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .trimmingCharacters(in: CharacterSet(charactersIn: "="))
        return "eyJhbGciOiJub25lIn0.\(b64).sig"
    }

    /// A token whose `wids` names the given directory role template ids.
    static func withRoles(_ roleTemplateIds: String...) -> String { with(["wids": roleTemplateIds]) }

    /// A token whose `groups` names the given group object ids.
    static func withGroups(_ groupIds: String...) -> String { with(["groups": groupIds]) }
}
