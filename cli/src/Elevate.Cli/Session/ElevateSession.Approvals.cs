using Elevate.Core.Auth;
using Elevate.Core.Models;

namespace Elevate.Cli.Session;

/// <summary>Pending approvals and decisions. Port of <c>AppModel.Approvals</c>.</summary>
public sealed partial class ElevateSession
{
    /// <summary>Every pending request, oldest first, tenant and id as tiebreaks.</summary>
    public IReadOnlyList<ApprovalRequest> ApprovalsOrdered =>
        [.. AllApprovals
            .OrderBy(r => r.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(r => TenantName(r.TenantKey), StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal)];

    /// <summary>Sends one Approve or Deny; throws with the service's reason when refused.</summary>
    public async Task DecideAsync(ApprovalRequest request, bool approve, string justification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = Identity(request.TenantKey.IdentityId)
            ?? throw new PimException(PimErrorKind.Unexpected, "That account is no longer signed in");
        if (!ApprovalProviders.TryGetValue(request.Kind, out var provider))
        {
            throw new PimException(PimErrorKind.Unexpected, "This request cannot be decided from Elevate");
        }

        await InteractionRetry.RunAsync(Tokens, identity, request.TenantKey.TenantId, provider.Scopes, async () =>
        {
            await provider.DecideAsync(request, approve, justification, identity, ct).ConfigureAwait(false);
            return true;
        }, ct: ct).ConfigureAwait(false);

        lock (_sync)
        {
            if (Approvals.TryGetValue(request.TenantKey, out var byKind) && byKind.TryGetValue(request.Kind, out var list))
            {
                list.RemoveAll(r => r.Id == request.Id);
            }
        }

        Settings.LastApprovalJustification = justification;
    }
}
