using System.Text.Json;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Support;

namespace Elevate.Core.Providers;

/// <summary>Azure resource role activation requests awaiting the signed-in user's approval, through ARM.</summary>
public sealed class AzureApprovalProvider : IApprovalProvider
{
    internal const string ListApiVersion = "2020-10-01";
    internal const string ApprovalApiVersion = "2021-01-01-preview";

    private readonly GraphTransport _transport;

    public AzureApprovalProvider(IHttpClient http, ITokenProvider tokens)
        => _transport = new GraphTransport(http, tokens, GraphTransport.MapArmError);

    public RoleScopeKind Kind => RoleScopeKind.AzureResource;

    public IReadOnlyList<string> Scopes => Auth.Scopes.ArmAll;

    // MARK: Wire models

    private sealed record RequestProperties(
        string? RoleDefinitionId,
        string? PrincipalId,
        string? RequestType,
        string? Status,
        string? ApprovalId,
        string? Justification,
        DateTimeOffset? CreatedOn,
        ScheduleInfo? ScheduleInfo,
        AzureResourceProvider.Expanded? ExpandedProperties);

    private sealed record Request(string Name, RequestProperties Properties);

    private sealed record StageProperties(string? ReviewResult, string? Status);

    private sealed record Stage(string? Name, string? Id, StageProperties? Properties);

    private sealed record ApprovalProperties(IReadOnlyList<Stage>? Stages);

    private sealed record Approval(ApprovalProperties? Properties);

    // MARK: Reads

    public async Task<IReadOnlyList<ApprovalRequest>> PendingApprovalsAsync(
        Identity identity, TenantContext tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        var url = AzureResourceProvider.ArmUrl(
            "providers/Microsoft.Authorization/roleAssignmentScheduleRequests",
            ListApiVersion, new Dictionary<string, string> { ["$filter"] = "asApprover()" });
        var items = await AzureResourceProvider.ListAllAsync<Request>(_transport, identity, tenant.TenantId, url, Scopes, ct)
            .ConfigureAwait(false);

        return [.. items
            .Where(r => r.Properties.Status == "PendingApproval")
            .Select(r =>
            {
                var expanded = r.Properties.ExpandedProperties;
                return new ApprovalRequest(
                    r.Name, tenant.Key, Kind, GraphApprovals.Action(r.Properties.RequestType),
                    targetName: expanded?.RoleDefinition?.DisplayName ?? r.Properties.RoleDefinitionId ?? r.Name,
                    scopeCaption: AzureResourceProvider.Caption(expanded),
                    requesterName: expanded?.Principal?.DisplayName ?? r.Properties.PrincipalId ?? "Unknown",
                    justification: r.Properties.Justification,
                    requestedDuration: GraphApprovals.Duration(r.Properties.ScheduleInfo),
                    createdAt: r.Properties.CreatedOn,
                    decisionRef: r.Properties.ApprovalId);
            })];
    }

    // MARK: Decision

    /// <summary>The stage to decide: the one still awaiting review, else the first.</summary>
    private static string PickStage(IReadOnlyList<Stage> stages)
    {
        var pick = stages.FirstOrDefault(s => string.Equals(s.Properties?.ReviewResult, "NotReviewed", StringComparison.OrdinalIgnoreCase))
            ?? stages.FirstOrDefault();
        return pick?.Name ?? pick?.Id?.Split('/').LastOrDefault() ?? throw GraphApprovals.NoStep();
    }

    public async Task DecideAsync(ApprovalRequest request, bool approve, string justification, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var approvalId = request.DecisionRef ?? throw GraphApprovals.NoStep();
        var tenantId = request.TenantKey.TenantId;
        var basePath = $"providers/Microsoft.Authorization/roleAssignmentApprovals/{approvalId}";
        var response = await _transport.GetAsync(
            identity, tenantId, AzureResourceProvider.ArmUrl(basePath, ApprovalApiVersion), Scopes, ct).ConfigureAwait(false);
        var approval = JsonSerializer.Deserialize<Approval>(response.Body, GraphJson.Options);
        var stage = PickStage(approval?.Properties?.Stages ?? []);
        var url = AzureResourceProvider.ArmUrl($"{basePath}/stages/{stage}", ApprovalApiVersion);
        var decision = GraphApprovals.Decision(approve, justification);
        try
        {
            await _transport.PatchAsync(identity, tenantId, url, Scopes, JsonSerializer.SerializeToUtf8Bytes(decision), ct)
                .ConfigureAwait(false);
        }
        catch (PimException e) when (e.Kind == PimErrorKind.Unexpected && e.Status == 400)
        {
            // Some ARM versions want the decision wrapped in `properties`; the flat form the docs show comes first.
            var wrapped = new Dictionary<string, Dictionary<string, string>> { ["properties"] = decision };
            await _transport.PatchAsync(identity, tenantId, url, Scopes, JsonSerializer.SerializeToUtf8Bytes(wrapped), ct)
                .ConfigureAwait(false);
        }
    }
}
