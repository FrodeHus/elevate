using Elevate.Core.Models;

namespace Elevate.App.Notifications;

/// <summary>Port of the macOS <c>ExpiryNotifying</c> protocol: expiry toasts and one-off notices.</summary>
public interface IExpiryNotifier
{
    /// <summary>Replaces every scheduled expiry notification with one per active assignment.</summary>
    Task RescheduleAsync(
        IReadOnlyList<ActiveAssignment> assignments,
        IReadOnlyDictionary<RoleKey, string> names,
        IReadOnlyDictionary<TenantKey, string> tenantNames);

    /// <summary>Posts a notification immediately.</summary>
    Task NotifyAsync(string title, string body);
}

public sealed class NoopNotifier : IExpiryNotifier
{
    public Task RescheduleAsync(
        IReadOnlyList<ActiveAssignment> assignments,
        IReadOnlyDictionary<RoleKey, string> names,
        IReadOnlyDictionary<TenantKey, string> tenantNames) => Task.CompletedTask;

    public Task NotifyAsync(string title, string body) => Task.CompletedTask;
}
