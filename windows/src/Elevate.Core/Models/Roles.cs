using System.Text.Json.Serialization;

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

public enum AssignmentStatus { Active, PendingApproval, PendingProvisioning, Scheduled, Failed }

/// <summary>A role assignment that currently exists (active, pending, scheduled, or failed).</summary>
public sealed record ActiveAssignment(
    RoleKey RoleKey,
    string? AssignmentId,
    DateTimeOffset StartDateTime,
    DateTimeOffset? EndDateTime,
    AssignmentStatus Status,
    // Server message for AssignmentStatus.Failed; null otherwise.
    string? FailureReason = null);

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
