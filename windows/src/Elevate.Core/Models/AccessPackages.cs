using System.Text.Json.Serialization;

namespace Elevate.Core.Models;

/// <summary>An entitlement management access package the signed-in user may request.</summary>
public sealed record AccessPackage(string Id, string DisplayName, string? Description = null, bool IsHidden = false);

/// <summary>Graph's <c>accessPackageAssignmentRequestState</c>, plus <see cref="Unknown"/> for values this build has not seen.</summary>
public enum AccessPackageRequestState
{
    Submitted,
    PendingApproval,
    Delivering,
    Delivered,
    DeliveryFailed,
    Denied,
    Scheduled,
    Canceled,
    PartiallyDelivered,
    Unknown,
}

public static class AccessPackageRequestStates
{
    /// <summary>Case-insensitive; anything unrecognised is <see cref="AccessPackageRequestState.Unknown"/> so one new value never fails a page.</summary>
    public static AccessPackageRequestState Parse(string? raw) =>
        ParseName<AccessPackageRequestState>(raw) ?? AccessPackageRequestState.Unknown;

    /// <summary>Requested tab: the request has not reached a final state.</summary>
    public static bool IsOpen(this AccessPackageRequestState state) => state is
        AccessPackageRequestState.Submitted or AccessPackageRequestState.PendingApproval or AccessPackageRequestState.Delivering
        or AccessPackageRequestState.Scheduled or AccessPackageRequestState.PartiallyDelivered;

    /// <summary>Declined tab: the request ended without access.</summary>
    public static bool IsDeclined(this AccessPackageRequestState state) => state is
        AccessPackageRequestState.Denied or AccessPackageRequestState.DeliveryFailed or AccessPackageRequestState.Canceled;

    /// <summary>Graph accepts a cancel only before delivery starts.</summary>
    public static bool IsCancellable(this AccessPackageRequestState state) => state is
        AccessPackageRequestState.Submitted or AccessPackageRequestState.PendingApproval;

    /// <summary>Matches an enum member by name, ignoring case; never by numeric value.</summary>
    internal static T? ParseName<T>(string? raw)
        where T : struct, Enum
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        foreach (var value in Enum.GetValues<T>())
        {
            if (string.Equals(value.ToString(), raw, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>One of the signed-in user's own access package requests.</summary>
public sealed record AccessPackageRequest(
    string Id,
    string PackageId,
    string PackageName,
    string RequestType,
    AccessPackageRequestState State,
    // Graph's free-text status, shown for failed and canceled requests.
    string? Status = null,
    string? Justification = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? PolicyId = null);

public enum AccessPackageAssignmentState
{
    Delivering,
    Delivered,
    Expired,
    Unknown,
}

public static class AccessPackageAssignmentStates
{
    public static AccessPackageAssignmentState Parse(string? raw) =>
        AccessPackageRequestStates.ParseName<AccessPackageAssignmentState>(raw) ?? AccessPackageAssignmentState.Unknown;
}

/// <summary>An access package currently (or formerly) assigned to the signed-in user.</summary>
public sealed record AccessPackageAssignment(
    string Id,
    string PackageId,
    string PackageName,
    AccessPackageAssignmentState State,
    string? PolicyName = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>One policy the signed-in user may request a package under, from <c>getApplicablePolicyRequirements</c>.</summary>
public sealed record PolicyRequirement(
    // The policy id.
    string Id,
    string DisplayName,
    string? Description = null,
    bool IsApprovalRequired = false,
    // True when the policy asks questions; Elevate hands such requests to the My Access portal.
    bool RequiresAnswers = false);

/// <summary>What one poll of a tenant returned. Persisted so the next poll can diff against it.</summary>
public sealed record AccessPackageSnapshot
{
    [JsonConstructor]
    public AccessPackageSnapshot(IReadOnlyList<AccessPackageRequest>? requests = null, IReadOnlyList<AccessPackageAssignment>? assignments = null)
    {
        Requests = requests ?? [];
        Assignments = assignments ?? [];
    }

    public IReadOnlyList<AccessPackageRequest> Requests { get; init; }

    public IReadOnlyList<AccessPackageAssignment> Assignments { get; init; }

    public bool Equals(AccessPackageSnapshot? other) =>
        other is not null && Requests.SequenceEqual(other.Requests) && Assignments.SequenceEqual(other.Assignments);

    public override int GetHashCode() => HashCode.Combine(Requests.Count, Assignments.Count);
}
