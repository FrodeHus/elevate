using System.Text;
using System.Text.Json;

namespace Elevate.Core.Auth;

/// <summary>Reads claims out of a JWT access token. Port of the Swift <c>AccessTokenClaims</c>.</summary>
public static class AccessTokenClaims
{
    /// <summary>Scopes any one of which lets the caller self-activate Entra directory roles.</summary>
    public static IReadOnlySet<string> EntraActivationScopes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "RoleAssignmentSchedule.ReadWrite.Directory",
        "RoleManagement.ReadWrite.Directory",
        "PrivilegedAccess.ReadWrite.AzureAD",
    };

    /// <summary>Delegated scopes in the token's <c>scp</c> claim, or null when the token is opaque or unparsable.</summary>
    public static IReadOnlySet<string>? GrantedScopes(string accessToken)
    {
        if (Claim(accessToken, "scp") is not { } scp)
        {
            return null;
        }

        return new HashSet<string>(scp.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    }

    /// <summary>The caller's object id in the token's tenant (<c>oid</c>), or null when the token is opaque.</summary>
    public static string? ObjectId(string accessToken) => Claim(accessToken, "oid");

    /// <summary>
    /// Whether a Graph token carries a scope that permits Entra role activation. Null when the token
    /// does not expose its scopes, so the caller keeps its prior assumption.
    /// </summary>
    public static bool? PermitsEntraActivation(string accessToken) =>
        GrantedScopes(accessToken) is { } scopes ? scopes.Overlaps(EntraActivationScopes) : null;

    private static string? Claim(string accessToken, string name)
    {
        var parts = accessToken?.Split('.');
        if (parts is not { Length: >= 2 } || Base64UrlDecode(parts[1]) is not { } payload)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[]? Base64UrlDecode(string value)
    {
        var b64 = value.Replace('-', '+').Replace('_', '/');
        b64 += new string('=', (4 - (b64.Length % 4)) % 4);
        try
        {
            return Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
