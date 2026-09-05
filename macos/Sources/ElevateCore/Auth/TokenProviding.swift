import Foundation

public enum GraphScopes {
    public static let userRead = "https://graph.microsoft.com/User.Read"
    public static let all = [
        "https://graph.microsoft.com/User.Read",
        "https://graph.microsoft.com/RoleEligibilitySchedule.Read.Directory",
        "https://graph.microsoft.com/RoleAssignmentSchedule.ReadWrite.Directory",
        "https://graph.microsoft.com/RoleManagementPolicy.Read.Directory",
    ]
}

public enum ArmScopes {
    public static let all = ["https://management.azure.com/user_impersonation"]
}

public protocol TokenProviding: Sendable {
    /// Interactive sign-in against the `organizations` authority using the given method. Returns the new identity.
    func signIn(method: SignInMethod) async throws -> Identity
    func signOut(_ identity: Identity) async throws
    func identities() async throws -> [Identity]
    /// Silent acquisition for `tenantId`. Throws `PIMError.interactionRequired` when a prompt is needed.
    func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String
    /// Interactive acquisition, optionally carrying a claims challenge. Throws `PIMError.consentRequired` on AADSTS65001.
    @discardableResult
    func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String
}

public enum InteractionRetry {
    /// Runs `operation`; on `interactionRequired` or `claimsChallenge` acquires a token interactively once and retries once.
    /// `fallbackClaims` is sent when the service demanded interaction without saying which claims
    /// it wants (a PIM MFA rule), so the browser actually re-verifies instead of silently reusing the session.
    public static func run<T: Sendable>(
        tokens: any TokenProviding, identity: Identity, tenantId: String, scopes: [String],
        fallbackClaims: String? = nil, operation: () async throws -> T
    ) async throws -> T {
        do {
            return try await operation()
        } catch PIMError.interactionRequired {
            try await tokens.acquireInteractively(identity: identity, tenantId: tenantId, scopes: scopes, claims: fallbackClaims)
            return try await operation()
        } catch PIMError.claimsChallenge(let claims) {
            try await tokens.acquireInteractively(identity: identity, tenantId: tenantId, scopes: scopes, claims: claims)
            return try await operation()
        }
    }
}
