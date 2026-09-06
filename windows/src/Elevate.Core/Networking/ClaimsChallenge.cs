using System.Text;
using System.Text.RegularExpressions;

namespace Elevate.Core.Networking;

/// <summary>Port of the Swift <c>ClaimsChallenge</c> enum.</summary>
public static partial class ClaimsChallenge
{
    /// <summary>
    /// Extracts the base64url <c>claims</c> parameter from a <c>WWW-Authenticate</c> header and
    /// returns the decoded JSON, or null when the header carries no claims challenge.
    /// </summary>
    public static string? Parse(string wwwAuthenticate)
    {
        var match = ClaimsParameter().Match(wwwAuthenticate);
        if (!match.Success)
        {
            return null;
        }

        var b64 = match.Groups[1].Value.Replace('-', '+').Replace('_', '/');
        while (b64.Length % 4 != 0)
        {
            b64 += "=";
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(data);
    }

    /// <summary>
    /// Claims request that makes Entra re-verify the user with multi-factor authentication, for a
    /// PIM <c>MfaRule</c> refusal (a 400, so the service sends no challenge header of its own).
    /// </summary>
    public const string MultiFactor = """{"access_token":{"amr":{"values":["mfa"]}}}""";

    /// <summary>
    /// Claims request for a Conditional Access authentication context (<c>acrs</c>), for roles whose
    /// policy carries <c>AuthenticationContext_EndUser_Assignment</c>.
    /// </summary>
    public static string AuthenticationContext(string id)
    {
        var escaped = id.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "{\"access_token\":{\"acrs\":{\"essential\":true,\"value\":\"" + escaped + "\"}}}";
    }

    [GeneratedRegex("claims=\"([^\"]+)\"")]
    private static partial Regex ClaimsParameter();
}
