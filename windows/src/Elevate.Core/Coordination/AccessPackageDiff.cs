using Elevate.Core.Models;

namespace Elevate.Core.Coordination;

/// <summary>Something worth telling the user about between two polls of a tenant's access packages.</summary>
public abstract record AccessPackageEvent
{
    private AccessPackageEvent()
    {
    }

    public abstract string PackageName { get; }

    public sealed record Approved(AccessPackageRequest Request) : AccessPackageEvent
    {
        public override string PackageName => Request.PackageName;
    }

    public sealed record Denied(AccessPackageRequest Request) : AccessPackageEvent
    {
        public override string PackageName => Request.PackageName;
    }

    public sealed record DeliveryFailed(AccessPackageRequest Request) : AccessPackageEvent
    {
        public override string PackageName => Request.PackageName;
    }

    public sealed record Revoked(AccessPackageAssignment Assignment) : AccessPackageEvent
    {
        public override string PackageName => Assignment.PackageName;
    }

    public sealed record Expired(AccessPackageAssignment Assignment) : AccessPackageEvent
    {
        public override string PackageName => Assignment.PackageName;
    }
}

/// <summary>
/// Pure comparison of two snapshots. A null <c>previous</c> is the first sight of a tenant and
/// never notifies: everything present then is baseline, not news. Port of the Swift <c>AccessPackageDiff</c>.
/// </summary>
public static class AccessPackageDiff
{
    public static IReadOnlyList<AccessPackageEvent> Events(AccessPackageSnapshot? previous, AccessPackageSnapshot current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null)
        {
            return [];
        }

        var events = new List<AccessPackageEvent>();

        // Requests: only a request we already knew in an open state can transition into news.
        var previousStates = new Dictionary<string, AccessPackageRequestState>(StringComparer.Ordinal);
        foreach (var r in previous.Requests)
        {
            previousStates.TryAdd(r.Id, r.State);
        }

        foreach (var r in current.Requests)
        {
            if (!previousStates.TryGetValue(r.Id, out var was) || !was.IsOpen() || was == r.State)
            {
                continue;
            }

            switch (r.State)
            {
                case AccessPackageRequestState.Delivered:
                    events.Add(new AccessPackageEvent.Approved(r));
                    break;
                case AccessPackageRequestState.Denied:
                    events.Add(new AccessPackageEvent.Denied(r));
                    break;
                case AccessPackageRequestState.DeliveryFailed:
                    events.Add(new AccessPackageEvent.DeliveryFailed(r));
                    break;
                default:
                    break;
            }
        }

        // Assignments: a delivered one that is gone or expired is either revoked or expired,
        // decided by whether its end date has passed.
        var currentById = new Dictionary<string, AccessPackageAssignment>(StringComparer.Ordinal);
        foreach (var a in current.Assignments)
        {
            currentById.TryAdd(a.Id, a);
        }

        foreach (var a in previous.Assignments.Where(a => a.State == AccessPackageAssignmentState.Delivered))
        {
            if (currentById.TryGetValue(a.Id, out var still) && still.State == AccessPackageAssignmentState.Delivered)
            {
                continue;
            }

            events.Add(a.ExpiresAt is { } end && end <= now
                ? new AccessPackageEvent.Expired(a)
                : new AccessPackageEvent.Revoked(a));
        }

        return events;
    }
}
