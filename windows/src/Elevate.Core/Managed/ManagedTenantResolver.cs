using System.Text.Json;
using Elevate.Core.Discovery;
using Elevate.Core.Models;
using Elevate.Core.Networking;

namespace Elevate.Core.Managed;

/// <summary>
/// The outcome of resolving managed tenant entries: the entries that became tenant ids, keyed by
/// the entry exactly as it was configured, and the entries that could not be resolved, in order.
/// </summary>
public sealed record ManagedTenantResolution(
    IReadOnlyDictionary<string, string> Ids,
    IReadOnlyList<string> Unresolved);

/// <summary>
/// Turns managed tenant entries — tenant GUIDs or verified domains — into tenant ids, using the
/// unauthenticated OpenID configuration endpoint for domains. Successful lookups are cached for
/// the life of the resolver, so a domain is fetched once no matter how often it is asked for; a
/// failure is not cached, so a transient one can be retried on the next call. Port of the Swift
/// <c>ManagedTenantResolver</c> actor.
/// </summary>
public sealed class ManagedTenantResolver(IHttpClient http)
{
    private readonly IHttpClient _http = http;
    private readonly Dictionary<string, string> _cache = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ManagedTenantResolution> ResolveAsync(IEnumerable<string> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var unresolved = new List<string>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var entry in entries)
            {
                if (_cache.TryGetValue(entry, out var cached))
                {
                    ids[entry] = cached;
                    continue;
                }

                string id;
                try
                {
                    id = await TenantDiscovery.TenantIdAsync(entry, _http, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is PimException or JsonException or HttpRequestException)
                {
                    if (!unresolved.Contains(entry, StringComparer.Ordinal))
                    {
                        unresolved.Add(entry);
                    }

                    continue;
                }

                _cache[entry] = id;
                ids[entry] = id;
            }
        }
        finally
        {
            _gate.Release();
        }

        return new ManagedTenantResolution(ids, unresolved);
    }
}
