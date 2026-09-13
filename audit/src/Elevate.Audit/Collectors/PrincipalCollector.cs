using System.Text.Json;
using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Support;

namespace Elevate.Audit.Collectors;

/// <summary>Resolves bare object ids (ARM principals, limited-information members) to typed principals in one POST per 1000 ids.</summary>
public sealed class PrincipalCollector(GraphTransport graph, Identity identity, string tenantId)
{
    public const int ChunkSize = 1000;

    private static readonly string[] Types = ["user", "group", "servicePrincipal", "device"];

    public async Task<IReadOnlyList<PrincipalRecord>> ResolveAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var result = new List<PrincipalRecord>(ids.Count);
        foreach (var chunk in ids.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(ChunkSize))
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new { ids = chunk, types = Types });
            var response = await graph.PostAsync(identity, tenantId, GraphUrls.GetByIds, ClientIds.GraphReadScopes, body, ct).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<GraphTransport.Page<Wire.WirePrincipal>>(response.Body, GraphJson.Options);
            if (page?.Value is { } value)
            {
                result.AddRange(value.Select(Wire.ToRecord));
            }
        }

        return result;
    }
}
