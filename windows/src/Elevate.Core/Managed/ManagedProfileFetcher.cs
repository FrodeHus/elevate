using System.Text;
using Elevate.Core.Networking;

namespace Elevate.Core.Managed;

/// <summary>
/// Downloads the managed profile document an organization publishes over HTTPS and keeps the last
/// good body next to <c>state.json</c>, so a failed fetch falls back to what worked last
/// (design §7.1). Port of the Swift <c>ManagedProfileFetcher</c>.
/// </summary>
public sealed class ManagedProfileFetcher(IHttpClient http, string cachePath)
{
    /// <summary>A published document larger than this is refused rather than parsed.</summary>
    public const int MaxBytes = 1_048_576;

    private readonly IHttpClient _http = http;
    private readonly string _cachePath = cachePath;

    public string CachePath => _cachePath;

    /// <summary>The cached document, or null when there is no cache or it cannot be parsed.</summary>
    public ManagedProfileSet? Cached()
    {
        try
        {
            return ManagedProfileSet.Parse(File.ReadAllText(_cachePath));
        }
        catch (Exception ex) when (ex is ManagedProfileException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches, parses and caches the document. The cache is written only after a successful parse,
    /// so a bad response never replaces a good cached copy.
    /// </summary>
    /// <exception cref="ManagedProfileException">
    /// The URL is not HTTPS, the server did not answer 200, the body is over <see cref="MaxBytes"/>,
    /// or it is not a valid profile document.
    /// </exception>
    public async Task<ManagedProfileSet> FetchAsync(Uri url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!string.Equals(url.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new ManagedProfileException("managed profiles URL must be https");
        }

        var request = new HttpRequestData("GET", url, new Dictionary<string, string> { ["Accept"] = "application/json" }, null);
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.Status != 200)
        {
            throw new ManagedProfileException($"managed profiles fetch failed with HTTP {response.Status}");
        }

        if (response.Body.Length > MaxBytes)
        {
            throw new ManagedProfileException("managed profiles document is larger than 1 MB");
        }

        var text = Encoding.UTF8.GetString(response.Body);
        var set = ManagedProfileSet.Parse(text);
        Write(text);
        return set;
    }

    /// <summary>
    /// A temporary file plus a move, like <see cref="Storage.AppStateStore"/>, so a crash mid-write
    /// cannot truncate the cache. Best effort: a cache that cannot be written costs one fetch next
    /// launch, nothing more.
    /// </summary>
    private void Write(string text)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next launch fetches again.
        }
    }
}
