using Elevate.Core.Models;

namespace Elevate.Core.Providers;

/// <summary>Reads the requests awaiting the signed-in user's approval, and sends the decision. Port of the Swift <c>ApprovalProvider</c>.</summary>
public interface IApprovalProvider
{
    RoleScopeKind Kind { get; }

    /// <summary>Token scopes this provider needs; all against one resource.</summary>
    IReadOnlyList<string> Scopes { get; }

    Task<IReadOnlyList<ApprovalRequest>> PendingApprovalsAsync(Identity identity, TenantContext tenant, CancellationToken ct = default);

    Task DecideAsync(ApprovalRequest request, bool approve, string justification, Identity identity, CancellationToken ct = default);
}
