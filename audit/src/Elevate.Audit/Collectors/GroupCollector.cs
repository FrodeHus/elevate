using System.Text.Json;
using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Audit.Rules;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Support;

namespace Elevate.Audit.Collectors;

/// <summary>
/// <paramref name="Groups"/>' own <c>DirectMembers</c> may reference a group id that is not itself a key
/// in <paramref name="Groups"/>: a nested group can be skipped (deleted, or unreadable by this account —
/// see <see cref="UnreadableGroups"/>) after its parent already recorded it as a member. Consumers that
/// walk <c>DirectMembers</c> (see <see cref="Rules.GroupExpansion"/>) must tolerate a missing lookup.
/// </summary>
public sealed record GroupData(
    IReadOnlyList<GroupRecord> Groups,
    IReadOnlyList<PrincipalRecord> Principals,
    string? PimUnavailableReason,
    string? GroupsUnavailableReason,
    int UnreadableGroups);

/// <summary>
/// Every role-assignable group plus every group reached from a seed, breadth-first through direct
/// members with a visited set (so nested groups are attributed and cycles terminate), each with its
/// PIM for Groups schedule instances. Rather than draining the queue one group at a time, each pass
/// takes up to <see cref="MaxConcurrency"/> ids off the queue — regardless of which breadth-first level
/// they came from — and fetches them concurrently, since a tenant with a deep or wide group hierarchy is
/// otherwise dominated by network round trips.
/// </summary>
/// <param name="progress">
/// Optional progress note, invoked from the single thread that drives <see cref="CollectAsync"/> (never
/// concurrently) every <see cref="ProgressEvery"/> groups recorded.
/// </param>
public sealed class GroupCollector(GraphTransport graph, Identity identity, string tenantId, Action<string>? verbose = null, Action<string>? progress = null)
{
    internal sealed record WireGroup(string Id, string? DisplayName, bool? IsAssignableToRole, IReadOnlyList<string>? GroupTypes, bool? SecurityEnabled, bool? MailEnabled, string? Visibility);

    internal sealed record WireGroupPim(string Id, string? PrincipalId, string? GroupId, string? AccessId, string? AssignmentType, DateTimeOffset? StartDateTime, DateTimeOffset? EndDateTime);

    private enum FetchStatus { Ok, Unreadable, TenantUnavailable }

    /// <summary>
    /// The outcome of processing one group id, produced by a task that may run concurrently with up to
    /// <see cref="MaxConcurrency"/> - 1 siblings. It carries no shared mutable state; everything it found
    /// is merged into the collector's dictionaries and counters by the single thread driving the wave
    /// loop in <see cref="CollectAsync"/>, so nothing here needs to be thread-safe on its own.
    /// </summary>
    private sealed record GroupOutcome(
        string Id,
        GroupRecord? Record,
        IReadOnlyList<string> NestedGroupIds,
        IReadOnlyList<PrincipalRecord> Principals,
        bool Unreadable,
        string? TenantWideMessage,
        string? PimUnavailableMessage)
    {
        public static GroupOutcome TenantWide(string id, string message) => new(id, null, [], [], false, message, null);

        public static GroupOutcome UnreadableGroup(string id) => new(id, null, [], [], true, null, null);

        public static GroupOutcome Skipped(string id) => new(id, null, [], [], false, null, null);
    }

    /// <summary>Up to this many groups are fetched from Graph at once per breadth-first level.</summary>
    private const int MaxConcurrency = 4;

    private const int ProgressEvery = 25;

    private readonly IReadOnlyList<string> _scopes = ClientIds.GraphReadScopes;
    private string? _pimUnavailable;
    private string? _groupsUnavailable;
    private int _unreadableGroups;

    public async Task<GroupData> CollectAsync(IEnumerable<string> seedGroupIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seedGroupIds);
        var seedIds = seedGroupIds as ICollection<string> ?? seedGroupIds.ToList();
        var known = new Dictionary<string, WireGroup>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (var g in await graph.ListAllAsync<WireGroup>(identity, tenantId, GraphUrls.RoleAssignableGroups, _scopes, ct).ConfigureAwait(false))
        {
            known[g.Id] = g;
            queue.Enqueue(g.Id);
        }

        var topLevelIds = new HashSet<string>(known.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var id in seedIds)
        {
            topLevelIds.Add(id);
            queue.Enqueue(id);
        }

        var groups = new Dictionary<string, GroupRecord>(StringComparer.OrdinalIgnoreCase);
        var principals = new Dictionary<string, PrincipalRecord>(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastProgressAt = 0;

        while (queue.Count > 0)
        {
            var wave = new List<string>(MaxConcurrency);
            while (wave.Count < MaxConcurrency && queue.TryDequeue(out var candidate))
            {
                // A group can be enqueued more than once (nested in a cycle, or listed by more than one
                // parent); `claimed` is only ever touched here, on the single thread driving the wave
                // loop, so this is an ordinary set membership check, not a race.
                if (claimed.Add(candidate))
                {
                    wave.Add(candidate);
                }
            }

            if (wave.Count == 0)
            {
                continue;
            }

            var outcomes = await Task.WhenAll(wave.Select(id => ProcessGroupAsync(id, known, ct))).ConfigureAwait(false);

            // Every outcome in this wave was fetched concurrently, so a sibling that reveals the scope is
            // missing tenant-wide tells us nothing about whether the *other* members of the wave "happened
            // before" or "after" it — but their own outcomes are independently trustworthy (each is one
            // group's own successful read), so they are still merged. Only the offending member itself is
            // dropped, and no further wave is dispatched once one is seen.
            var sawTenantWide = false;
            foreach (var outcome in outcomes)
            {
                if (outcome.TenantWideMessage is { } message)
                {
                    _groupsUnavailable ??= message;
                    sawTenantWide = true;
                    continue;
                }

                if (outcome.Unreadable)
                {
                    _unreadableGroups++;
                }

                _pimUnavailable ??= outcome.PimUnavailableMessage;

                if (outcome.Record is not { } record)
                {
                    continue;
                }

                groups[outcome.Id] = record;
                foreach (var p in outcome.Principals)
                {
                    principals.TryAdd(p.Id, p);
                }

                foreach (var nested in outcome.NestedGroupIds)
                {
                    queue.Enqueue(nested);
                }
            }

            if (sawTenantWide)
            {
                break;
            }

            if (progress is not null)
            {
                while (groups.Count - lastProgressAt >= ProgressEvery)
                {
                    lastProgressAt += ProgressEvery;
                    progress($"Expanded {groups.Count} groups…");
                }
            }
        }

        if (verbose is not null)
        {
            await CrossCheckTransitiveMembersAsync(topLevelIds, groups, ct).ConfigureAwait(false);
        }

        return new GroupData(
            groups.Values.OrderBy(g => g.Id, StringComparer.Ordinal).ToList(),
            principals.Values.ToList(),
            _pimUnavailable,
            _groupsUnavailable,
            _unreadableGroups);
    }

    /// <summary>Fetches and assembles everything for one group id, without touching any shared state.</summary>
    private async Task<GroupOutcome> ProcessGroupAsync(string id, IReadOnlyDictionary<string, WireGroup> known, CancellationToken ct)
    {
        WireGroup? meta;
        if (known.TryGetValue(id, out var k))
        {
            meta = k;
        }
        else
        {
            var (fetched, status, message) = await GetGroupAsync(id, ct).ConfigureAwait(false);
            switch (status)
            {
                case FetchStatus.TenantUnavailable:
                    return GroupOutcome.TenantWide(id, message!);
                case FetchStatus.Unreadable:
                    return GroupOutcome.UnreadableGroup(id);
            }

            meta = fetched;
            if (meta is null)
            {
                return GroupOutcome.Skipped(id); // deleted between calls
            }
        }

        var (members, membersStatus, membersMessage) = await GetGroupMembersAsync(id, ct).ConfigureAwait(false);
        if (membersStatus == FetchStatus.TenantUnavailable)
        {
            return GroupOutcome.TenantWide(id, membersMessage!);
        }

        var memberList = members ?? [];
        var direct = new List<GroupMemberRecord>(memberList.Count);
        var nested = new List<string>();
        var resolved = new List<PrincipalRecord>();
        foreach (var m in memberList)
        {
            var type = Wire.PrincipalTypeOf(m.OdataType);
            direct.Add(new GroupMemberRecord(m.Id, type));
            switch (type)
            {
                case PrincipalType.Group:
                    nested.Add(m.Id);
                    break;
                case PrincipalType.User or PrincipalType.ServicePrincipal:
                    resolved.Add(Wire.ToRecord(m));
                    break;
            }
        }

        var (pimStatus, assigned, eligible, pimUnavailableMessage) = await PimAsync(id, ct).ConfigureAwait(false);
        var record = new GroupRecord(
            id,
            meta.DisplayName ?? id,
            meta.IsAssignableToRole ?? false,
            meta.GroupTypes?.Contains("DynamicMembership", StringComparer.OrdinalIgnoreCase) == true,
            pimStatus,
            direct,
            assigned,
            eligible,
            meta.SecurityEnabled ?? true,
            meta.MailEnabled ?? false,
            meta.Visibility);

        return new GroupOutcome(id, record, nested, resolved, membersStatus == FetchStatus.Unreadable, null, pimUnavailableMessage);
    }

    /// <summary>
    /// Spec §4.2: <c>transitiveMembers</c> flattens the path so it is never used for findings, only as a
    /// verbose-mode sanity check that the breadth-first walk (limited to groups it could read) found the
    /// same number of user/service-principal members as Graph's own flattened count.
    /// </summary>
    private async Task CrossCheckTransitiveMembersAsync(IEnumerable<string> topLevelIds, Dictionary<string, GroupRecord> groups, CancellationToken ct)
    {
        var expansion = new GroupExpansion(groups);
        foreach (var id in topLevelIds)
        {
            if (!groups.TryGetValue(id, out var group))
            {
                continue;
            }

            var walked = expansion.Expand(id).Count;
            var members = await graph.ListAllAsync<Wire.WirePrincipal>(identity, tenantId, GraphUrls.GroupTransitiveMembers(id), _scopes, ct).ConfigureAwait(false);
            var flattened = members.Count(m => Wire.PrincipalTypeOf(m.OdataType) is PrincipalType.User or PrincipalType.ServicePrincipal);
            if (walked != flattened)
            {
                verbose!($"{group.DisplayName}: walk found {walked} members, transitiveMembers reports {flattened}");
            }
        }
    }

    private async Task<(WireGroup? Meta, FetchStatus Status, string? Message)> GetGroupAsync(string id, CancellationToken ct)
    {
        try
        {
            var response = await graph.GetAsync(identity, tenantId, GraphUrls.Group(id), _scopes, ct).ConfigureAwait(false);
            return (JsonSerializer.Deserialize<WireGroup>(response.Body, GraphJson.Options), FetchStatus.Ok, null);
        }
        catch (PimException e) when (IsMissingScope(e))
        {
            // GroupMember.Read.All is missing or restricted: every other group would refuse the same way.
            return (null, FetchStatus.TenantUnavailable, e.UserMessage);
        }
        catch (PimException e) when (IsPerGroupRefusal(e))
        {
            // Deleted, or a nested group the signed-in account cannot read; its parent still lists it as a member.
            return (null, FetchStatus.Unreadable, null);
        }
    }

    /// <summary>
    /// Same classification as <see cref="GetGroupAsync"/>: a tenant-wide missing scope stops the caller
    /// from asking further, while one group refusing its members is counted as unreadable and the group
    /// is still recorded, with no members.
    /// </summary>
    private async Task<(IReadOnlyList<Wire.WirePrincipal>? Members, FetchStatus Status, string? Message)> GetGroupMembersAsync(string id, CancellationToken ct)
    {
        try
        {
            return (await graph.ListAllAsync<Wire.WirePrincipal>(identity, tenantId, GraphUrls.GroupMembers(id), _scopes, ct).ConfigureAwait(false), FetchStatus.Ok, null);
        }
        catch (PimException e) when (IsMissingScope(e))
        {
            return (null, FetchStatus.TenantUnavailable, e.UserMessage);
        }
        catch (PimException e) when (IsPerGroupRefusal(e))
        {
            return (null, FetchStatus.Unreadable, null);
        }
    }

    /// <summary>
    /// <see cref="_pimUnavailable"/> is only ever assigned by the single thread merging a wave's results,
    /// but this read can happen concurrently from more than one in-flight <see cref="ProcessGroupAsync"/>
    /// task within the same wave. That is a benign race: it is purely an optimisation to skip a call
    /// already known to fail, so at worst a few sibling tasks in the same wave each make one redundant
    /// PIM request before the field is set for the next wave.
    /// </summary>
    private async Task<(PimStatus Status, IReadOnlyList<GroupPimRecord> Assigned, IReadOnlyList<GroupPimRecord> Eligible, string? PimUnavailableMessage)> PimAsync(string id, CancellationToken ct)
    {
        if (_pimUnavailable is not null)
        {
            return (PimStatus.Unknown, [], [], null);
        }

        try
        {
            var assigned = await graph.ListAllAsync<WireGroupPim>(identity, tenantId, GraphUrls.GroupPimAssignments(id), _scopes, ct).ConfigureAwait(false);
            var eligible = await graph.ListAllAsync<WireGroupPim>(identity, tenantId, GraphUrls.GroupPimEligibilities(id), _scopes, ct).ConfigureAwait(false);
            var status = assigned.Count + eligible.Count > 0 ? PimStatus.Onboarded : PimStatus.Unknown;
            return (status, assigned.Select(Map).ToList(), eligible.Select(Map).ToList(), null);
        }
        catch (PimException e) when (IsMissingScope(e))
        {
            return (PimStatus.Unknown, [], [], e.UserMessage);
        }
        catch (PimException e) when (e.Kind is PimErrorKind.Forbidden or PimErrorKind.ConsentRequired || e.Status == 404)
        {
            return (PimStatus.NotOnboarded, [], [], null);
        }
    }

    /// <summary>
    /// Core phrases a PermissionScopeNotGranted 403 as "… is not granted &lt;scope&gt; …"; that is a
    /// tenant-wide condition, not one group's. The wording lives in Elevate.Core, so the tenant-wide
    /// tests here (<c>…TheGroupScopeIsMissing…</c>, <c>…ThePimScopeIsMissing…</c>) are the drift guard:
    /// if Core rephrases the message they fail rather than silently reclassifying the whole tenant.
    /// </summary>
    private static bool IsMissingScope(PimException e) =>
        e.Kind == PimErrorKind.Forbidden && e.UserMessage.Contains("is not granted", StringComparison.Ordinal);

    /// <summary>One group refusing to be read: deleted, or this account cannot see it — not a tenant-wide condition.</summary>
    private static bool IsPerGroupRefusal(PimException e) =>
        e.Status == 404 || e.Kind is PimErrorKind.Forbidden or PimErrorKind.ConsentRequired;

    private static GroupPimRecord Map(WireGroupPim p) => new(
        p.Id,
        p.PrincipalId ?? string.Empty,
        p.AccessId ?? "member",
        Wire.AssignmentTypeOf(p.AssignmentType),
        p.StartDateTime,
        p.EndDateTime);
}
