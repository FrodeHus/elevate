using Elevate.Core.Models;
using Elevate.Core.Support;

namespace Elevate.Core.Providers;

/// <summary>Port of the Swift <c>PIMProvider</c> protocol.</summary>
public interface IPimProvider
{
    RoleScopeKind Kind { get; }

    /// <summary>Token scopes this provider needs; all against one resource.</summary>
    IReadOnlyList<string> Scopes { get; }

    Task<IReadOnlyList<EligibleRole>> EligibleRolesAsync(Identity identity, TenantContext tenant, CancellationToken ct = default);

    Task<IReadOnlyList<ActiveAssignment>> ActiveAssignmentsAsync(Identity identity, TenantContext tenant, CancellationToken ct = default);

    Task<RolePolicy> PolicyAsync(EligibleRole role, Identity identity, CancellationToken ct = default);

    Task<ActiveAssignment> ActivateAsync(ActivationRequest request, Identity identity, CancellationToken ct = default);

    Task DeactivateAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default);

    /// <summary>Withdraws a request that is still waiting for an approver.</summary>
    Task CancelPendingRequestAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default);
}

/// <summary>
/// Start times for activations booked ahead of time. Port of the Swift <c>ScheduledStart</c>.
/// <para>
/// A start counts as scheduled only when it is more than a minute out: PIM echoes back a start of
/// "now" that is a few seconds off our clock, and that must still read as an immediate activation.
/// </para>
/// </summary>
public static class ScheduleRules
{
    /// <summary>How far ahead a start must be before it counts as scheduled rather than immediate.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Request statuses that must never read as a booked-ahead activation: one still waiting on an
    /// approver is pending approval, and a refused or withdrawn one is nothing at all.
    /// </summary>
    public static IReadOnlySet<string> NotScheduled { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PendingApproval", "PendingAdminDecision", "PendingApprovalProvisioning",
        "Denied", "AdminDenied", "Failed", "FailedAsResourceIsLocked", "Canceled", "Cancelled", "Revoked",
        "TimedOut", "Invalid",
    };

    /// <summary>True when <paramref name="status"/> is one of the above, compared case-insensitively.</summary>
    public static bool IsSettledOrPending(string? status) => status is not null && NotScheduled.Contains(status);

    public static bool IsFuture(DateTimeOffset? date, DateTimeOffset? now = null) =>
        date is { } value && value - (now ?? DateTimeOffset.UtcNow) > Horizon;

    /// <summary>
    /// The start to report. A future start the service echoed wins; failing that the future start we
    /// asked for, because the service often answers with the moment it received the request.
    /// </summary>
    public static DateTimeOffset Effective(DateTimeOffset? response, DateTimeOffset? requested, DateTimeOffset? now = null)
    {
        if (response is { } echoed && IsFuture(echoed, now))
        {
            return echoed;
        }

        if (requested is { } asked && IsFuture(asked, now))
        {
            return asked;
        }

        return response ?? requested ?? now ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// The end time to report: an explicit end the service gave wins; failing that, the parsed ISO 8601
    /// duration added to <paramref name="start"/>; failing that, the caller-supplied fallback duration.
    /// </summary>
    public static DateTimeOffset? End(DateTimeOffset? explicitEnd, string? duration, DateTimeOffset start, TimeSpan? fallback = null)
    {
        if (explicitEnd is { } end)
        {
            return end;
        }

        if (duration is not null && Iso8601Duration.Parse(duration) is { } parsed)
        {
            return start + parsed;
        }

        return fallback is { } span ? start + span : null;
    }
}
