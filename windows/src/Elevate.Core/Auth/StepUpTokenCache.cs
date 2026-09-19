namespace Elevate.Core.Auth;

/// <summary>
/// Holds the access token a step-up sign-in just produced, so the call that follows uses
/// <em>that</em> token rather than whatever MSAL's cache still holds. Port of the Swift
/// <c>StepUpTokenCache</c>.
/// </summary>
/// <remarks>
/// MSAL bypasses its access-token cache whenever a claims request is specified, and does not
/// promise to write the token it hands back into that cache. Asking it silently for a token right
/// after a step-up can therefore return the pre-step-up one — which carries no <c>acrs</c> claim,
/// so the service refuses the retry for exactly the reason it refused the first attempt, and the
/// user is told their verification did not count.
/// </remarks>
public sealed class StepUpTokenCache
{
    /// <summary>Discarded this long before the token's own expiry, so a token that expires mid-call is not handed out.</summary>
    public static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Lifetime assumed for a token whose expiry cannot be read (an opaque, non-JWT token). Long
    /// enough to cover the retry the step-up was made for, short enough to be no one's cache.
    /// </summary>
    public static readonly TimeSpan OpaqueLifetime = TimeSpan.FromMinutes(5);

    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly Lock _gate = new();
    private readonly Func<DateTimeOffset> _now;

    /// <param name="now">The clock, for tests; the system clock by default.</param>
    public StepUpTokenCache(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// Remembers a token acquired with a claims request. Call only for a claims acquisition: a
    /// plain interactive token has nothing MSAL's own cache lacks.
    /// </summary>
    public void Store(string token, string identityId, string tenantId, IReadOnlyList<string> scopes)
    {
        var expiry = AccessTokenClaims.Expiry(token) ?? _now() + OpaqueLifetime;
        lock (_gate)
        {
            _entries[KeyFor(identityId, tenantId, scopes)] = new Entry(token, expiry);
        }
    }

    /// <summary>
    /// The stored token for this identity, tenant and scope set, or null once it is gone or too
    /// near expiry.
    /// </summary>
    public string? Token(string identityId, string tenantId, IReadOnlyList<string> scopes)
    {
        var key = KeyFor(identityId, tenantId, scopes);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (entry.ExpiresAt - _now() <= Skew)
            {
                _entries.Remove(key);
                return null;
            }

            return entry.Token;
        }
    }

    /// <summary>Drops everything held for one identity: it signed out, or its client id changed.</summary>
    public void Forget(string identityId)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(k => k.IdentityId == identityId).ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    /// <summary>Scope order is the caller's accident, not part of the identity of a token.</summary>
    private static Key KeyFor(string identityId, string tenantId, IReadOnlyList<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        return new Key(identityId, tenantId, string.Join(' ', scopes.Order(StringComparer.Ordinal)));
    }

    private readonly record struct Key(string IdentityId, string TenantId, string Scopes);

    private readonly record struct Entry(string Token, DateTimeOffset ExpiresAt);
}
