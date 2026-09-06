using Elevate.Core.Models;

namespace Elevate.Core.Support;

/// <summary>Order for the "Active now" section: what expires first on top, then what is waiting.</summary>
public static class ActiveSummary
{
    public static IReadOnlyList<ActiveAssignment> Order(IEnumerable<ActiveAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        var all = assignments.ToList();

        var active = all.Where(a => a.Status.Kind == AssignmentStatusKind.Active)
            .OrderBy(a => a.EndDateTime ?? DateTimeOffset.MaxValue)
            .ThenBy(a => a.AssignmentId ?? string.Empty, StringComparer.Ordinal);
        var scheduled = ByStart(all, AssignmentStatusKind.Scheduled);
        var pending = ByStart(all, AssignmentStatusKind.PendingApproval);
        var provisioning = ByStart(all, AssignmentStatusKind.PendingProvisioning);

        return [.. active, .. scheduled, .. pending, .. provisioning];
    }

    private static IEnumerable<ActiveAssignment> ByStart(IEnumerable<ActiveAssignment> all, AssignmentStatusKind kind) =>
        all.Where(a => a.Status.Kind == kind)
            .OrderBy(a => a.StartDateTime)
            .ThenBy(a => a.AssignmentId ?? string.Empty, StringComparer.Ordinal);
}
