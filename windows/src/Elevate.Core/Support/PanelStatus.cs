using Elevate.Core.Models;

namespace Elevate.Core.Support;

/// <summary>What the tray icon needs to know, derived from the assignments alone.</summary>
public readonly record struct PanelStatus(int ActiveCount, bool ExpiringSoon, bool PendingApproval)
{
    public static readonly TimeSpan DefaultSoonWithin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// <see cref="ExpiringSoon"/> is true when an active assignment ends within <paramref name="soonWithin"/>
    /// of <paramref name="now"/> (and has not ended yet).
    /// </summary>
    public static PanelStatus Compute(IEnumerable<ActiveAssignment> assignments, DateTimeOffset now, TimeSpan? soonWithin = null)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        var within = soonWithin ?? DefaultSoonWithin;
        var active = 0;
        var soon = false;
        var pending = false;
        foreach (var a in assignments)
        {
            switch (a.Status.Kind)
            {
                case AssignmentStatusKind.Active:
                    active += 1;
                    if (a.EndDateTime is { } end)
                    {
                        var left = end - now;
                        if (left > TimeSpan.Zero && left <= within)
                        {
                            soon = true;
                        }
                    }

                    break;
                case AssignmentStatusKind.PendingApproval:
                case AssignmentStatusKind.Scheduled:
                    pending = true;
                    break;
                default:
                    break;
            }
        }

        return new PanelStatus(active, soon, pending);
    }
}
