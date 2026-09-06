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
            e.Handled = true;
        };
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
        await Model!.BootstrapAsync();
        // Developer switch: `Elevate.exe --flyout` opens the flyout at once, for screenshots and smoke tests.
        if (Environment.GetCommandLineArgs().Contains("--flyout", StringComparer.OrdinalIgnoreCase))
        {
            _flyout?.Show(_tray?.IconRect);
        }
    }

    /// <summary>Production wiring. The client id lives in AppSettings; when it is missing the flyout shows the setup state.</summary>
    private static AppModel Live()
    {
        var settings = new AppSettings();
        var http = new HttpClientAdapter();
        var firstParty = new NoFirstPartyProviders();
        var tokens = new CompositeTokenProvider(null, firstParty);
        return new AppModel(tokens, http, new AppStateStore(), new NoopNotifier(), new NetworkMonitor(), settings, firstParty);
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

    public void OpenSettings()
    {
        // The Settings window arrives with the windows task.
        _flyout?.Hide();
    }

    public void OpenAddAccount()
    {
        // The Add account window arrives with the windows task.
        _flyout?.Hide();
    }

    public void Quit()
    {
        _flyout?.Hide();
        _tray?.Dispose();
        _tray = null;
        Model?.Dispose();
        Exit();
    }

    /// <summary>Stands in until the MSAL providers land: no first-party sign-in is possible yet.</summary>
    private sealed class NoFirstPartyProviders : IFirstPartyProviders
    {
        public ITokenProvider? Provider(SignInMethod method) => null;

        public IReadOnlyCollection<ITokenProvider> Known { get; } = [];
    }
}
