using Elevate.Core.Models;

namespace Elevate.Core.Providers;

/// <summary>Wire shape for a Graph/ARM schedule's expiration, shared by the providers that read one.</summary>
internal sealed record ScheduleExpiration(string? Type, string? Duration, DateTimeOffset? EndDateTime);

/// <summary>Wire shape for a Graph/ARM schedule.</summary>
internal sealed record ScheduleInfo(DateTimeOffset? StartDateTime, ScheduleExpiration? Expiration);

/// <summary>How a schedule request's status maps to an assignment status, shared by the three providers.</summary>
internal static class GraphSchedule
{
    /// <summary>
    /// The status a freshly created request reports. Graph and ARM share the common names; the extra
    /// ARM ones are harmless for Graph, which never sends them.
    /// </summary>
    internal static AssignmentStatus RequestStatus(string raw) => raw switch
    {
        "PendingApproval" or "PendingAdminDecision" or "PendingApprovalProvisioning" => AssignmentStatus.PendingApproval,
        "PendingProvisioning" or "PendingScheduleCreation" or "ScheduleCreated" or "Accepted"
            or "PendingEvaluation" or "ProvisioningStarted" or "PendingExternalProvisioning"
            => AssignmentStatus.PendingProvisioning,
        "Denied" or "Failed" or "Canceled" or "Revoked" or "TimedOut" or "Invalid" or "AdminDenied"
            or "FailedAsResourceIsLocked" => AssignmentStatus.Failed(raw),
        _ => AssignmentStatus.Active,
    };

    /// <summary>
    /// The status and end to report for a created request. A future start only masks an outcome that
    /// would otherwise read as active; pending and failed still win, and only a live or booked
    /// assignment carries an end.
    /// </summary>
    internal static (AssignmentStatus Status, DateTimeOffset? End) Settle(string rawStatus, DateTimeOffset start, DateTimeOffset? end)
    {
        var reported = RequestStatus(rawStatus);
        var status = reported == AssignmentStatus.Active && ScheduleRules.IsFuture(start)
            ? AssignmentStatus.Scheduled
            : reported;
        return (status, status == AssignmentStatus.Active || status == AssignmentStatus.Scheduled ? end : null);
    }
}
