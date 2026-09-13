namespace Elevate.Core.Models;

/// <summary>The exact assignments successfully created by one execution, independent of later profile edits.</summary>
public sealed record ProfileRun(Guid Id, Guid ProfileId, string ProfileName, DateTimeOffset StartedAt, List<ProfileRun.Entry> Entries)
{
    public sealed record Entry(ActiveAssignment Assignment, string DisplayName, bool Completed = false)
    {
        /// <summary>
        /// Pre-flight verdict for one entry against the assignment currently visible for its role.
        /// Mirrors the macOS <c>Entry.disposition(current:now:)</c>, including its reason strings:
        /// a blocked entry cannot be acted on until state changes, a skipped one is terminal.
        /// </summary>
        public ProfileRun.Disposition Disposition(ActiveAssignment? current, DateTimeOffset now)
        {
            if (Completed) return ProfileRun.Disposition.Completed;
            if (current is null && Assignment.Status.Kind != AssignmentStatusKind.Active && Live(Assignment.EndDateTime, now))
                return ProfileRun.Disposition.Blocked("Awaiting active assignment confirmation");
            if (current is null || !Live(current.EndDateTime, now))
                return ProfileRun.Disposition.Skipped("Already inactive or expired");
            if (string.IsNullOrEmpty(Assignment.AssignmentId) && string.IsNullOrEmpty(Assignment.ScheduleId))
                return ProfileRun.Disposition.Blocked("Assignment identity could not be verified");
            if (Assignment.EndDateTime is null && Assignment.Status.Kind != AssignmentStatusKind.Active
                && current.Status.Kind == AssignmentStatusKind.Active)
                return ProfileRun.Disposition.Blocked("Original activation interval could not be verified; deactivate this role individually");
            if (!Matches(Assignment, current)) return ProfileRun.Disposition.Skipped("Assignment replaced");
            if (current.Status.Kind != AssignmentStatusKind.Active) return ProfileRun.Disposition.Blocked("Awaiting active assignment confirmation");
            var remaining = 300 - (now - current.StartDateTime).TotalSeconds;
            if (remaining > 0)
                return ProfileRun.Disposition.Blocked($"Can be deactivated in {(int)Math.Ceiling(remaining)} s (minimum activation period)");
            return ProfileRun.Disposition.Ready;
        }

        private static bool Live(DateTimeOffset? end, DateTimeOffset now) => end is null || end > now;
    }

    public enum DispositionKind { Ready, Completed, Blocked, Skipped }

    /// <summary>What a deactivation pass would do with an entry; <see cref="Reason"/> is set for blocked and skipped.</summary>
    public sealed record Disposition(DispositionKind Kind, string? Reason = null)
    {
        public static readonly Disposition Ready = new(DispositionKind.Ready);
        public static readonly Disposition Completed = new(DispositionKind.Completed);
        public static Disposition Blocked(string reason) => new(DispositionKind.Blocked, reason);
        public static Disposition Skipped(string reason) => new(DispositionKind.Skipped, reason);
    }

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
