import Foundation

/// Holds the access token a step-up sign-in just produced, so the call that follows uses *that*
/// token rather than whatever the SDK's cache still holds.
///
/// MSAL bypasses its access-token cache whenever a claims request is specified, and does not
/// promise to write the token it hands back into that cache. Asking it silently for a token right
/// after a step-up can therefore return the pre-step-up one — which carries no `acrs` claim, so the
/// service refuses the retry for exactly the reason it refused the first attempt, and the user is
/// told their verification did not count. `OAuthSession` already caches the loopback providers'
/// tokens itself; this is the same guarantee for MSAL.
public actor StepUpTokenCache {
    struct Key: Hashable { let identityId: String; let tenantId: String; let scopes: String }
    struct Entry { let token: String; let expiresAt: Date }

    /// Discarded this long before the token's own expiry, so a token that expires mid-call is not handed out.
    static let skew: TimeInterval = 60
    /// Lifetime assumed for a token whose expiry cannot be read (an opaque, non-JWT token). Long
    /// enough to cover the retry the step-up was made for, short enough to be no one's cache.
    static let opaqueLifetime: TimeInterval = 300

    private var entries: [Key: Entry] = [:]
    private let now: @Sendable () -> Date

    public init(now: @escaping @Sendable () -> Date = { Date() }) { self.now = now }

    /// Remembers a token acquired with a claims request. Call only for a claims acquisition: a
    /// plain interactive token has nothing the SDK's own cache lacks.
    public func store(_ token: String, identityId: String, tenantId: String, scopes: [String]) {
        let expiry = AccessTokenClaims.expiry(token) ?? now().addingTimeInterval(Self.opaqueLifetime)
        entries[Self.key(identityId, tenantId, scopes)] = Entry(token: token, expiresAt: expiry)
    }

    /// The stored token for this identity, tenant and scope set, or nil once it is gone or too near expiry.
    public func token(identityId: String, tenantId: String, scopes: [String]) -> String? {
        let key = Self.key(identityId, tenantId, scopes)
        guard let entry = entries[key] else { return nil }
        guard entry.expiresAt.timeIntervalSince(now()) > Self.skew else {
            entries[key] = nil
            return nil
        }
        return entry.token
    }

    /// Drops everything held for one identity: it signed out, or its client id changed.
    public func forget(identityId: String) {
        entries = entries.filter { $0.key.identityId != identityId }
    }

    /// Scope order is the caller's accident, not part of the identity of a token.
    private static func key(_ identityId: String, _ tenantId: String, _ scopes: [String]) -> Key {
        Key(identityId: identityId, tenantId: tenantId, scopes: scopes.sorted().joined(separator: " "))
    }
}
