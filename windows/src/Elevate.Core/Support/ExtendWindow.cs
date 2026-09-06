using Elevate.Core.Models;

namespace Elevate.Core.Support;

/// <summary>Extend is offered only near the end of an activation; earlier it would just shorten the remaining time.</summary>
public static class ExtendWindow
{
    public static readonly TimeSpan DefaultWithin = TimeSpan.FromMinutes(15);

    public static bool CanExtend(ActiveAssignment assignment, RolePolicy policy, DateTimeOffset now, TimeSpan? within = null)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(policy);

        // An Extend deactivates first; with approval required the re-activation would only be pending,
        // leaving the user without access in the meantime.
        if (policy.RequiresApproval)
        {
            return false;
        }

        if (assignment.Status.Kind != AssignmentStatusKind.Active || assignment.EndDateTime is not { } end)
        {
            return false;
        }

        var left = end - now;
        return left > TimeSpan.Zero && left <= (within ?? DefaultWithin);
    }
}
