using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Providers;
using Elevate.Core.Support;

namespace Elevate.Core.Discovery;

/// <summary>A tenant an identity can reach, as reported by Azure Resource Manager.</summary>
public sealed record DiscoveredTenant(string TenantId, string DisplayName, string? DefaultDomain)
{
    public string Id => TenantId;
}

/// <summary>Finds the tenants an identity can act in, and names them.</summary>
public sealed partial class TenantDiscovery
{
    private readonly IHttpClient _http;
    private readonly ITokenProvider _tokens;

    public TenantDiscovery(IHttpClient http, ITokenProvider tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    private sealed record ArmTenant(string TenantId, string? DisplayName, string? DefaultDomain);

    private sealed record ArmCollection(IReadOnlyList<ArmTenant> Value);

    /// <summary>Every tenant the identity can reach, via Azure Resource Manager using a home-tenant token.</summary>
    public async Task<IReadOnlyList<DiscoveredTenant>> DiscoverTenantsAsync(Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var transport = new GraphTransport(_http, _tokens);
        var url = new Uri("https://management.azure.com/tenants?api-version=2022-12-01");
        var response = await transport
            .GetAsync(identity, identity.HomeTenantId, url, Scopes.ArmAll, ct)
            .ConfigureAwait(false);

        var collection = JsonSerializer.Deserialize<ArmCollection>(response.Body, GraphJson.Options);
        return
        [
            .. (collection?.Value ?? []).Select(t =>
                new DiscoveredTenant(t.TenantId, t.DisplayName ?? t.DefaultDomain ?? t.TenantId, t.DefaultDomain)),
        ];
    }

    /// <summary>Accepts a tenant GUID or a verified domain; domains are resolved via the OpenID configuration issuer.</summary>
    public async Task<string> ResolveTenantIdAsync(string domainOrId, CancellationToken ct = default)
    {
        var input = (domainOrId ?? string.Empty).Trim();
        if (TenantGuid().IsMatch(input))
        {
            return input.ToLowerInvariant();
        }

        var text = "https://login.microsoftonline.com/" + PercentEncodePath(input) + "/v2.0/.well-known/openid-configuration";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url))
        {
            throw new PimException(PimErrorKind.Unexpected, $"Invalid tenant '{input}'");
        }

        var response = await _http.SendAsync(new HttpRequestData("GET", url), ct).ConfigureAwait(false);
        if (response.Status != 200)
        {
            throw new PimException(PimErrorKind.Unexpected, $"Unknown tenant '{input}'", response.Status);
        }

        var issuer = JsonSerializer.Deserialize<OpenIdConfiguration>(response.Body, GraphJson.Options)?.Issuer ?? string.Empty;
        var match = IssuerTenant().Match(issuer);
        if (!match.Success)
        {
            throw new PimException(PimErrorKind.Unexpected, $"No tenant id in issuer {issuer}", 200);
        }

        return match.Groups[1].Value.ToLowerInvariant();
    }

    private sealed record OpenIdConfiguration(string? Issuer);

    /// <summary>Display name from Graph <c>/organization</c> inside that tenant.</summary>
    public async Task<string> TenantDisplayNameAsync(Identity identity, string tenantId, CancellationToken ct = default)
    {
        var transport = new GraphTransport(_http, _tokens);
        var url = new Uri(GraphTransport.GraphBase + "/organization?$select=id,displayName");
        var response = await transport
            .GetAsync(identity, tenantId, url, [Scopes.GraphUserRead], ct)
            .ConfigureAwait(false);

        var organizations = JsonSerializer.Deserialize<OrganizationCollection>(response.Body, GraphJson.Options);
        return organizations?.Value is [{ DisplayName: { } name }, ..] ? name : tenantId;
    }

    private sealed record Organization(string? DisplayName);

    private sealed record OrganizationCollection(IReadOnlyList<Organization> Value);

    /// <summary>
    /// Percent-encodes everything outside Foundation's <c>urlPathAllowed</c> set, which is what the
    /// Swift side applies to a domain before dropping it into the discovery URL.
    /// </summary>
    internal static string PercentEncodePath(string value)
    {
        const string allowed = "!$&'()*+,-./:;=@_~";
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || allowed.Contains(c, StringComparison.Ordinal))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex TenantGuid();

    [GeneratedRegex("([0-9a-fA-F-]{36})")]
    private static partial Regex IssuerTenant();
}
