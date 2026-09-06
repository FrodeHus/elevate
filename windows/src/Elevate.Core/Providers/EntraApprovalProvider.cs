using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;

namespace Elevate.Core.Providers;

/// <summary>Entra directory role activation requests awaiting the signed-in user's approval.</summary>
public sealed class EntraApprovalProvider : IApprovalProvider
{
    internal const string ApprovalsPath = "/roleManagement/directory/roleAssignmentApprovals";

    private const string PendingPath =
        "/roleManagement/directory/roleAssignmentScheduleRequests/filterByCurrentUser(on='approver')"
        + "?$filter=status eq 'PendingApproval'&$expand=roleDefinition,principal";

    private readonly GraphTransport _transport;

    public EntraApprovalProvider(IHttpClient http, ITokenProvider tokens)
        => _transport = new GraphTransport(http, tokens);

    public RoleScopeKind Kind => RoleScopeKind.EntraDirectory;

    public IReadOnlyList<string> Scopes => Auth.Scopes.GraphAll;

    private sealed record Request(
        string Id,
        string? Action,
        string? PrincipalId,
        string? RoleDefinitionId,
        string? Justification,
        DateTimeOffset? CreatedDateTime,
        ScheduleInfo? ScheduleInfo,
        GraphApprovals.Named? RoleDefinition,
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
            targetName: r.RoleDefinition?.DisplayName ?? r.RoleDefinitionId ?? r.Id,
            requesterName: GraphApprovals.Requester(r.Principal, r.PrincipalId),
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
