using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;

namespace Elevate.Core.Providers;

/// <summary>PIM for Groups activation requests awaiting the signed-in user's approval.</summary>
public sealed class GroupApprovalProvider : IApprovalProvider
{
    internal const string ApprovalsPath = GroupProvider.Base + "/assignmentApprovals";

    private const string PendingPath = GroupProvider.Base
        + "/assignmentScheduleRequests/filterByCurrentUser(on='approver')"
        + "?$filter=status eq 'PendingApproval'&$expand=group,principal";

    private readonly GraphTransport _transport;

    public GroupApprovalProvider(IHttpClient http, ITokenProvider tokens)
        => _transport = new GraphTransport(http, tokens);

    public RoleScopeKind Kind => RoleScopeKind.Group;

    public IReadOnlyList<string> Scopes => Auth.Scopes.GroupAll;

    private sealed record Request(
        string Id,
        string? Action,
        string? PrincipalId,
        string? GroupId,
        string? AccessId,
        string? Justification,
        DateTimeOffset? CreatedDateTime,
        ScheduleInfo? ScheduleInfo,
        GraphApprovals.Named? Group,
        GraphApprovals.Principal? Principal);

    public async Task<IReadOnlyList<ApprovalRequest>> PendingApprovalsAsync(
        Identity identity, TenantContext tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        var items = await _transport.ListAllAsync<Request>(
            identity, tenant.TenantId, _transport.GraphUrl(PendingPath), Scopes, ct).ConfigureAwait(false);

        return [.. items.Select(r => new ApprovalRequest(
            r.Id, tenant.Key, Kind, GraphApprovals.Action(r.Action),
            targetName: r.Group?.DisplayName ?? r.GroupId ?? r.Id,
            requesterName: GraphApprovals.Requester(r.Principal, r.PrincipalId),
            scopeCaption: r.AccessId is { } access ? (GroupProvider.Access(access) == GroupAccess.Owner ? "owner" : "member") : null,
            justification: r.Justification,
            requestedDuration: GraphApprovals.Duration(r.ScheduleInfo),
            createdAt: r.CreatedDateTime,
            decisionRef: r.Id))];
    }

    public Task DecideAsync(ApprovalRequest request, bool approve, string justification, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GraphApprovals.DecideAsync(request, approve, justification, identity, _transport, Scopes, ApprovalsPath, ct);
    }
}
