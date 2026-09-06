using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Support;

namespace Elevate.Core.Providers;

/// <summary>Sends authorized requests and maps non-success responses to <see cref="PimException"/>.</summary>
public sealed partial class GraphTransport
{
    public const string GraphBase = "https://graph.microsoft.com/v1.0";
    public const string ArmBase = "https://management.azure.com";

    /// <summary>The approvals resources live on beta only; every other Graph call stays on v1.0.</summary>
    public const string GraphBetaBase = "https://graph.microsoft.com/beta";

    private readonly IHttpClient _http;
    private readonly Func<HttpResponseData, PimException> _mapper;

    public GraphTransport(
        IHttpClient http,
        ITokenProvider tokens,
        Func<HttpResponseData, PimException>? mapper = null)
    {
        _http = http;
        Tokens = tokens;
        _mapper = mapper ?? MapError;
    }

    /// <summary>The token provider, so a provider can read the caller's claims from its own token.</summary>
    internal ITokenProvider Tokens { get; }

    /// <summary>A Graph URL for <paramref name="path"/>, percent-encoding it only when it is not already a valid URL.</summary>
    public Uri GraphUrl(string path) => Url(GraphBase, path);

    /// <summary>A Graph beta URL for <paramref name="path"/>, percent-encoding it only when it is not already a valid URL.</summary>
    public Uri GraphBetaUrl(string path) => Url(GraphBetaBase, path);

    private static Uri Url(string baseUrl, string path)
    {
        if (Uri.TryCreate(baseUrl + path, UriKind.Absolute, out var url))
        {
            return url;
        }

        if (Uri.TryCreate(baseUrl + PercentEncodeQuery(path), UriKind.Absolute, out var encoded))
        {
            return encoded;
        }

        throw new PimException(PimErrorKind.Unexpected, "Bad URL");
    }

    /// <summary>OData string literals escape a single quote by doubling it.</summary>
    public static string OdataEscaped(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// Percent-encodes everything outside Foundation's <c>urlQueryAllowed</c> character set, which is
    /// what the Swift providers use before dropping an OData <c>$filter</c> into a URL.
    /// </summary>
    public static string PercentEncodeQuery(string value)
    {
        const string allowed = "!$&'()*+,-./:;=?@_~";
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

    /// <summary>One page of a Graph list.</summary>
    public sealed record Page<T>(
        IReadOnlyList<T> Value,
        [property: JsonPropertyName("@odata.nextLink")] string? NextLink);

    /// <summary>GETs every page of a Graph list, following <c>@odata.nextLink</c>.</summary>
    public async Task<IReadOnlyList<T>> ListAllAsync<T>(
        Identity identity,
        string tenantId,
        Uri url,
        IReadOnlyList<string> scopes,
        CancellationToken ct = default)
    {
        Uri? next = url;
        var all = new List<T>();
        while (next is { } current)
        {
            var response = await GetAsync(identity, tenantId, current, scopes, ct).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<Page<T>>(response.Body, GraphJson.Options);
            if (page?.Value is { } items)
            {
                all.AddRange(items);
            }

            next = page?.NextLink is { } link && Uri.TryCreate(link, UriKind.Absolute, out var parsed) ? parsed : null;
        }

        return all;
    }

    public Task<HttpResponseData> GetAsync(
        Identity identity, string tenantId, Uri url, IReadOnlyList<string> scopes, CancellationToken ct = default)
        => SendAsync(new HttpRequestData("GET", url), identity, tenantId, scopes, ct);

    public Task<HttpResponseData> PostAsync(
        Identity identity, string tenantId, Uri url, IReadOnlyList<string> scopes, byte[] body, CancellationToken ct = default)
        => SendAsync(WithJsonBody("POST", url, body), identity, tenantId, scopes, ct);

    public Task<HttpResponseData> PutAsync(
        Identity identity, string tenantId, Uri url, IReadOnlyList<string> scopes, byte[] body, CancellationToken ct = default)
        => SendAsync(WithJsonBody("PUT", url, body), identity, tenantId, scopes, ct);

    public Task<HttpResponseData> PatchAsync(
        Identity identity, string tenantId, Uri url, IReadOnlyList<string> scopes, byte[] body, CancellationToken ct = default)
        => SendAsync(WithJsonBody("PATCH", url, body), identity, tenantId, scopes, ct);

    private static HttpRequestData WithJsonBody(string method, Uri url, byte[] body) =>
        new(method, url, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
        }, body);

    private async Task<HttpResponseData> SendAsync(
        HttpRequestData request,
        Identity identity,
        string tenantId,
        IReadOnlyList<string> scopes,
        CancellationToken ct)
    {
        var token = await Tokens.AccessTokenAsync(identity, tenantId, scopes, ct).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {token}",
            ["Accept"] = "application/json",
        };

        var response = await _http.SendAsync(request with { Headers = headers }, ct).ConfigureAwait(false);
        if (response.Status is >= 200 and < 300)
        {
            return response;
        }

        var error = _mapper(response);

        // Admin consent only helps the user's own app registration; for a first-party sign-in a 403 is a plain refusal.
        if (error.Kind == PimErrorKind.ConsentRequired && identity.SignInMethod.Kind != SignInMethodKind.OwnApp)
        {
            throw new PimException(
                PimErrorKind.Forbidden,
                FirstPartyForbiddenMessage(response.BodyText, identity.SignInMethod));
        }

        throw error;
    }

    public static PimException MapError(HttpResponseData r)
    {
        ArgumentNullException.ThrowIfNull(r);

        switch (r.Status)
        {
            case 401:
                if (r.Header("WWW-Authenticate") is { } header && ClaimsChallenge.Parse(header) is { } claims)
                {
                    return new PimException(PimErrorKind.ClaimsChallenge, claims);
                }

                return new PimException(PimErrorKind.InteractionRequired);
            case 403:
                return new PimException(PimErrorKind.ConsentRequired);
            case 429:
            {
                var retryAfter = r.Header("Retry-After") is { } value ? $"{value}s" : "a few seconds";
                return new PimException(PimErrorKind.Network, $"Throttled by the service; retry in {retryAfter}");
            }

            case 400:
            {
                var text = r.BodyText;
                if (text.Contains("ActiveDurationTooShort", StringComparison.Ordinal)
                    || text.Contains("within 5 minutes", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("within five minutes", StringComparison.OrdinalIgnoreCase))
                {
                    return new PimException(
                        PimErrorKind.PolicyViolation,
                        "PIM requires a role to stay active for 5 minutes before it can be deactivated");
                }

                if (text.Contains("RoleAssignmentRequestPolicyValidationFailed", StringComparison.Ordinal)
                    || text.Contains("RoleAssignmentRequestAcrsValidationFailed", StringComparison.Ordinal))
                {
                    if (text.Contains("MfaRule", StringComparison.Ordinal) || text.Contains("Acrs", StringComparison.Ordinal))
                    {
                        // Graph did not hand us a claims header; ask the caller for a fresh interactive sign-in.
                        return new PimException(PimErrorKind.InteractionRequired);
                    }

                    return new PimException(PimErrorKind.PolicyViolation, GraphMessage(text) ?? "Policy validation failed");
                }

                if (text.Contains("RoleAssignmentDoesNotExist", StringComparison.Ordinal)
                    || text.Contains("RoleAssignmentRequestNotEligible", StringComparison.Ordinal))
                {
                    return new PimException(PimErrorKind.NotEligible);
                }

                return new PimException(PimErrorKind.Unexpected, GraphMessage(text) ?? text, 400);
            }

            default:
                return new PimException(PimErrorKind.Unexpected, GraphMessage(r.BodyText) ?? r.BodyText, r.Status);
        }
    }

    /// <summary>ARM variant: 403 is an RBAC denial at that scope, not a missing admin consent.</summary>
    public static PimException MapArmError(HttpResponseData r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.Status == 403
            ? new PimException(PimErrorKind.PolicyViolation, "Not permitted at this scope")
            : MapError(r);
    }

    private sealed record ErrorEnvelope(ErrorEnvelope.ErrorBody? Error)
    {
        public sealed record ErrorBody(string? Code, string? Message);
    }

    internal static string? GraphMessage(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ErrorEnvelope>(body, GraphJson.Options)?.Error?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Explains a 403 for a Microsoft first-party sign-in. <c>PermissionScopeNotGranted</c> means the Microsoft app
    /// is not pre-authorised for the scope; only an admin can grant it, so say what the user can do instead.
    /// </summary>
    internal static string FirstPartyForbiddenMessage(string body, SignInMethod method)
    {
        var message = GraphMessage(body)
            ?? (body.Length == 0 ? "HTTP 403" : body[..Math.Min(300, body.Length)]);

        if (!body.Contains("PermissionScopeNotGranted", StringComparison.Ordinal))
        {
            return message;
        }

        var match = MissingScope().Match(message);
        var scope = match.Success ? match.Groups[1].Value : "the required Graph permission";
        var alternative = method.Kind == SignInMethodKind.AzureCLI ? "try the Azure PowerShell app, " : "";
        return $"The {method.DisplayName} is not granted {scope} in this tenant. Entra roles can be read but not "
            + $"activated with it; use your own app registration, {alternative}or ask an admin to grant the "
            + "permission to the Microsoft enterprise app.";
    }

    [GeneratedRegex("missing permission scope ([A-Za-z.]+)")]
    private static partial Regex MissingScope();
}
