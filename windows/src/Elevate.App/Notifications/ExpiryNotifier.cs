using Elevate.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Elevate.App.Notifications;

/// <summary>
/// Expiry toasts: one "expires in 5 minutes" toast with an Extend button and one "expired" toast
/// with Activate again, per active assignment. The toasts are handed to Windows as
/// <see cref="ScheduledToastNotification"/>s so they fire even when Elevate is not running, the
/// way <c>UNTimeIntervalNotificationTrigger</c> does on macOS; a click then launches the app
/// through the COM activator <see cref="AppNotificationManager.Register"/> set up. When the OS
/// schedule is unavailable the timing falls back to an in-process timer. Port of the macOS
/// <c>ExpiryNotifier</c>.
/// </summary>
public sealed class ExpiryNotifier : IExpiryNotifier, IDisposable
{
    public const string Group = ExpiryPlan.Group;
    public static readonly TimeSpan LeadTime = ExpiryPlan.LeadTime;
    public static readonly TimeSpan ExpiredDelay = ExpiryPlan.ExpiredDelay;

    private readonly Lock _gate = new();
    private readonly DispatcherQueue _dispatcher;
    /// <summary>Toasts the in-process timer owns: everything when scheduling is unavailable, else only what the OS refused.</summary>
    private readonly List<PlannedToast> _planned = [];
    private Timer? _timer;
    private bool _registered;
    private ToastNotifier? _scheduler;
    private bool _fallbackLogged;

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

    /// <summary>Whether expiry toasts are handed to the OS schedule (true) or timed in-process (false).</summary>
    public bool SchedulesWithSystem => _scheduler is not null;

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
        _scheduler = CreateScheduler();
    }

    /// <summary>
    /// The toast notifier for the app entry <see cref="AppNotificationManager.Register"/> created.
    /// The SDK derives the AUMID from the executable path and does not expose it, so it is read
    /// back from the registry through the COM activator it registered for this executable.
    /// </summary>
    private static ToastNotifier? CreateScheduler()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                App.Log("Toasts: process path unknown; timing expiry toasts in-process");
                return null;
            }

            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes");
            using var aumids = classes?.OpenSubKey("AppUserModelId");
            if (classes is null || aumids is null)
            {
                App.Log("Toasts: no AppUserModelId registrations; timing expiry toasts in-process");
                return null;
            }

            var entries = aumids.GetSubKeyNames().Select(id =>
            {
                using var key = aumids.OpenSubKey(id);
                return new AppUserModelEntry(id, key?.GetValue("CustomActivator") as string);
            });
            var aumid = NotificationAppId.Find(exe, entries, clsid =>
            {
                using var server = classes.OpenSubKey(@"CLSID\" + clsid + @"\LocalServer32");
                return server?.GetValue(null) as string;
            });
            if (aumid is null)
            {
                App.Log("Toasts: no notification app id registered for " + exe + "; timing expiry toasts in-process");
                return null;
            }

            var notifier = ToastNotificationManager.CreateToastNotifier(aumid);
            // Prove the schedule is reachable now rather than on the first refresh.
            _ = notifier.GetScheduledToastNotifications();
            App.Log("Toasts: scheduling expiry toasts with Windows as " + aumid);
            return notifier;
        }
        catch (Exception e)
        {
            App.Log("Toasts: OS scheduling unavailable (" + e.Message + "); timing expiry toasts in-process");
            return null;
        }
    }

    /// <summary>A toast launch (the app was not running): opens the activation window for the toast's role.</summary>
    public void HandleLaunch(AppNotificationActivatedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Handle(args.Arguments);
    }

    private void Handle(IDictionary<string, string> arguments)
    {
        if (ExpiryPlan.RoleFor(arguments) is not { } key)
        {
            return;
        }

        App.Log($"Toast action '{arguments[ExpiryPlan.ActionArgument]}' for {key.TenantId}");
        _dispatcher.TryEnqueue(() => OnExtend?.Invoke(key));
    }

    public Task RescheduleAsync(
        IReadOnlyList<ActiveAssignment> assignments,
        IReadOnlyDictionary<RoleKey, string> names,
        IReadOnlyDictionary<TenantKey, string> tenantNames)
    {
        Replace(ExpiryPlan.ForRoles(assignments, names, tenantNames), ExpiryPlan.IsRoleTag, p => p.IsRoleToast);
        return Task.CompletedTask;
    }

    /// <summary>
    /// One "expired" toast per delivered access package assignment with an end date, timed to the
    /// end date so it fires on time even between polls. Replaces the previous set, so an
    /// assignment that was revoked drops its pending toast.
    /// </summary>
    public Task SetPackageExpiriesAsync(IReadOnlyList<PackageExpiry> expiries)
    {
        Replace(ExpiryPlan.ForPackages(expiries), ExpiryPlan.IsPackageTag, p => !p.IsRoleToast);
        return Task.CompletedTask;
    }

    /// <summary>Developer switch: one Extend toast for a stand-in role, <paramref name="delay"/> from now.</summary>
    public void ScheduleTestToast(TimeSpan delay)
    {
        var key = new RoleKey("dev-identity", "dev-tenant", new EntraDirectoryScope("dev-role", "/"));
        var toast = new PlannedToast(DateTimeOffset.UtcNow + delay, "expiry-test", "Test role expires in 5 minutes", "Developer toast", ExpiryPlan.ExtendButton, key);
        Replace([toast], tag => tag == "expiry-test", p => p.Tag == "expiry-test");
    }

    /// <summary>
    /// Replaces one family of toasts (role or package) with <paramref name="wanted"/>. With the OS
    /// schedule, the family's stale entries are removed and the new ones added; whatever the OS
    /// refuses, and everything when the schedule is unavailable, goes to the in-process timer. A
    /// toast lives in exactly one of the two, so nothing fires twice.
    /// </summary>
    private void Replace(IReadOnlyList<PlannedToast> wanted, Func<string, bool> ownsTag, Func<PlannedToast, bool> inFamily)
    {
        var pending = ExpiryPlan.Pending(wanted, DateTimeOffset.UtcNow);
        var forTimer = _scheduler is null ? pending : ScheduleWithSystem(pending, ownsTag);
        lock (_gate)
        {
            _planned.RemoveAll(p => inFamily(p));
            _planned.AddRange(forTimer);
            Arm();
        }
    }

    /// <summary>Reconciles the OS schedule with <paramref name="wanted"/>; returns the toasts it could not schedule.</summary>
    private List<PlannedToast> ScheduleWithSystem(IReadOnlyList<PlannedToast> wanted, Func<string, bool> ownsTag)
    {
        var notifier = _scheduler!;
        try
        {
            var existing = notifier.GetScheduledToastNotifications()
                .Where(s => s.Group == Group)
                .ToList();
            var changes = ExpiryPlan.Reconcile(existing.Select(s => new ScheduledEntry(s.Tag, s.DeliveryTime)), wanted, ownsTag);
            foreach (var stale in existing.Where(s => changes.Remove.Contains(s.Tag, StringComparer.Ordinal)))
            {
                notifier.RemoveFromSchedule(stale);
            }

            var refused = new List<PlannedToast>();
            foreach (var toast in changes.Add)
            {
                try
                {
                    var document = new XmlDocument();
                    document.LoadXml(Build(toast).Payload);
                    notifier.AddToSchedule(new ScheduledToastNotification(document, toast.FireAt) { Tag = toast.Tag, Group = Group });
                }
                catch (Exception e)
                {
                    LogFallbackOnce("Toasts: could not schedule " + toast.Tag + " (" + e.Message + "); timing it in-process");
                    refused.Add(toast);
                }
            }

            return refused;
        }
        catch (Exception e)
        {
            // The schedule itself failed: nothing was changed, so the whole set is timed in-process.
            LogFallbackOnce("Toasts: OS schedule failed (" + e.Message + "); timing expiry toasts in-process");
            return wanted.ToList();
        }
    }

    private void LogFallbackOnce(string message)
    {
        if (_fallbackLogged)
        {
            return;
        }

        _fallbackLogged = true;
        App.Log(message);
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
        List<PlannedToast> due;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            due = _planned.Where(p => p.FireAt <= now).ToList();
            _planned.RemoveAll(p => p.FireAt <= now);
            Arm();
        }

        foreach (var toast in due)
        {
            Show(Build(toast));
        }
    }

    /// <summary>The toast content: the same text, buttons and arguments whether shown now or scheduled.</summary>
    private static AppNotification Build(PlannedToast toast)
    {
        var builder = new AppNotificationBuilder()
            .AddText(toast.Title)
            .AddText(toast.Body)
            .SetTag(toast.Tag)
            .SetGroup(Group);
        if (toast.Key is null)
        {
            // A package expiry has no action: nothing in Elevate can renew it.
            return builder.BuildNotification();
        }

        var keyJson = ExpiryPlan.EncodeKey(toast.Key);
        return builder
            .AddArgument(ExpiryPlan.ActionArgument, ExpiryPlan.OpenAction)
            .AddArgument(ExpiryPlan.KeyArgument, keyJson)
            .AddButton(new AppNotificationButton(toast.Button).AddArgument(ExpiryPlan.ActionArgument, toast.Action!).AddArgument(ExpiryPlan.KeyArgument, keyJson))
            .AddButton(new AppNotificationButton("Dismiss").AddArgument(ExpiryPlan.ActionArgument, "dismiss"))
            .BuildNotification();
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
        // Scheduled toasts stay with the OS on purpose: they are the point of scheduling.
        _scheduler = null;
        if (_registered)
        {
            try
            {
                // Unregister (not UnregisterAll) keeps the COM activator, so a scheduled toast can still launch the app.
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
