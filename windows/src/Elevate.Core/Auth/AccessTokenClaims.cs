using System.Buffers.Text;
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
    /// The directory role template ids the token carries (<c>wids</c>), or null when the token is
    /// opaque. Entra emits <c>wids</c> for tenant-wide directory roles only: a role scoped to an
    /// administrative unit or an application never appears here, so its absence is not evidence
    /// that the role is missing — see <see cref="CarriesDirectoryRole"/>.
    /// </summary>
    public static IReadOnlySet<string>? DirectoryRoles(string accessToken) => IdSet(accessToken, "wids");

    /// <summary>
    /// The group object ids the token carries (<c>groups</c>), or null when the token is opaque or
    /// the registration does not emit group claims — which is the default, and why a caller falls
    /// back to asking Graph.
    /// </summary>
    public static IReadOnlySet<string>? GroupMemberships(string accessToken) => IdSet(accessToken, "groups");

    /// <summary>
    /// Whether the token grants <paramref name="roleTemplateId"/>. Null when the token is opaque, so
    /// the caller reports that it cannot tell rather than that the role is missing.
    /// </summary>
    public static bool? CarriesDirectoryRole(string accessToken, string roleTemplateId) =>
        DirectoryRoles(accessToken) is { } roles ? roles.Contains(roleTemplateId?.ToLowerInvariant() ?? string.Empty) : null;

    /// <summary>Whether the token carries <paramref name="groupId"/> in <c>groups</c>; null when there is no such claim.</summary>
    public static bool? CarriesGroup(string accessToken, string groupId) =>
        GroupMemberships(accessToken) is { } groups ? groups.Contains(groupId?.ToLowerInvariant() ?? string.Empty) : null;

    /// <summary>
    /// A claim holding an array of GUIDs, lower-cased so the comparison does not depend on how the
    /// service happened to case them. Null when the claim is absent or is not an array.
    /// </summary>
    private static IReadOnlySet<string>? IdSet(string accessToken, string name)
    {
        if (ArrayClaim(accessToken, name) is not { } values)
        {
            return null;
        }

        return new HashSet<string>(values.Select(v => v.ToLowerInvariant()), StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a Graph token carries a scope that permits Entra role activation. Null when the token
    /// does not expose its scopes, so the caller keeps its prior assumption.
    /// </summary>
    public static bool? PermitsEntraActivation(string accessToken) =>
        GrantedScopes(accessToken) is { } scopes ? scopes.Overlaps(EntraActivationScopes) : null;

    /// <summary>
    /// Whether a Graph token carries the self-service entitlement management scope. Null when the
    /// token does not expose its scopes.
    /// </summary>
    public static bool? PermitsEntitlementSelfService(string accessToken) =>
        GrantedScopes(accessToken) is { } scopes ? scopes.Contains(Scopes.EntitlementClaim) : null;

    private static string? Claim(string accessToken, string name)
    {
        using var payload = Payload(accessToken);
        return payload is not null
            && payload.RootElement.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>The string members of an array claim, or null when it is absent or not an array.</summary>
    private static IReadOnlyList<string>? ArrayClaim(string accessToken, string name)
    {
        using var payload = Payload(accessToken);
        if (payload is null
            || !payload.RootElement.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } text)
            {
                items.Add(text);
            }
        }

        return items;
    }

    /// <summary>The decoded JWT body, or null when the token is opaque or not JSON.</summary>
    private static JsonDocument? Payload(string accessToken)
    {
        var parts = accessToken?.Split('.');
        if (parts is not { Length: >= 2 } || Base64UrlDecode(parts[1]) is not { } body)
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[]? Base64UrlDecode(string value)
    {
        try
        {
            return Base64Url.DecodeFromChars(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
