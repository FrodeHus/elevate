using System.Text.Json;
using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Audit.Rules;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Support;

namespace Elevate.Audit.Collectors;

public sealed record GroupData(
    IReadOnlyList<GroupRecord> Groups,
    IReadOnlyList<PrincipalRecord> Principals,
    string? PimUnavailableReason,
    string? GroupsUnavailableReason,
    int UnreadableGroups);

/// <summary>
/// Every role-assignable group plus every group reached from a seed, breadth-first through direct
/// members with a visited set (so nested groups are attributed and cycles terminate), each with its
/// PIM for Groups schedule instances.
/// </summary>
public sealed class GroupCollector(GraphTransport graph, Identity identity, string tenantId, Action<string>? verbose = null)
{
    internal sealed record WireGroup(string Id, string? DisplayName, bool? IsAssignableToRole, IReadOnlyList<string>? GroupTypes);

    internal sealed record WireGroupPim(string Id, string? PrincipalId, string? GroupId, string? AccessId, string? AssignmentType, DateTimeOffset? StartDateTime, DateTimeOffset? EndDateTime);

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
        while (queue.TryDequeue(out var id))
        {
            if (groups.ContainsKey(id))
            {
                continue;
            }

            var meta = known.TryGetValue(id, out var k) ? k : await GetGroupAsync(id, ct).ConfigureAwait(false);
            if (meta is null)
            {
                if (_groupsUnavailable is not null)
                {
                    break; // the scope is missing tenant-wide; asking for every other group would repeat the same 403
                }

                continue; // deleted between calls, or one group this account cannot read
            }

            var members = await graph.ListAllAsync<Wire.WirePrincipal>(identity, tenantId, GraphUrls.GroupMembers(id), _scopes, ct).ConfigureAwait(false);
            var direct = new List<GroupMemberRecord>(members.Count);
            foreach (var m in members)
            {
                var type = Wire.PrincipalTypeOf(m.OdataType);
                direct.Add(new GroupMemberRecord(m.Id, type));
                switch (type)
                {
                    case PrincipalType.Group:
                        queue.Enqueue(m.Id);
                        break;
                    case PrincipalType.User or PrincipalType.ServicePrincipal:
                        principals.TryAdd(m.Id, Wire.ToRecord(m));
                        break;
                }
            }

            var (status, assigned, eligible) = await PimAsync(id, ct).ConfigureAwait(false);
            groups[id] = new GroupRecord(
                id,
                meta.DisplayName ?? id,
                meta.IsAssignableToRole ?? false,
                meta.GroupTypes?.Contains("DynamicMembership", StringComparer.OrdinalIgnoreCase) == true,
                status,
                direct,
                assigned,
                eligible);
        }

        if (verbose is not null)
        {
            await CrossCheckTransitiveMembersAsync(topLevelIds, groups, ct).ConfigureAwait(false);
        }

        return new GroupData(groups.Values.ToList(), principals.Values.ToList(), _pimUnavailable, _groupsUnavailable, _unreadableGroups);
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

    private async Task<WireGroup?> GetGroupAsync(string id, CancellationToken ct)
    {
        try
        {
            var response = await graph.GetAsync(identity, tenantId, GraphUrls.Group(id), _scopes, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<WireGroup>(response.Body, GraphJson.Options);
        }
        catch (PimException e) when (IsMissingScope(e))
        {
            // GroupMember.Read.All is missing or restricted: every other group would refuse the same way.
            _groupsUnavailable = e.UserMessage;
            return null;
        }
        catch (PimException e) when (e.Status == 404 || e.Kind is PimErrorKind.Forbidden or PimErrorKind.ConsentRequired)
        {
            // Deleted, or a nested group the signed-in account cannot read; its parent still lists it as a member.
            _unreadableGroups++;
            return null;
        }
    }

    private async Task<(PimStatus Status, IReadOnlyList<GroupPimRecord> Assigned, IReadOnlyList<GroupPimRecord> Eligible)> PimAsync(string id, CancellationToken ct)
    {
        if (_pimUnavailable is not null)
        {
            return (PimStatus.Unknown, [], []);
        }

        try
        {
            var assigned = await graph.ListAllAsync<WireGroupPim>(identity, tenantId, GraphUrls.GroupPimAssignments(id), _scopes, ct).ConfigureAwait(false);
            var eligible = await graph.ListAllAsync<WireGroupPim>(identity, tenantId, GraphUrls.GroupPimEligibilities(id), _scopes, ct).ConfigureAwait(false);
            var status = assigned.Count + eligible.Count > 0 ? PimStatus.Onboarded : PimStatus.Unknown;
            return (status, assigned.Select(Map).ToList(), eligible.Select(Map).ToList());
        }
        catch (PimException e) when (IsMissingScope(e))
        {
            _pimUnavailable = e.UserMessage;
            return (PimStatus.Unknown, [], []);
        }
        catch (PimException e) when (e.Kind is PimErrorKind.Forbidden or PimErrorKind.ConsentRequired || e.Status == 404)
        {
            return (PimStatus.NotOnboarded, [], []);
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

    private static GroupPimRecord Map(WireGroupPim p) => new(
        p.Id,
        p.PrincipalId ?? string.Empty,
        p.AccessId ?? "member",
        Wire.AssignmentTypeOf(p.AssignmentType),
        p.StartDateTime,
        p.EndDateTime);
}
