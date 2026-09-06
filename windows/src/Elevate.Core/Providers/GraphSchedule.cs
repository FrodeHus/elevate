namespace Elevate.Core.Providers;

/// <summary>Wire shape for a Graph/ARM schedule's expiration, shared by the providers that read one.</summary>
internal sealed record ScheduleExpiration(string? Type, string? Duration, DateTimeOffset? EndDateTime);

/// <summary>Wire shape for a Graph/ARM schedule.</summary>
internal sealed record ScheduleInfo(DateTimeOffset? StartDateTime, ScheduleExpiration? Expiration);
