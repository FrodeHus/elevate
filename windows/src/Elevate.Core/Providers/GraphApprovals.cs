using System.Text.Json;
using Elevate.Core.Models;
using Elevate.Core.Support;

namespace Elevate.Core.Providers;

/// <summary>Wire shapes and rules the two Graph approval providers share.</summary>
internal static class GraphApprovals
{
    /// <summary>The expanded <c>principal</c> of a request. <c>@odata.type</c> is ignored.</summary>
    internal sealed record Principal(string? DisplayName, string? UserPrincipalName);

    internal sealed record Named(string? Id, string? DisplayName);

    internal sealed record Step(string Id, string? Status, bool? AssignedToMe);

    /// <summary><c>selfActivate</c> → activate, <c>selfExtend</c> → extend, <c>selfRenew</c> → renew, anything else → other.</summary>
    internal static ApprovalAction Action(string? raw) => raw?.ToLowerInvariant() switch
    {
        "selfactivate" => ApprovalAction.Activate,
        "selfextend" => ApprovalAction.Extend,
        "selfrenew" => ApprovalAction.Renew,
        _ => ApprovalAction.Other,
    };

    /// <summary>Display name, then UPN, then the principal id the request carries.</summary>
    internal static string Requester(Principal? principal, string? principalId) =>
        principal?.DisplayName ?? principal?.UserPrincipalName ?? principalId ?? "Unknown";

    /// <summary>The requested duration a schedule carries, when it parses.</summary>
    internal static TimeSpan? Duration(ScheduleInfo? schedule) =>
        schedule?.Expiration?.Duration is { } d ? Iso8601Duration.Parse(d) : null;

    /// <summary>The step to decide: the one in progress, else the one assigned to us, else the first.</summary>
    internal static Step PickStep(IReadOnlyList<Step> steps)
    {
        if (steps.FirstOrDefault(s => string.Equals(s.Status, "InProgress", StringComparison.OrdinalIgnoreCase)) is { } inProgress)
        {
            return inProgress;
        }

        if (steps.FirstOrDefault(s => s.AssignedToMe == true) is { } mine)
        {
            return mine;
        }

        return steps.Count > 0 ? steps[0] : throw NoStep();
    }

    internal static PimException NoStep() => new(PimErrorKind.Unexpected, "No approval step to decide", 0);

    internal static byte[] DecisionBody(bool approve, string justification) =>
        JsonSerializer.SerializeToUtf8Bytes(Decision(approve, justification));

    internal static Dictionary<string, string> Decision(bool approve, string justification) => new()
    {
        ["reviewResult"] = approve ? "Approve" : "Deny",
        ["justification"] = justification,
    };

    /// <summary>GETs the approval's steps on the beta base, then PATCHes the one to decide.</summary>
    internal static async Task DecideAsync(
        ApprovalRequest request, bool approve, string justification, Identity identity,
        GraphTransport transport, IReadOnlyList<string> scopes, string approvalsPath, CancellationToken ct)
    {
        var approvalId = request.DecisionRef ?? request.Id;
        var stepsUrl = transport.GraphBetaUrl($"{approvalsPath}/{approvalId}/steps");
        var response = await transport.GetAsync(identity, request.TenantKey.TenantId, stepsUrl, scopes, ct).ConfigureAwait(false);
        var steps = JsonSerializer.Deserialize<GraphTransport.Page<Step>>(response.Body, GraphJson.Options)?.Value ?? [];
        var step = PickStep(steps);
        await transport.PatchAsync(
            identity, request.TenantKey.TenantId,
            transport.GraphBetaUrl($"{approvalsPath}/{approvalId}/steps/{step.Id}"),
            scopes, DecisionBody(approve, justification), ct).ConfigureAwait(false);
    }
}
