using System.Text.Json;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Support;

namespace Elevate.Core.Providers;

/// <summary>PIM for app roles (preview) activation requests awaiting the signed-in user's approval.</summary>
public sealed class AppRoleApprovalProvider : IApprovalProvider
{
    internal const string ApprovalsPath = AppRoleProvider.Base + "/assignmentApprovals";

    private const string PendingPath = AppRoleProvider.Base
        + "/assignmentScheduleRequests/filterByCurrentUser(on='approver')"
        + "?$filter=status eq 'PendingApproval'&$expand=servicePrincipal($select=id,displayName),principal";

    private readonly GraphTransport _transport;

    public AppRoleApprovalProvider(IHttpClient http, ITokenProvider tokens)
        => _transport = new GraphTransport(http, tokens);

    public RoleScopeKind Kind => RoleScopeKind.AppRole;

    public IReadOnlyList<string> Scopes => Auth.Scopes.AppRoleAll;

    private sealed record AppRoleRef(string? DisplayName, string? Value);

    private sealed record Request(
        string Id,
        string? ApprovalId,
        string? Action,
        string? PrincipalId,
        string? ServicePrincipalId,
        string? AppRoleId,
        string? Justification,
        DateTimeOffset? CreatedDateTime,
        ScheduleInfo? ScheduleInfo,
        AppRoleRef? AppRole,
        GraphApprovals.Named? ServicePrincipal,
        GraphApprovals.Principal? Principal);

    private sealed record Approval(IReadOnlyList<GraphApprovals.Step>? Stages);

    public async Task<IReadOnlyList<ApprovalRequest>> PendingApprovalsAsync(
        Identity identity, TenantContext tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        var items = await _transport.ListAllAsync<Request>(
            identity, tenant.TenantId, _transport.GraphBetaUrl(PendingPath), Scopes, ct).ConfigureAwait(false);

        return [.. items.Select(r => new ApprovalRequest(
            r.Id, tenant.Key, Kind, GraphApprovals.Action(r.Action),
            targetName: r.AppRole?.DisplayName ?? r.AppRole?.Value ?? r.AppRoleId ?? r.Id,
            requesterName: GraphApprovals.Requester(r.Principal, r.PrincipalId),
            scopeCaption: r.ServicePrincipal?.DisplayName ?? r.ServicePrincipalId,
            justification: r.Justification,
            requestedDuration: GraphApprovals.Duration(r.ScheduleInfo),
            createdAt: r.CreatedDateTime,
            decisionRef: r.ApprovalId ?? r.Id))];
    }

    /// <summary>
    /// The app role approval names its steps <c>stages</c> and serves them only as an expansion of
    /// the approval, unlike Groups and Entra (<c>/steps</c>).
    /// </summary>
    // shortcut: the PATCH is modelled on the Groups one and has not been run against a real pending
    // request (that needs a second account as approver). Verify it when one is available.
    public async Task DecideAsync(ApprovalRequest request, bool approve, string justification, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var approvalId = request.DecisionRef ?? request.Id;
        var tenantId = request.TenantKey.TenantId;
        var response = await _transport.GetAsync(
            identity, tenantId, _transport.GraphBetaUrl($"{ApprovalsPath}/{approvalId}?$expand=stages"), Scopes, ct).ConfigureAwait(false);
        var stage = GraphApprovals.PickStep(
            JsonSerializer.Deserialize<Approval>(response.Body, GraphJson.Options)?.Stages ?? []);
        await _transport.PatchAsync(
            identity, tenantId, _transport.GraphBetaUrl($"{ApprovalsPath}/{approvalId}/stages/{stage.Id}"),
            Scopes, GraphApprovals.DecisionBody(approve, justification), ct).ConfigureAwait(false);
    }
}
