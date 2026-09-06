using Elevate.Core.Models;
using Elevate.Core.Storage;

namespace Elevate.Core.Coordination;

/// <summary>Port of Swift's <c>QuickActivate.Decision</c>: either the requests to send, or why the dialog is needed.</summary>
public abstract record QuickActivateDecision
{
    private QuickActivateDecision()
    {
    }

    public sealed record Ready(IReadOnlyList<ActivationRequest> Requests) : QuickActivateDecision
    {
        public bool Equals(Ready? other) => other is not null && Requests.SequenceEqual(other.Requests);

        public override int GetHashCode() => Requests.Count;
    }

    public sealed record NeedsDialog(string Reason) : QuickActivateDecision;
}

/// <summary>Decides whether an activation can go ahead without the dialog, and builds the requests when it can.</summary>
public static class QuickActivate
{
    public static QuickActivateDecision Decide(EligibleRole role, RoleMemory? memory)
    {
        ArgumentNullException.ThrowIfNull(role);
        var p = role.Policy;
        if (p.RequiresTicket)
        {
            return new QuickActivateDecision.NeedsDialog("ticket required");
        }

        if (p.RequiresApproval)
        {
            return new QuickActivateDecision.NeedsDialog("approval required");
        }

        var reason = memory?.Justification.Trim() ?? "";
        if (p.RequiresJustification && reason.Length == 0)
        {
            return new QuickActivateDecision.NeedsDialog("no remembered reason");
        }

        var wanted = memory?.LastDuration ?? p.DefaultDuration;
        var duration = wanted < p.MaximumDuration ? wanted : p.MaximumDuration;
        return new QuickActivateDecision.Ready(
            [new ActivationRequest(role.Key, duration, reason, null, p.AuthenticationContext)]);
    }

    public static QuickActivateDecision Decide(IReadOnlyList<ProfilePlanItem> items, string? justification)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Any(i => i.Disposition == ProfilePlanDisposition.NotLoaded))
        {
            return new QuickActivateDecision.NeedsDialog("roles still loading");
        }

        var toRun = items.Where(i => i.Disposition == ProfilePlanDisposition.Activate).ToList();
        if (toRun.Count == 0)
        {
            return new QuickActivateDecision.NeedsDialog("nothing to activate");
        }

        if (toRun.Any(i => i.Role?.Policy.RequiresTicket == true))
        {
            return new QuickActivateDecision.NeedsDialog("ticket required");
        }

        var reason = justification?.Trim() ?? "";
        if (reason.Length == 0 && toRun.Any(i => i.Role?.Policy.RequiresJustification ?? true))
        {
            return new QuickActivateDecision.NeedsDialog("no remembered reason");
        }

        return new QuickActivateDecision.Ready(
            [.. toRun.Select(i => new ActivationRequest(i.RoleKey, i.Duration, reason, null, i.Role?.Policy.AuthenticationContext))]);
    }
}
