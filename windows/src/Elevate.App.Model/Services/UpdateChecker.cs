using System.Text.Json;
using Elevate.Core.Models;
using Elevate.Core.Networking;

namespace Elevate.App.Services;

/// <summary>
/// Reads the latest published Windows release of Elevate from GitHub. Port of the macOS
/// <c>UpdateChecker</c>, over the releases list rather than <c>releases/latest</c>: the repository
/// tags macOS releases too, and only <c>windows-v*</c> tags describe this app.
/// </summary>
public sealed class UpdateChecker(IHttpClient http, Uri? url = null)
{
    public static readonly Uri ReleasesUrl = new("https://api.github.com/repos/FrodeHus/elevate/releases?per_page=20");

    public const string TagPrefix = "windows-v";

    public sealed record Release(string Tag, Uri Url)
    {
        /// <summary>"1.2.3" for the tag "windows-v1.2.3".</summary>
        public string Version => Tag[TagPrefix.Length..];
    }

    private sealed record Wire(string? Tag_name, Uri? Html_url, bool? Draft, bool? Prerelease);

    private readonly Uri _url = url ?? ReleasesUrl;

    /// <summary>
    /// The newest Windows release, or null when there is none yet (including a 404 on a repository
    /// without releases). Throws on any other failure (offline, rate limited, unreadable body).
    /// </summary>
    public async Task<Release?> LatestAsync(CancellationToken ct = default)
    {
        var request = new HttpRequestData("GET", _url, new Dictionary<string, string>
        {
            ["Accept"] = "application/vnd.github+json",
            ["User-Agent"] = "Elevate",
        }, null);
        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.Status == 404)
        {
            return null;
        }

        if (response.Status is < 200 or >= 300)
        {
            throw new PimException(PimErrorKind.Unexpected, response.BodyText, response.Status);
        }

        List<Wire>? releases;
        try
        {
            releases = JsonSerializer.Deserialize<List<Wire>>(response.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            throw new PimException(PimErrorKind.Unexpected, "Could not read the release information from GitHub", response.Status);
        }

        var release = releases?.FirstOrDefault(r =>
            r.Tag_name is { } tag && tag.StartsWith(TagPrefix, StringComparison.Ordinal)
            && r.Html_url is not null && r.Draft != true && r.Prerelease != true);
        return release is null ? null : new Release(release.Tag_name!, release.Html_url!);
    }
}
