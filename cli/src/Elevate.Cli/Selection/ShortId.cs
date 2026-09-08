using System.Security.Cryptography;
using System.Text;
using Elevate.Core;
using Elevate.Core.Models;

namespace Elevate.Cli.Selection;

/// <summary>
/// Eight hex characters that name a role or request on the command line. Derived from the stable
/// key, so the same role has the same id in every run and in <c>--json</c> output.
/// </summary>
public static class ShortId
{
    public static string For(RoleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Hash(Json.Serialize(key));
    }

    public static string For(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return For(request.TenantKey, request.Id);
    }

    /// <summary>For an access package, request or assignment id inside one tenant.</summary>
    public static string For(TenantKey key, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Hash(key.IdentityId + "|" + key.TenantId + "|" + id);
    }

    public static bool LooksLikeId(string text) =>
        text.Length == 8 && text.All(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c));

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes)[..8];
    }
}
