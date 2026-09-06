using System.Text.Json.Serialization;
using Elevate.Core;

namespace Elevate.Core.Models;

/// <summary>The activation rules PIM reports for a role.</summary>
public sealed record RolePolicy(
    TimeSpan DefaultDuration,
    TimeSpan MaximumDuration,
    bool RequiresJustification,
    bool RequiresTicket,
    // Key stays "requiresMFA" to match the Swift encoding.
    [property: JsonPropertyName("requiresMFA")] bool RequiresMfa,
    bool RequiresApproval,
    // Conditional Access authentication context id (e.g. "c1") the activation token must carry.
    string? AuthenticationContext = null)
{
    public static readonly RolePolicy ManualDefault = new(
        TimeSpan.FromHours(1), TimeSpan.FromHours(8),
        RequiresJustification: true, RequiresTicket: false, RequiresMfa: false, RequiresApproval: false);
}

public enum RoleSource { Discovered, Manual }

/// <summary>A role the user is eligible to activate.</summary>
public sealed record EligibleRole(
    RoleKey Key,
    string DisplayName,
    RoleSource Source,
    RolePolicy Policy,
    // Secondary caption, e.g. the Azure scope's display name and type. Null for Entra roles.
    string? Detail = null,
    // Name of the group that grants this eligibility; null for a direct eligibility.
    string? ViaGroup = null);

public enum AssignmentStatusKind { Active, PendingApproval, PendingProvisioning, Scheduled, Failed }

/// <summary>
/// Swift's <c>ActiveAssignment.Status</c>: four plain cases plus <c>failed(String)</c>.
/// Encoded as Swift synthesises it — <c>{"active":{}}</c>, <c>{"failed":{"_0":"reason"}}</c>.
/// </summary>
[JsonConverter(typeof(AssignmentStatusJsonConverter))]
public sealed record AssignmentStatus
{
    private AssignmentStatus(AssignmentStatusKind kind, string? failureReason)
    {
        Kind = kind;
        FailureReason = failureReason;
    }

    public AssignmentStatusKind Kind { get; }

    /// <summary>Server message carried by <see cref="AssignmentStatusKind.Failed"/>; null otherwise.</summary>
    public string? FailureReason { get; }

    public static readonly AssignmentStatus Active = new(AssignmentStatusKind.Active, null);
    public static readonly AssignmentStatus PendingApproval = new(AssignmentStatusKind.PendingApproval, null);
    public static readonly AssignmentStatus PendingProvisioning = new(AssignmentStatusKind.PendingProvisioning, null);
    public static readonly AssignmentStatus Scheduled = new(AssignmentStatusKind.Scheduled, null);

    public static AssignmentStatus Failed(string reason) => new(AssignmentStatusKind.Failed, reason);
}

/// <summary>A role assignment that currently exists (active, pending, scheduled, or failed).</summary>
public sealed record ActiveAssignment(
    RoleKey RoleKey,
    string? AssignmentId,
    DateTimeOffset StartDateTime,
    DateTimeOffset? EndDateTime,
    AssignmentStatus Status);

public sealed record TicketInfo(string Number, string System);

/// <summary>An activation the user asked for.</summary>
public sealed record ActivationRequest(
    RoleKey RoleKey,
    TimeSpan Duration,
    string Justification,
    TicketInfo? Ticket = null,
    // Authentication context the role's policy demands; the coordinator asks for a token carrying it.
    string? AuthenticationContext = null,
    // When the activation should begin; null means immediately.
    DateTimeOffset? StartDateTime = null);
