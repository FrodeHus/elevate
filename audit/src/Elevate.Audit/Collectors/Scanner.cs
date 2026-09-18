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
/// <param name="note">
/// Progress text for the user. Directory, Azure and tenant collection run as concurrent tasks, so this
/// may be invoked from more than one task at a time; callers must be safe to call from any thread
/// (e.g. writing to <see cref="Console"/>, which is inherently thread-safe).
/// </param>
/// <param name="verbose">
/// Same concurrency note as <paramref name="note"/>: nullable, and may be invoked from concurrent tasks.
/// </param>
public sealed class Scanner(
    GraphTransport graph,
    GraphTransport? arm,
    Identity identity,
    string tenantId,
    AuditOptions options,
    string toolVersion,
    Action<string> note,
    TimeProvider? clock = null,
    Action<string>? verbose = null,
    string? activationHistoryDeclined = null)
{
    private sealed record WireOrganization(string Id, string? DisplayName);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Written by <see cref="CollectActivationsAsync"/> only, and read after it has completed.</summary>
    private string? _activationsUnavailable;

    public async Task<Snapshot> ScanAsync(CancellationToken ct)
    {
        // Written concurrently only by CollectAzureAsync below (the sole task that touches it while
        // directoryTask/azureTask/tenantTask are all in flight); every other Add happens after `await
        // azureTask` has completed, so there is never more than one writer at a time and no lock is needed.
        var skipped = new List<SkippedSource>();

        var scannedAt = _clock.GetUtcNow();
        var since = scannedAt.AddDays(-Math.Max(1, options.LookbackDays));

        note("Reading directory roles…");
        var directoryTask = new DirectoryRoleCollector(graph, identity, tenantId).CollectAsync(ct);
        var azureTask = arm is not null && !options.SkipAzure ? CollectAzureAsync(arm, since, skipped, ct) : Task.FromResult<AzureData?>(null);
        var tenantTask = ReadTenantAsync(ct);
        var activationTask = activationHistoryDeclined is null ? CollectActivationsAsync(since, ct) : Task.FromResult<IReadOnlyList<ActivationRecord>?>(null);

        var directory = await directoryTask.ConfigureAwait(false);
        var azure = await azureTask.ConfigureAwait(false);
        var tenant = await tenantTask.ConfigureAwait(false);
        var directoryActivations = await activationTask.ConfigureAwait(false);
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
        var groups = await new GroupCollector(graph, identity, tenantId, verbose, note).CollectAsync(seeds, ct).ConfigureAwait(false);
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

        var history = BuildHistory(since, scannedAt, directoryActivations, azure, activationHistoryDeclined ?? _activationsUnavailable, skipped);

        return new Snapshot(
            Snapshot.KindMarker,
            toolVersion,
            tenant,
            identity.Upn,
            scannedAt,
            directory.Definitions,
            directory.Assignments,
            directory.Eligibilities,
            groups.Groups,
            principals.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList(),
            azure?.Scopes ?? [],
            azure?.RoleDefinitions ?? [],
            azure?.Assignments ?? [],
            azure?.Eligibilities ?? [],
            skipped)
        { Activations = history };
    }

    /// <summary>
    /// What the lookback saw, per role system: Entra and PIM for Groups come from the directory audit log,
    /// Azure from ARM's request history. A system whose history could not be read is left out of
    /// <see cref="ActivationHistory.Systems"/>, so its eligibilities are skipped rather than reported as
    /// never used; when no system is readable at all there is no history, and the rules do not run.
    /// </summary>
    private static ActivationHistory? BuildHistory(
        DateTimeOffset since,
        DateTimeOffset until,
        IReadOnlyList<ActivationRecord>? directoryActivations,
        AzureData? azure,
        string? directoryUnavailable,
        List<SkippedSource> skipped)
    {
        var systems = new List<RoleSystem>();
        var activations = new List<ActivationRecord>();
        if (directoryActivations is not null)
        {
            systems.Add(RoleSystem.Entra);
            systems.Add(RoleSystem.Group);
            activations.AddRange(directoryActivations);
        }
        else
        {
            skipped.Add(new SkippedSource("activation-history", directoryUnavailable ?? "PIM activation history could not be read, so unused Entra and group eligibilities are not reported."));
        }

        if (azure is { ActivationsReadable: true })
        {
            systems.Add(RoleSystem.Azure);
            activations.AddRange(azure.Activations);
        }
        else if (azure is not null)
        {
            skipped.Add(new SkippedSource("activation-history", "Azure PIM request history could not be read, so unused Azure eligibilities are not reported."));
        }

        return systems.Count == 0 ? null : new ActivationHistory(since, until, systems, activations);
    }

    private async Task<IReadOnlyList<ActivationRecord>?> CollectActivationsAsync(DateTimeOffset since, CancellationToken ct)
    {
        try
        {
            return await new ActivationCollector(graph, identity, tenantId).CollectAsync(since, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _activationsUnavailable = e is PimException pe
                ? $"PIM activation history could not be read: {pe.UserMessage}"
                : $"PIM activation history could not be read: {e.Message}";
            return null;
        }
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

    private async Task<AzureData?> CollectAzureAsync(GraphTransport transport, DateTimeOffset since, List<SkippedSource> skipped, CancellationToken ct)
    {
        try
        {
            note("Reading Azure role assignments…");
            return await new AzureCollector(transport, identity, tenantId).CollectAsync(since, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            skipped.Add(new SkippedSource("azure", e is PimException pe ? pe.UserMessage : e.Message));
            return null;
        }
    }
}
