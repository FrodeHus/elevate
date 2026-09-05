import Foundation

public enum PIMError: Error, Hashable, Sendable {
    /// Tenant has not consented to a required scope (Graph 403 or AADSTS65001).
    case consentRequired
    /// The service refused the call (HTTP 403) for an account that cannot be helped by admin consent (first-party sign-in); carries the server message.
    case forbidden(String)
    /// Silent token acquisition failed; the caller must run an interactive flow.
    case interactionRequired
    /// Resource returned a claims challenge; payload is the decoded claims JSON.
    case claimsChallenge(String)
    case notEligible
    case policyViolation(String)
    case pendingApproval
    case network(String)
    case unexpected(status: Int, body: String)

    public var userMessage: String {
        switch self {
        case .consentRequired: "Admin consent required for this tenant"
        case .forbidden(let m): "Not permitted: \(m)"
        case .interactionRequired: "Sign in again"
        case .claimsChallenge: "Multi-factor authentication required"
        case .notEligible: "Not eligible for this role"
        case .policyViolation(let m): m
        case .pendingApproval: "Awaiting approval"
        case .network(let m): "Network error: \(m)"
        // status 0 is our own marker for "not an HTTP failure": the body is the message.
        case .unexpected(let s, let body): s == 0 ? (body.isEmpty ? "Unexpected error" : body) : (body.isEmpty ? "Unexpected response (\(s))" : "Unexpected response (\(s)): \(String(body.prefix(300)))")
        }
    }
}
