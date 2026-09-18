using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Elevate.Cli.Infrastructure;
using CoreScopes = Elevate.Core.Auth.Scopes;

namespace Elevate.Cli.Auth;

/// <summary>
/// The resource a token is minted for. A token has exactly one audience, so every caller names it:
/// <c>arm</c>, <c>graph</c> or a resource URI. The name also decides the variable
/// <c>run --export-token</c> exports it as, so a token never reaches a command as a bare
/// <c>ACCESS_TOKEN</c> nobody can attribute to an API.
/// </summary>
public sealed record TokenResource(string Name, string Uri, IReadOnlyList<string> Scopes)
{
    /// <summary>Azure Resource Manager. <c>user_impersonation</c> is the whole surface, so the token carries the Azure roles that are active now.</summary>
    public static TokenResource Arm { get; } = new("arm", "https://management.azure.com", CoreScopes.ArmAll);

    /// <summary>
    /// Microsoft Graph. Deliberately narrow: the token carries only what Elevate's registration is
    /// consented for, whatever role was just activated. See <see cref="GraphCaveat"/>.
    /// </summary>
    public static TokenResource Graph { get; } = new("graph", "https://graph.microsoft.com", CoreScopes.GraphAll);

    /// <summary>What a Graph token from Elevate can and cannot do; printed before one is handed out.</summary>
    public static string GraphCaveat =>
        "A Graph token from Elevate carries only the permissions its app registration is consented for ("
        + string.Join(", ", CoreScopes.GraphAll.Select(Bare))
        + "). An activated role rides in the token's 'wids' claim, but that delegated ceiling applies on top of it, "
        + "so calls outside those permissions are refused with a 403 however privileged the role. ARM has no such ceiling.";

    /// <summary>True for Microsoft Graph, whose scope ceiling makes the token far less capable than the role suggests.</summary>
    public bool IsGraph => Name == "graph";

    /// <summary>The variable <c>run --export-token</c> puts the token in: <c>ELEVATE_ARM_TOKEN</c>, <c>ELEVATE_VAULT_AZURE_NET_TOKEN</c>.</summary>
    public string EnvironmentVariable
    {
        get
        {
            var builder = new StringBuilder("ELEVATE_");
            foreach (var c in Name)
            {
                builder.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
            }

            return builder.Append("_TOKEN").ToString();
        }
    }

    /// <summary>
    /// The resource <c>--resource</c> names: the <c>arm</c> and <c>graph</c> aliases, a resource URI
    /// (<c>https://vault.azure.net</c>) or a bare host (<c>vault.azure.net</c>). Anything else is a
    /// usage error rather than a token for an audience nobody meant.
    /// </summary>
    public static TokenResource Parse(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new CliException("Say which resource the token is for: --resource arm, --resource graph, or a resource URI.", ExitCodes.Usage);
        }

        switch (value.ToLowerInvariant())
        {
            case "arm" or "azure" or "azurerm" or "management":
                return Arm;
            case "graph" or "msgraph" or "microsoftgraph":
                return Graph;
            default:
                break;
        }

        // A bare host is the common slip and means one thing only; the scheme is filled in rather than refused.
        var text2 = value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value;
        if (!System.Uri.TryCreate(text2, UriKind.Absolute, out var uri)
            || (uri.Scheme != System.Uri.UriSchemeHttps && uri.Scheme != System.Uri.UriSchemeHttp)
            || uri.Host.Length == 0
            || !uri.Host.Contains('.', StringComparison.Ordinal))
        {
            throw new CliException($"'{value}' is not a resource: use arm, graph or a resource URI such as https://vault.azure.net.", ExitCodes.Usage);
        }

        var root = $"{uri.Scheme}://{uri.Host}";
        return new TokenResource(uri.Host, root, [root + "/.default"]);
    }

    /// <summary>When the token stops working, from its own <c>exp</c> claim; null when it is opaque.</summary>
    public static DateTimeOffset? ExpiresOn(string accessToken)
    {
        var parts = (accessToken ?? string.Empty).Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("exp", out var exp)
                && exp.ValueKind == JsonValueKind.Number
                && exp.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                    : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary><c>RoleEligibilitySchedule.Read.Directory</c> out of the full scope URL, for a readable caveat.</summary>
    private static string Bare(string scope) => scope[(scope.LastIndexOf('/') + 1)..];
}
