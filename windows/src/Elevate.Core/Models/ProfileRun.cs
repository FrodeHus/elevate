namespace Elevate.Core.Models;

/// <summary>The exact assignments successfully created by one execution, independent of later profile edits.</summary>
public sealed record ProfileRun(Guid Id, Guid ProfileId, string ProfileName, DateTimeOffset StartedAt, List<ProfileRun.Entry> Entries)
{
    public sealed record Entry(ActiveAssignment Assignment, string DisplayName, bool Completed = false);

    public ProfileRun DeepCopy() => this with { Entries = [.. Entries] };

    public bool Equals(ProfileRun? other) => other is not null && Id == other.Id && ProfileId == other.ProfileId
        && ProfileName == other.ProfileName && StartedAt == other.StartedAt && Entries.SequenceEqual(other.Entries);
    public override int GetHashCode() => HashCode.Combine(Id, ProfileId, ProfileName, StartedAt, Entries.Count);

    /// <summary>A role key alone cannot distinguish an extension or a replacement activation.</summary>
    public static bool Matches(ActiveAssignment expected, ActiveAssignment current)
    {
        if (expected.RoleKey != current.RoleKey) return false;
        if (!string.IsNullOrEmpty(expected.ScheduleId) && !string.IsNullOrEmpty(current.ScheduleId)
            && expected.ScheduleId != current.ScheduleId) return false;
        var sameSchedule = !string.IsNullOrEmpty(expected.ScheduleId) && !string.IsNullOrEmpty(current.ScheduleId)
            && expected.ScheduleId == current.ScheduleId;
        var sameRequest = !string.IsNullOrEmpty(current.ActivationRequestId)
            && (expected.ActivationRequestId ?? expected.AssignmentId) == current.ActivationRequestId;
        if (!sameSchedule && !sameRequest && (string.IsNullOrEmpty(expected.AssignmentId) || expected.AssignmentId != current.AssignmentId)) return false;
        if (expected.Status.Kind is AssignmentStatusKind.PendingApproval or AssignmentStatusKind.PendingProvisioning
            && expected.EndDateTime is null) return false;
        return expected.StartDateTime.ToUnixTimeSeconds() == current.StartDateTime.ToUnixTimeSeconds()
            && expected.EndDateTime?.ToUnixTimeSeconds() == current.EndDateTime?.ToUnixTimeSeconds();
    }
}
