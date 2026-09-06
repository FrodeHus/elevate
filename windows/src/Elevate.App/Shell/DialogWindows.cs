using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT.Interop;

namespace Elevate.App.Shell;

/// <summary>
/// Chrome shared by the small windows (activation, configure roles, add account, settings, tenant
/// dialogs): Mica, a dialog-style presenter (title bar, not resizable), a size in effective pixels,
/// placement centred on the work area, Escape to close, and registration as the sign-in anchor
/// while the window is open.
/// </summary>
internal static class DialogWindows
{
    public static void Configure(Window window, string title, int width, int height, FrameworkElement root, bool autoHeight = false)
    {
        window.Title = title;
        window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        window.AppWindow.SetPresenter(presenter);
        window.AppWindow.Title = title;
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Elevate.ico");
        if (File.Exists(icon))
        {
            window.AppWindow.SetIcon(icon);
        }

        var hwnd = new HWND(WindowNative.GetWindowHandle(window));
        var scale = PInvoke.GetDpiForWindow(hwnd) / 96.0;
        Place(window, (int)Math.Round(width * scale), (int)Math.Round(height * scale));

        if (autoHeight)
        {
            root.SizeChanged += (_, _) =>
            {
                root.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
                var wanted = (int)Math.Round((root.DesiredSize.Height + TitleBarHeight) * scale);
                var size = window.AppWindow.Size;
                if (Math.Abs(size.Height - wanted) > 2)
                {
                    window.AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width, wanted));
                }
            };
        }

        root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                window.Close();
                e.Handled = true;
            }
        };

        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                App.Current.InteractionAnchor = hwnd;
            }
        };
        window.Closed += (_, _) =>
        {
            if (App.Current.InteractionAnchor == hwnd)
            {
                App.Current.InteractionAnchor = IntPtr.Zero;
            }
        };
    }

    /// <summary>The standard title bar, in effective pixels, which the content's desired size does not include.</summary>
    private const int TitleBarHeight = 32;

    private static void Place(Window window, int width, int height)
    {
        PInvoke.GetCursorPos(out var cursor);
        var work = WindowPlacement.WorkAreaAt(cursor);
        var x = work.left + Math.Max(0, (work.right - work.left - width) / 2);
        var y = work.top + Math.Max(0, (work.bottom - work.top - height) / 2);
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }

    /// <summary>Brings an already open window to the front.</summary>
    public static void Front(Window window)
    {
        window.AppWindow.Show(true);
        PInvoke.SetForegroundWindow(new HWND(WindowNative.GetWindowHandle(window)));
        window.Activate();
    }

    /// <summary>Enter anywhere in the window presses the default button, unless a multi-line box has focus.</summary>
    public static void DefaultButton(FrameworkElement root, Microsoft.UI.Xaml.Controls.Button button)
    {
        root.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter || !button.IsEnabled)
            {
                return;
            }

            if (e.OriginalSource is Microsoft.UI.Xaml.Controls.TextBox { AcceptsReturn: true })
            {
                return;
            }

            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
            peer.Invoke();
            e.Handled = true;
        };
    }
}
