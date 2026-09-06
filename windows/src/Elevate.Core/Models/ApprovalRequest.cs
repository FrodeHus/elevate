using System.Text.Json.Serialization;

namespace Elevate.Core.Models;

/// <summary>
/// What the requester asked for. Only <see cref="Activate"/> can be decided through the APIs;
/// extend and renew requests are listed with a pointer to the portal.
/// </summary>
public enum ApprovalAction { Activate, Extend, Renew, Other }

/// <summary>One activation request waiting for the signed-in user's decision as an approver.</summary>
public sealed record ApprovalRequest
{
    [JsonConstructor]
    public ApprovalRequest(
        string id,
        TenantKey tenantKey,
        RoleScopeKind kind,
        ApprovalAction action,
        string targetName,
        string requesterName,
        string? scopeCaption = null,
        string? justification = null,
        TimeSpan? requestedDuration = null,
        DateTimeOffset? createdAt = null,
        string? decisionRef = null)
    {
        Id = id;
        TenantKey = tenantKey;
        Kind = kind;
        Action = action;
        TargetName = targetName;
        ScopeCaption = scopeCaption;
        RequesterName = requesterName;
        Justification = justification;
        RequestedDuration = requestedDuration;
        CreatedAt = createdAt;
        DecisionRef = decisionRef;
    }

    /// <summary>Request id (Graph) or request name (ARM).</summary>
    public string Id { get; init; }

    public TenantKey TenantKey { get; init; }

    public RoleScopeKind Kind { get; init; }

    public ApprovalAction Action { get; init; }

    /// <summary>Role or group display name; falls back to the id when the service did not expand it.</summary>
    public string TargetName { get; init; }

    /// <summary>Azure scope display, or "member"/"owner" for a group request.</summary>
    public string? ScopeCaption { get; init; }

    /// <summary>Display name or UPN of the requester; falls back to their principal id.</summary>
    public string RequesterName { get; init; }

    public string? Justification { get; init; }

    public TimeSpan? RequestedDuration { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Graph: the approval id (equal to the request id). ARM: <c>properties.approvalId</c>.</summary>
    public string? DecisionRef { get; init; }
}

/// <summary>Which pending requests have not been notified about yet.</summary>
public static class ApprovalDiff
{
    /// <summary>Requests in <paramref name="current"/> whose id is not in <paramref name="previousIds"/>, in input order.</summary>
    public static IReadOnlyList<ApprovalRequest> NewRequests(IReadOnlySet<string> previousIds, IEnumerable<ApprovalRequest> current)
    {
        ArgumentNullException.ThrowIfNull(previousIds);
        ArgumentNullException.ThrowIfNull(current);
        return [.. current.Where(r => !previousIds.Contains(r.Id))];
    }
}
