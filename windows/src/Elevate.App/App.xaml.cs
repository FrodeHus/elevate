using Elevate.App.Auth;
using Elevate.App.Notifications;
using Elevate.App.Services;
using Elevate.App.Shell;
using Elevate.App.Tray;
using Elevate.App.ViewModels;
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
        Model = Live();
        Model.Changed += (_, _) => UpdateTray();

        _tray = new TrayIcon("Elevate");
        _tray.LeftClick += anchor => _flyout?.Toggle(anchor);
        _tray.MenuCommand += OnTrayMenu;
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
            // Developer switch: `Elevate.exe --flyout` opens the flyout at once, for screenshots and smoke tests.
            if (Environment.GetCommandLineArgs().Contains("--flyout", StringComparer.OrdinalIgnoreCase))
            {
                _flyout?.Show(_tray?.IconRect);
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
    private AppModel Live()
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
        var model = new AppModel(tokens, http, new AppStateStore(), new NoopNotifier(), new NetworkMonitor(), settings,
            firstParty, ownApp, MakeOwnApp);
        if (initError is not null)
        {
            model.Notice = $"Could not initialise sign-in with the saved client ID: {initError}. Check it in Settings.";
            model.LogError($"Sign-in setup: {initError}");
        }

        return model;
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

    // The windows themselves arrive with the windows task; until then these only close the flyout.

    public void OpenSettings() => _flyout?.Hide();

    public void OpenAddAccount(SignInMethod? preselected = null) => _flyout?.Hide();

    public void OpenActivation(IReadOnlyList<RoleKey> keys) => _flyout?.Hide();

    public void OpenConfigureRoles(TenantKey tenantKey) => _flyout?.Hide();

    public void OpenAddTenant(string identityId) => _flyout?.Hide();

    public void OpenDiscoverTenants(string identityId) => _flyout?.Hide();

    public void Quit()
    {
        _flyout?.Hide();
        _tray?.Dispose();
        _tray = null;
        Model?.Dispose();
        Exit();
    }
}
