using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Providers;
using Elevate.Core.Support;

namespace Elevate.App.ViewModels;

/// <summary>Pending approvals and decisions. Port of <c>AppModel+Approvals.swift</c>.</summary>
public sealed partial class AppModel
{
    /// <summary>
    /// Requests awaiting this user's decision, per tenant and kind. Session only: approvals are
    /// read opportunistically on every refresh and a failed read keeps the previous list.
    /// </summary>
    public Dictionary<TenantKey, Dictionary<RoleScopeKind, List<ApprovalRequest>>> Approvals { get; } = [];

    /// <summary>Requests whose Approve/Deny is currently being sent; their rows show a spinner.</summary>
    public HashSet<string> DecisionInFlight { get; } = [];

    /// <summary>Last decision failure per request id, shown on the row and in the window.</summary>
    public Dictionary<string, string> ApprovalErrors { get; } = [];

    /// <summary>Approval readers/deciders, one per kind, rebuilt with the coordinator when the client id changes.</summary>
    internal Dictionary<RoleScopeKind, IApprovalProvider> ApprovalProviders { get; private set; } = [];

    private static Dictionary<RoleScopeKind, IApprovalProvider> MakeApprovalProviders(IHttpClient http, ITokenProvider tokens) => new()
    {
        [RoleScopeKind.EntraDirectory] = new EntraApprovalProvider(http, tokens),
        [RoleScopeKind.Group] = new GroupApprovalProvider(http, tokens),
        [RoleScopeKind.AzureResource] = new AzureApprovalProvider(http, tokens),
    };

    // MARK: Derived

    public bool CollapsedApprovals => Settings.CollapsedApprovals;

    public void ToggleApprovals()
    {
        Settings.CollapsedApprovals = !Settings.CollapsedApprovals;
        Touch();
    }

    private IEnumerable<ApprovalRequest> AllApprovals => Approvals.Values.SelectMany(byKind => byKind.Values).SelectMany(list => list);

    /// <summary>
    /// Every pending request across accounts and tenants, oldest first, tenant name and id as
    /// tiebreaks so the order is stable between refreshes. Filtered by the panel search when active.
    /// </summary>
    public IReadOnlyList<ApprovalRequest> ApprovalsOrdered
    {
        get
        {
            var ordered = AllApprovals
                .OrderBy(r => r.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(ApprovalTenantName, StringComparer.Ordinal)
                .ThenBy(r => r.Id, StringComparer.Ordinal);
            if (!IsFiltering)
            {
                return [.. ordered];
            }

            return [.. ordered.Where(r =>
                PanelFilter.Matches(SearchQuery, r.TargetName)
                || PanelFilter.Matches(SearchQuery, r.RequesterName)
                || PanelFilter.Matches(SearchQuery, ApprovalTenantName(r)))];
        }
    }

    /// <summary>The tray badge's condition: everything pending, never narrowed by the panel search.</summary>
    public int PendingApprovalCount => AllApprovals.Count();

    /// <summary>
    /// Looks up a single request by id in the unfiltered set, so a request that falls outside the
    /// panel's current search still resolves for a window that is already showing it.
    /// </summary>
    public ApprovalRequest? Approval(string id) => AllApprovals.FirstOrDefault(r => r.Id == id);

    public string ApprovalTenantName(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Tenant(request.TenantKey)?.DisplayName ?? request.TenantKey.TenantId;
    }

    // MARK: Announcements

    /// <summary>
    /// Notifies once per request the user has not seen before. Only adds here: a single tenant's
    /// refresh knows nothing about the tenants that have not been read yet this launch, so pruning
    /// here would forget their ids and re-notify them a moment later. <see cref="RefreshAllAsync"/> prunes instead.
    /// </summary>
    internal async Task AnnounceNewApprovalsAsync()
    {
        var all = AllApprovals.ToList();
        var fresh = ApprovalDiff.NewRequests(Settings.SeenApprovalIds, all);
        // Mark every currently pending id as seen before the first await below, so a concurrently
        // refreshing tenant reading the seen set cannot re-announce these same requests.
        var seen = new HashSet<string>(Settings.SeenApprovalIds, StringComparer.Ordinal);
        seen.UnionWith(all.Select(r => r.Id));
        Settings.SeenApprovalIds = seen;
        foreach (var r in fresh)
        {
            try
            {
                await Notifier.NotifyAsync("Approval requested", $"{r.TargetName} for {r.RequesterName}, {ApprovalTenantName(r)}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogError($"Notifications: {e.Message}");
            }
        }
    }

    /// <summary>
    /// After a full sweep every tenant's list is current, so the seen set can be cut back to what is
    /// still pending; a request that is withdrawn and comes back notifies again.
    /// </summary>
    internal void PruneSeenApprovals()
    {
        var pending = AllApprovals.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(Settings.SeenApprovalIds, StringComparer.Ordinal);
        seen.IntersectWith(pending);
        Settings.SeenApprovalIds = seen;
    }

    /// <summary>
    /// Forgets the approvals of the matching tenants, along with their in-flight and error state,
    /// so a removed tenant or signed-out account leaves nothing in the pinned section.
    /// </summary>
    internal void DropApprovals(Func<TenantKey, bool> matches)
    {
        foreach (var key in Approvals.Keys.Where(matches).ToList())
        {
            foreach (var id in Approvals[key].Values.SelectMany(l => l).Select(r => r.Id))
            {
                DecisionInFlight.Remove(id);
                ApprovalErrors.Remove(id);
            }

            Approvals.Remove(key);
        }
    }

    // MARK: Decisions

    /// <summary>
    /// Sends one Approve or Deny. Returns true when the service accepted it; the caller closes its
    /// window on true and shows <see cref="ApprovalErrors"/> for the request on false.
    /// </summary>
    public async Task<bool> DecideAsync(ApprovalRequest request, bool approve, string justification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (DecisionInFlight.Contains(request.Id))
        {
            return Fail(request, "A decision for this request is already being sent.", "a decision is already being sent");
        }

        if (Identity(request.TenantKey.IdentityId) is not { } identity)
        {
            return Fail(request, "That account is no longer signed in.", "that account is no longer signed in");
        }

        if (!ApprovalProviders.TryGetValue(request.Kind, out var provider))
        {
            return Fail(request, "This request cannot be decided from Elevate.", "cannot be decided from Elevate");
        }

        var generation = ConfigGeneration;
        DecisionInFlight.Add(request.Id);
        ApprovalErrors.Remove(request.Id);
        Touch();
        try
        {
            await InteractionRetry.RunAsync(Tokens, identity, request.TenantKey.TenantId, provider.Scopes, async () =>
            {
                await provider.DecideAsync(request, approve, justification, identity, ct);
                return true;
            }, ct: ct);
            if (generation != ConfigGeneration)
            {
                return Fail(request, "Decision not completed; refresh and try again", "decision not completed");
            }

            // Drop the row now; the follow-up refresh re-lists it if a further approval stage remains.
            if (Approvals.TryGetValue(request.TenantKey, out var byKind) && byKind.TryGetValue(request.Kind, out var list))
            {
                list.RemoveAll(r => r.Id == request.Id);
            }

            ApprovalErrors.Remove(request.Id);
            Settings.LastApprovalJustification = justification;
            _ = RefreshAsync(request.TenantKey, new HashSet<RoleScopeKind> { request.Kind });
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            if (generation != ConfigGeneration)
            {
                return Fail(request, "Decision not completed; refresh and try again", "decision not completed");
            }

            var message = Describe(e);
            return Fail(request, message, message);
        }
        finally
        {
            DecisionInFlight.Remove(request.Id);
            Touch();
        }
    }

    private bool Fail(ApprovalRequest request, string shown, string logged)
    {
        ApprovalErrors[request.Id] = shown;
        LogError($"Approval {request.TargetName}: {logged}");
        Touch();
        return false;
    }
}
