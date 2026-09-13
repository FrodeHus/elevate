using System.Text.Json;
using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Support;

namespace Elevate.Audit.Collectors;

/// <summary>
/// Runs the collectors and assembles the <see cref="Snapshot"/>. Directory roles are required; Azure,
/// PIM for Groups and principal resolution degrade to a <see cref="SkippedSource"/> entry.
/// </summary>
public sealed class Scanner(
    GraphTransport graph,
    GraphTransport? arm,
    Identity identity,
    string tenantId,
    AuditOptions options,
    string toolVersion,
    Action<string> note,
    TimeProvider? clock = null)
{
    private sealed record WireOrganization(string Id, string? DisplayName);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<Snapshot> ScanAsync(CancellationToken ct)
    {
        var skipped = new List<SkippedSource>();

        note("Reading directory roles…");
        var directoryTask = new DirectoryRoleCollector(graph, identity, tenantId).CollectAsync(ct);
        var azureTask = arm is not null && !options.SkipAzure ? CollectAzureAsync(arm, skipped, ct) : Task.FromResult<AzureData?>(null);
        var tenantTask = ReadTenantAsync(ct);

        var directory = await directoryTask.ConfigureAwait(false);
        var azure = await azureTask.ConfigureAwait(false);
        var tenant = await tenantTask.ConfigureAwait(false);
        if (azure is null && options.SkipAzure)
        {
            skipped.Add(new SkippedSource("azure", "skipped with --skip-azure"));
        }

        var principals = directory.Principals.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var seeds = directory.Assignments.Where(a => a.IsPermanent)
            .Select(a => a.PrincipalId)
            .Where(id => principals.TryGetValue(id, out var p) && p.Type == PrincipalType.Group)
            .Concat(azure?.Assignments.Where(a => string.Equals(a.PrincipalType, "Group", StringComparison.OrdinalIgnoreCase)).Select(a => a.PrincipalId) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        note("Expanding groups…");
        var groups = await new GroupCollector(graph, identity, tenantId).CollectAsync(seeds, ct).ConfigureAwait(false);
        foreach (var p in groups.Principals)
        {
            principals.TryAdd(p.Id, p);
        }

        if (groups.PimUnavailableReason is { } reason)
        {
            skipped.Add(new SkippedSource("pim-for-groups", reason));
        }

        if (groups.GroupsUnavailableReason is { } groupsReason)
        {
            skipped.Add(new SkippedSource("groups", groupsReason));
        }

        if (groups.UnreadableGroups > 0)
        {
            skipped.Add(new SkippedSource("groups", $"{groups.UnreadableGroups} nested group(s) could not be read; their members are not included."));
        }

        var referenced = directory.Assignments.Select(a => a.PrincipalId)
            .Concat(directory.Eligibilities.Select(e => e.PrincipalId))
            .Concat(groups.Groups.SelectMany(g => g.PimAssignments.Concat(g.PimEligibilities)).Select(p => p.PrincipalId))
            .Concat(azure?.Assignments.Select(a => a.PrincipalId) ?? [])
            .Concat(azure?.Eligibilities.Select(a => a.PrincipalId) ?? [])
            .Where(id => !string.IsNullOrEmpty(id) && !principals.ContainsKey(id) && !groups.Groups.Any(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (referenced.Count > 0)
        {
            note($"Resolving {referenced.Count} principals…");
            try
            {
                foreach (var p in await new PrincipalCollector(graph, identity, tenantId).ResolveAsync(referenced, ct).ConfigureAwait(false))
                {
                    principals.TryAdd(p.Id, p);
                }
            }
            catch (PimException e)
            {
                skipped.Add(new SkippedSource("principals", $"Some principals could not be resolved to names: {e.UserMessage}"));
            }
        }

        // Neither $expand=principal nor getByIds projects userType, so a user that still has no
        // accountEnabled was never told apart from a guest; read those back explicitly.
        var unprojected = principals.Values.Where(p => p.Type == PrincipalType.User && p.AccountEnabled is null).Select(p => p.Id).ToList();
        if (unprojected.Count > 0)
        {
            note($"Reading {unprojected.Count} user records…");
            try
            {
                foreach (var p in await new PrincipalCollector(graph, identity, tenantId).EnrichUsersAsync(unprojected, ct).ConfigureAwait(false))
                {
                    principals[p.Id] = p;
                }
            }
            catch (PimException e)
            {
                skipped.Add(new SkippedSource("principals", $"Guest status and sign-in status could not be read for some users: {e.UserMessage}"));
            }
        }

        skipped.AddRange((azure?.Notes ?? []).Select(n => new SkippedSource("azure-management-groups", n)));

        return new Snapshot(
            Snapshot.KindMarker,
            toolVersion,
            tenant,
            identity.Upn,
            _clock.GetUtcNow(),
            directory.Definitions,
            directory.Assignments,
            directory.Eligibilities,
            groups.Groups,
            principals.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList(),
            azure?.Scopes ?? [],
            azure?.RoleDefinitions ?? [],
            azure?.Assignments ?? [],
            azure?.Eligibilities ?? [],
            skipped);
    }

    private string FallbackTenantId() => Guid.TryParse(tenantId, out _) ? tenantId : identity.HomeTenantId;

    private async Task<TenantInfo> ReadTenantAsync(CancellationToken ct)
    {
        try
        {
            var response = await graph.GetAsync(identity, tenantId, GraphUrls.Organization, ClientIds.GraphReadScopes, ct).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<GraphTransport.Page<WireOrganization>>(response.Body, GraphJson.Options);
            var org = page?.Value.FirstOrDefault();
            return new TenantInfo(org?.Id ?? FallbackTenantId(), org?.DisplayName);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new TenantInfo(FallbackTenantId(), null);
        }
    }

    private async Task<AzureData?> CollectAzureAsync(GraphTransport transport, List<SkippedSource> skipped, CancellationToken ct)
    {
        try
        {
            note("Reading Azure role assignments…");
            return await new AzureCollector(transport, identity, tenantId).CollectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (skipped)
            {
                skipped.Add(new SkippedSource("azure", e is PimException pe ? pe.UserMessage : e.Message));
            }

            return null;
        }
    }
}
