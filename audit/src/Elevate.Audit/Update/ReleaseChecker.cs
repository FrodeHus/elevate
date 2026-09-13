using System.Text.Json;
using Elevate.Core.Models;
using Elevate.Core.Networking;

namespace Elevate.Audit.Update;

/// <summary>The newest published release that ships an elevate-audit archive. Same shape as the CLI's checker, different asset prefix.</summary>
public sealed class ReleaseChecker(IHttpClient http, Uri? url = null)
{
    public const string AssetPrefix = "elevate-audit-";

    public static readonly Uri ReleasesUrl = new("https://api.github.com/repos/FrodeHus/elevate/releases?per_page=20");

    public sealed record Release(string Tag, Uri Url)
    {
        public string Version => Tag.StartsWith('v') || Tag.StartsWith('V') ? Tag[1..] : Tag;
    }

    private sealed record Asset(string? Name);

    private sealed record Wire(string? Tag_name, Uri? Html_url, bool? Draft, bool? Prerelease, List<Asset>? Assets);

    private readonly Uri _url = url ?? ReleasesUrl;

    public async Task<Release?> LatestAsync(CancellationToken ct = default)
    {
        var request = new HttpRequestData("GET", _url, new Dictionary<string, string>
        {
            ["Accept"] = "application/vnd.github+json",
            ["User-Agent"] = "elevate-audit",
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
            r.Tag_name is { } tag && tag.StartsWith('v')
            && r.Html_url is not null && r.Draft != true && r.Prerelease != true
            && r.Assets?.Any(a => a.Name?.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) == true) == true);
        return release is null ? null : new Release(release.Tag_name!, release.Html_url!);
    }

    /// <summary>Numeric comparison of the dotted prefix; build metadata and pre-release suffixes are ignored.</summary>
    public static bool IsNewer(string tag, string current)
    {
        static Version Parse(string text)
        {
            var core = text.TrimStart('v', 'V');
            var cut = core.IndexOfAny(['-', '+']);
            if (cut >= 0)
            {
                core = core[..cut];
            }

            var parts = core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
            while (parts.Count < 3)
            {
                parts.Add(0);
            }

            return new Version(parts[0], parts[1], parts[2]);
        }

        return Parse(tag) > Parse(current);
    }
}
