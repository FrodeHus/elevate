using Elevate.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using WinRT.Interop;

namespace Elevate.App.Shell;

/// <summary>
/// The tray flyout: a borderless, non-resizable, always-on-top window with a Mica Alt backdrop,
/// 380 px wide and as tall as its content (up to 640), placed above the taskbar near the icon.
/// Hides when it loses activation, like the Wi-Fi and Quick Settings flyouts.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private const int Width = 380;
    private const int MaxHeight = 640;
    private const int ListSlack = 12;

    private readonly OverlappedPresenter _presenter;
    private DateTimeOffset _hiddenAt = DateTimeOffset.MinValue;
    private DateTimeOffset _shownAt = DateTimeOffset.MinValue;
    private bool _shown;

    public FlyoutWindow(AppModel model)
    {
        InitializeComponent();
        Model = model;
        Panel.Bind(model, this);
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };

        _presenter = OverlappedPresenter.Create();
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsAlwaysOnTop = true;
        _presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(_presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "Elevate";

        var hwnd = new HWND(WindowNative.GetWindowHandle(this));
        var preference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        unsafe
        {
            PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &preference, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        }

        Activated += OnActivated;
        Root.SizeChanged += (_, _) => FitToContent();
        // After the layout pass that realises the new rows, never inside the redraw itself.
        Panel.ContentChanged += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FitToContent);
    }

    public AppModel Model { get; }

    internal HWND Handle => new(WindowNative.GetWindowHandle(this));

    public bool IsOpen => _shown;

    /// <summary>
    /// Opens the flyout near <paramref name="anchor"/> (the tray icon's rectangle) or the cursor.
    /// A click on the tray icon while the flyout is open deactivates it first, which hides it, so a
    /// toggle arriving right after a hide is treated as "close", not "reopen".
    /// </summary>
    internal void Toggle(RECT? anchor)
    {
        if (_shown)
        {
            Hide();
            return;
        }

        if (DateTimeOffset.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        Show(anchor);
    }

    internal void Show(RECT? anchor)
    {
        Model.PanelOpened();
        Panel.ResetSearch();
        Panel.Refresh();
        _shown = true;
        _shownAt = DateTimeOffset.UtcNow;
        Root.UpdateLayout();
        Place(anchor);
        AppWindow.Show(true);
        PInvoke.SetForegroundWindow(Handle);
        Activate();
    }

    public void Hide()
    {
        if (!_shown)
        {
            return;
        }

        _shown = false;
        _hiddenAt = DateTimeOffset.UtcNow;
        AppWindow.Hide();
    }

    private double Scale => PInvoke.GetDpiForWindow(Handle) / 96.0;

    private void Place(RECT? anchor)
    {
        var (width, height) = PixelSize();
        var origin = WindowPlacement.FlyoutOrigin(width, height, anchor);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(origin.X, origin.Y, width, height));
    }

    private (int Width, int Height) PixelSize()
    {
        // Let the list realise its containers first, so the unbounded measure below sees every row.
        Root.UpdateLayout();
        Root.Measure(new Windows.Foundation.Size(Width, double.PositiveInfinity));
        // The list's unbounded measure runs a few pixels short of its drawn height; give it room.
        var desired = Math.Max(44, Math.Min(MaxHeight, Root.DesiredSize.Height + (Panel.ListVisible ? ListSlack : 0)));
        return ((int)Math.Round(Width * Scale), (int)Math.Round(desired * Scale));
    }

    /// <summary>Content grew or shrank while open: keep the bottom edge anchored to the taskbar.</summary>
    private void FitToContent()
    {
        if (!_shown)
        {
            return;
        }

        var (width, height) = PixelSize();
        var current = AppWindow.Size;
        if (current.Height == height && current.Width == width)
        {
            return;
        }

        var position = AppWindow.Position;
        var bottom = position.Y + current.Height;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(position.X, bottom - height, width, height));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // A deactivation in the first moments after Show is the foreground fight of the launch itself
        // (the shell may refuse to give a new window focus), not the user clicking elsewhere.
        if (args.WindowActivationState == WindowActivationState.Deactivated
            && DateTimeOffset.UtcNow - _shownAt > TimeSpan.FromMilliseconds(500))
        {
            Hide();
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }
}
