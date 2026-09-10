namespace Elevate.Core.Auth;

/// <summary>
/// The optional shared Elevate app registration the project publishes for quick starts and
/// testing: a multi-tenant public client with no secret and only the delegated scopes in the
/// registration guide. Offered with no SLA. The id is never written to diagnostics, where only
/// "shared Elevate app" appears.
/// </summary>
public static class SharedApp
{
    /// <summary>Application (client) id of the shared Elevate app registration.</summary>
    public const string ClientId = "c9011cc5-7422-4630-a432-73ff4df5834e";

    /// <summary>
    /// Admin consent <c>redirect_uri</c> for the shared registration: a Web redirect registered on
    /// the shared app, so the administrator lands on a page that explains what just happened.
    /// </summary>
    public const string ConsentRedirectUri = "https://elevate.reothor.no/consent.html";

    /// <summary>The loopback-friendly redirect an own registration's consent link lands on.</summary>
    public const string NativeClientRedirectUri = "https://login.microsoftonline.com/common/oauth2/nativeclient";

    /// <summary>True when <paramref name="clientId"/> is the shared registration, ignoring case and surrounding whitespace.</summary>
    public static bool IsSharedClientId(string? clientId) =>
        string.Equals((clientId ?? string.Empty).Trim(), ClientId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The admin consent link for <paramref name="clientId"/>: the Graph, group and entitlement
    /// scopes Elevate asks for, under <paramref name="tenantSegment"/> (a tenant id, or
    /// <c>organizations</c> for the shared app). The shared registration redirects to its consent
    /// page; own registrations keep the <c>nativeclient</c> redirect.
    /// </summary>
    public static Uri AdminConsentUri(string clientId, string tenantSegment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantSegment);
        var id = clientId.Trim();
        var scopes = string.Join(' ', Scopes.GraphAll.Concat(Scopes.GroupAll).Concat(Scopes.EntitlementAll));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = id,
            ["scope"] = scopes,
            ["redirect_uri"] = IsSharedClientId(id) ? ConsentRedirectUri : NativeClientRedirectUri,
        };
        var text = "https://login.microsoftonline.com/" + Uri.EscapeDataString(tenantSegment) + "/v2.0/adminconsent?"
            + string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return new Uri(text);
    }
}
