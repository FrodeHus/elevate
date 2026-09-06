using Elevate.App.Auth;
using Elevate.App.Notifications;
using Elevate.App.Services;
using Elevate.App.Shell;
using Elevate.App.Tray;
using Elevate.App.ViewModels;
using Elevate.App.Views;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Storage;
using Elevate.Core.Support;
using Microsoft.UI.Xaml;

namespace Elevate.App;

public partial class App : Application
{
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private ExpiryNotifier? _notifier;
    private PanelStatus _drawnStatus = new(-1, false, false);

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // A failure in a view must not take the tray icon down with it.
            Model?.LogError("Unhandled: " + e.Message);
            Log("Unhandled exception: " + e.Message + Environment.NewLine + e.Exception);
            e.Handled = true;
        };
    }

    /// <summary>Appends one line to <c>%LOCALAPPDATA%\Elevate\elevate.log</c>; the only place failures of the shell itself go.</summary>
    public static void Log(string message)
    {
        try
        {
            var path = Path.Combine(AppStateStore.DefaultDirectory, "elevate.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    public static new App Current => (App)Application.Current;

    public AppModel? Model { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Toast activation must be wired before anything else: a click on a toast while the app is
        // not running launches it, and the platform expects the registration to happen first.
        _notifier = new ExpiryNotifier(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        _notifier.OnExtend = key =>
        {
            if (Model is not null)
            {
                Model.PendingExtend = key;
            }
        };
        try
        {
            _notifier.Register();
        }
        catch (Exception e)
        {
            Log("Notification registration failed: " + e.Message);
        }

        Model = Live(_notifier);
        Model.Changed += (_, _) =>
        {
            UpdateTray();
            if (Model.PendingExtend is { } key)
            {
                Model.PendingExtend = null;
                OpenActivation([key]);
            }
        };

        _tray = new TrayIcon("Elevate");
        _tray.LeftClick += anchor => _flyout?.Toggle(anchor);
        _tray.MenuCommand += OnTrayMenu;
        _tray.OpenRequested += () => _flyout?.Show(_tray?.IconRect);
        _tray.Invalidated += () =>
        {
            _drawnStatus = new PanelStatus(-1, false, false);
            UpdateTray();
        };
        UpdateTray();

        _flyout = new FlyoutWindow(Model);
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await Model!.BootstrapAsync();
            if (_notifier is { IsEnabled: false })
            {
                Model.Notice = "Notifications are off for Elevate; enable them in Settings > System > Notifications to get expiry alerts.";
                Model.LogError("Notifications are not enabled for Elevate");
            }

            // Launched by a toast (the app was not running): the toast's role goes straight to activation.
            var activation = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification
                && activation.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs toast)
            {
                _notifier?.HandleLaunch(toast);
            }
            // Developer switches, for screenshots and smoke tests: `--flyout` opens the flyout at once,
            // `--show <settings|add-account|configure|activation|bulk|add-tenant|discover>` opens one window.
            var args = Environment.GetCommandLineArgs();
            if (args.Contains("--flyout", StringComparer.OrdinalIgnoreCase))
            {
                _flyout?.Show(_tray?.IconRect);
            }

            var show = Array.IndexOf(args, "--show");
            if (show >= 0 && show + 1 < args.Length)
            {
                ShowForDevelopment(args[show + 1]);
            }
        }
        catch (Exception e)
        {
            Log("Startup failed: " + e);
            if (Model is not null)
            {
                Model.StartupError = e.Message;
            }
        }
    }

    /// <summary>
    /// The window a sign-in dialog should be parented to: the window that asked for it when one is
    /// open, else the flyout, else the tray's hidden window (a top-level window is all WAM needs).
    /// </summary>
    public IntPtr InteractionAnchor { get; set; }

    private IntPtr AnchorHandle()
    {
        if (InteractionAnchor != IntPtr.Zero)
        {
            return InteractionAnchor;
        }

        if (_flyout is { IsOpen: true })
        {
            return _flyout.Handle;
        }

        return _tray?.Handle ?? IntPtr.Zero;
    }

    /// <summary>
    /// Production wiring. The client id lives in AppSettings; when it is missing or unusable the
    /// flyout shows the setup state instead of a startup error.
    /// </summary>
    private AppModel Live(IExpiryNotifier notifier)
    {
        var settings = new AppSettings();
        var http = new HttpClientAdapter();
        var cache = new TokenCache(settings.Directory);
        // One gate across every provider, so no two interactive sign-ins can run at the same time.
        var gate = new InteractiveGate();
        Func<IntPtr> anchor = AnchorHandle;
        var firstParty = new FirstPartyProviderRegistry(cache, gate, anchor);
        IOwnAppTokenProvider MakeOwnApp(string clientId) => new MsalTokenProvider(clientId, cache, gate, anchor);

        IOwnAppTokenProvider? ownApp = null;
        string? initError = null;
        if (settings.IsConfigured)
        {
            try
            {
                ownApp = MakeOwnApp(settings.ClientId);
            }
            catch (Exception e) when (e is PimException or InvalidOperationException)
            {
                initError = e is PimException pim ? pim.UserMessage : e.Message;
            }
        }

        var tokens = new CompositeTokenProvider(ownApp, firstParty);
        var model = new AppModel(tokens, http, new AppStateStore(), notifier, new NetworkMonitor(), settings,
            firstParty, ownApp, MakeOwnApp);
        if (initError is not null)
        {
            model.Notice = $"Could not initialise sign-in with the saved client ID: {initError}. Check it in Settings.";
            model.LogError($"Sign-in setup: {initError}");
        }

        return model;
    }

    private void ShowForDevelopment(string name)
    {
        var model = Model!;
        var identity = model.Identities.FirstOrDefault();
        var tenant = model.State.Tenants.FirstOrDefault();
        switch (name.ToLowerInvariant())
        {
            case "settings":
                OpenSettings();
                break;
            case "add-account":
                OpenAddAccount();
                break;
            case "configure" when tenant is not null:
                OpenConfigureRoles(tenant.Key);
                break;
            case "activation" when tenant is not null:
                OpenActivation([.. model.RolesFor(tenant.Key).Take(1).Select(r => r.Key)]);
                break;
            case "bulk" when tenant is not null:
                OpenActivation([.. model.Roles.Values.SelectMany(r => r).Take(3).Select(r => r.Key)]);
                break;
            case "add-tenant" when identity is not null:
                OpenAddTenant(identity.Id);
                break;
            case "discover" when identity is not null:
                OpenDiscoverTenants(identity.Id);
                break;
            default:
                Log("Nothing to show for --show " + name);
                break;
        }
    }

    private void UpdateTray()
    {
        if (Model is null || _tray is null)
        {
            return;
        }

        var status = PanelStatus.Compute(Model.Active.Values, Model.Clock);
        if (status == _drawnStatus)
        {
            return;
        }

        _drawnStatus = status;
        _tray.SetIcon(TrayIconRenderer.Render(status));
    }

    private void OnTrayMenu(TrayMenuItem item)
    {
        switch (item)
        {
            case TrayMenuItem.Open:
                _flyout?.Show(_tray?.IconRect);
                break;
            case TrayMenuItem.Settings:
                OpenSettings();
                break;
            case TrayMenuItem.Quit:
                Quit();
                break;
            default:
                break;
        }
    }

    // MARK: Windows

    /// <summary>The open secondary windows, one per kind (and per activation key set), so a repeat request fronts the existing one.</summary>
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);

    private void Open(string key, Func<Window> create)
    {
        _flyout?.Hide();
        if (_windows.TryGetValue(key, out var existing))
        {
            DialogWindows.Front(existing);
            return;
        }

        var window = create();
        _windows[key] = window;
        window.Closed += (_, _) => _windows.Remove(key);
        window.Activate();
        DialogWindows.Front(window);
    }

    public void OpenSettings() => Open("settings", () => new SettingsWindow(Model!));

    public void OpenAddAccount(SignInMethod? preselected = null) => Open("add-account", () => new AddAccountWindow(Model!, preselected));

    public void OpenActivation(IReadOnlyList<RoleKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return;
        }

        // One activation window at a time: a new request replaces the old one.
        var wanted = "activate:" + string.Join("|", keys);
        foreach (var (key, window) in _windows.Where(p => p.Key.StartsWith("activate:", StringComparison.Ordinal) && p.Key != wanted).ToList())
        {
            window.Close();
        }

        Open(wanted, () => new ActivationWindow(Model!, keys));
    }

    public void OpenConfigureRoles(TenantKey tenantKey) => Open("configure:" + tenantKey, () => new ConfigureRolesWindow(Model!, tenantKey));

    public void OpenAddTenant(string identityId) => Open("add-tenant:" + identityId, () => new TenantWindow(Model!, identityId, TenantWindowMode.Add));

    public void OpenDiscoverTenants(string identityId) => Open("discover:" + identityId, () => new TenantWindow(Model!, identityId, TenantWindowMode.Discover));

    public void Quit()
    {
        _flyout?.Hide();
        foreach (var window in _windows.Values.ToList())
        {
            window.Close();
        }

        _tray?.Dispose();
        _tray = null;
        _notifier?.Dispose();
        _notifier = null;
        Model?.Dispose();
        Exit();
    }
}
