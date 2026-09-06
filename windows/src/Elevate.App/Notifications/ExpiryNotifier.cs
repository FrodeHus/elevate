using System.Text.Json;
using Elevate.Core;
using Elevate.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Elevate.App.Notifications;

/// <summary>
/// Expiry toasts through the Windows App SDK notification manager: one "expires in 5 minutes"
/// toast with an Extend button and one "expired" toast with Activate again, per active assignment.
/// The manager cannot schedule a toast for later, so the timing is kept in-process: this is a
/// tray app that runs for as long as a role is active. Port of the macOS <c>ExpiryNotifier</c>.
/// </summary>
public sealed class ExpiryNotifier : IExpiryNotifier, IDisposable
{
    public const string Group = "elevate-expiry";
    public static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ExpiredDelay = TimeSpan.FromSeconds(5);

    private sealed record Planned(DateTimeOffset FireAt, string Tag, string Title, string Body, string Button, RoleKey Key);

    private readonly Lock _gate = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly List<Planned> _planned = [];
    private Timer? _timer;
    private bool _registered;

    public ExpiryNotifier(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Receives the role to re-activate when the user presses Extend or Activate again; raised on the UI thread.</summary>
    public Action<RoleKey>? OnExtend { get; set; }

    /// <summary>Whether Windows currently lets Elevate show notifications.</summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                return AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Hooks the activation handler and registers the app with the notification platform. Must run
    /// before anything shows a toast, and before the app handles a toast launch.
    /// </summary>
    public void Register()
    {
        if (_registered)
        {
            return;
        }

        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += (_, args) => Handle(args.Arguments);
        manager.Register();
        _registered = true;
    }

    /// <summary>A toast launch (the app was not running): opens the activation window for the toast's role.</summary>
    public void HandleLaunch(AppNotificationActivatedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Handle(args.Arguments);
    }

    private void Handle(IDictionary<string, string> arguments)
    {
        if (!arguments.TryGetValue("action", out var action) || action is not ("extend" or "again" or "open"))
        {
            return;
        }

        if (!arguments.TryGetValue("key", out var json))
        {
            return;
        }

        RoleKey? key;
        try
        {
            key = Json.Deserialize<RoleKey>(json);
        }
        catch (JsonException)
        {
            return;
        }

        if (key is null)
        {
            return;
        }

        _dispatcher.TryEnqueue(() => OnExtend?.Invoke(key));
    }

    public Task RescheduleAsync(
        IReadOnlyList<ActiveAssignment> assignments,
        IReadOnlyDictionary<RoleKey, string> names,
        IReadOnlyDictionary<TenantKey, string> tenantNames)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(tenantNames);

        var planned = new List<Planned>();
        foreach (var a in assignments.Where(a => a.Status.Kind == AssignmentStatusKind.Active))
        {
            if (a.EndDateTime is not { } end)
            {
                continue;
            }

            var id = a.AssignmentId ?? Guid.NewGuid().ToString("N");
            var name = names.GetValueOrDefault(a.RoleKey) ?? "PIM role";
            var tenant = tenantNames.GetValueOrDefault(a.RoleKey.TenantKey) ?? a.RoleKey.TenantId;
            planned.Add(new Planned(end - LeadTime, "expiry-" + id, $"{name} expires in 5 minutes", tenant, "Extend", a.RoleKey));
            planned.Add(new Planned(end + ExpiredDelay, "expired-" + id, $"{name} expired", tenant, "Activate again", a.RoleKey));
        }

        lock (_gate)
        {
            _planned.Clear();
            _planned.AddRange(planned.Where(p => p.FireAt > DateTimeOffset.UtcNow.AddSeconds(1)));
            Arm();
        }

        return Task.CompletedTask;
    }

    public Task NotifyAsync(string title, string body)
    {
        Show(new AppNotificationBuilder().AddText(title).AddText(body).BuildNotification());
        return Task.CompletedTask;
    }

    private void Arm()
    {
        _timer?.Dispose();
        _timer = null;
        if (_planned.Count == 0)
        {
            return;
        }

        var next = _planned.Min(p => p.FireAt);
        var delay = next - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        // Timer periods are capped just under 25 days; a role never lasts that long, but clamp anyway.
        if (delay > TimeSpan.FromDays(20))
        {
            delay = TimeSpan.FromDays(20);
        }

        _timer = new Timer(_ => Fire(), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void Fire()
    {
        List<Planned> due;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            due = _planned.Where(p => p.FireAt <= now).ToList();
            _planned.RemoveAll(p => p.FireAt <= now);
            Arm();
        }

        foreach (var toast in due)
        {
            var keyJson = Json.Serialize(toast.Key);
            var action = toast.Button == "Extend" ? "extend" : "again";
            var builder = new AppNotificationBuilder()
                .AddText(toast.Title)
                .AddText(toast.Body)
                .AddArgument("action", "open")
                .AddArgument("key", keyJson)
                .AddButton(new AppNotificationButton(toast.Button).AddArgument("action", action).AddArgument("key", keyJson))
                .AddButton(new AppNotificationButton("Dismiss").AddArgument("action", "dismiss"))
                .SetTag(toast.Tag)
                .SetGroup(Group);
            Show(builder.BuildNotification());
        }
    }

    private static void Show(AppNotification notification)
    {
        try
        {
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception e)
        {
            App.Log("Toast failed: " + e.Message);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        if (_registered)
        {
            try
            {
                AppNotificationManager.Default.Unregister();
            }
            catch (Exception)
            {
                // Leaving the registration behind is harmless; the next launch re-registers.
            }

            _registered = false;
        }
    }
}
