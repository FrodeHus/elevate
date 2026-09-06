using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Elevate.App.Tray;

/// <summary>
/// The notification-area icon, over <c>Shell_NotifyIcon</c>. Owns a hidden top-level window
/// whose procedure receives the icon's callbacks and the shell's <c>TaskbarCreated</c> broadcast
/// (which a message-only window would never see), so the icon survives an Explorer restart.
/// Must be created and used on the UI thread; the WinUI dispatcher pumps its messages.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = PInvoke.WM_USER + 1;
    private const string ClassName = "Reothor.Elevate.Tray";

    private readonly WNDPROC _wndProc;
    private readonly uint _taskbarCreated;
    private readonly string _tooltip;
    private HWND _hwnd;
    private HICON _icon;
    private bool _added;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;
        _wndProc = WndProc;
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        RegisterClass();
        fixed (char* className = ClassName)
        fixed (char* title = "Elevate")
        {
            _hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, className, title, WINDOW_STYLE.WS_OVERLAPPED,
                0, 0, 0, 0, HWND.Null, HMENU.Null, PInvoke.GetModuleHandle((char*)null), null);
        }

        if (_hwnd.IsNull)
        {
            throw new InvalidOperationException("Could not create the tray window: " + Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Raised on the UI thread on a left click. The argument is the icon's screen rectangle when known.</summary>
    public event Action<RECT?>? LeftClick;

    /// <summary>Raised on the UI thread when the user picks a context-menu item.</summary>
    public event Action<TrayMenuItem>? MenuCommand;

    /// <summary>Raised when the taskbar's DPI or theme changed, so the icon can be redrawn.</summary>
    public event Action? Invalidated;

    public HWND Handle => _hwnd;

    /// <summary>Adds the icon, or replaces its image once added. Takes ownership of <paramref name="icon"/>.</summary>
    public void SetIcon(HICON icon)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
        }

        _icon = icon;
        if (_added)
        {
            var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
        }
        else
        {
            Add();
        }
    }

    /// <summary>The icon's rectangle in screen pixels, or null when the shell will not say (hidden in the overflow).</summary>
    public RECT? IconRect
    {
        get
        {
            var id = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER),
                hWnd = _hwnd,
                uID = IconId,
            };
            return PInvoke.Shell_NotifyIconGetRect(in id, out var rect).Succeeded ? rect : null;
        }
    }

    /// <summary>Shows the native context menu at the cursor and raises <see cref="MenuCommand"/> for the choice.</summary>
    public void ShowContextMenu()
    {
        var menu = PInvoke.CreatePopupMenu();
        try
        {
            Append(menu, MENU_ITEM_FLAGS.MF_STRING, TrayMenuItem.Open, "Open");
            Append(menu, MENU_ITEM_FLAGS.MF_STRING, TrayMenuItem.Settings, "Settings…");
            Append(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
            Append(menu, MENU_ITEM_FLAGS.MF_STRING, TrayMenuItem.Quit, "Quit Elevate");
            PInvoke.GetCursorPos(out var point);
            // The menu closes on its own only when our window is in the foreground (a known shell quirk).
            PInvoke.SetForegroundWindow(_hwnd);
            var flags = TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON
                | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN | TRACK_POPUP_MENU_FLAGS.TPM_LEFTALIGN | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY;
            var chosen = PInvoke.TrackPopupMenuEx(menu, (uint)flags, point.X, point.Y, _hwnd, (TPMPARAMS*)null);
            PInvoke.PostMessage(_hwnd, PInvoke.WM_NULL, 0, 0);
            if (chosen.Value != 0)
            {
                MenuCommand?.Invoke((TrayMenuItem)chosen.Value);
            }
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }

    private static void Append(HMENU menu, MENU_ITEM_FLAGS flags, TrayMenuItem id, string? text)
    {
        fixed (char* p = text)
        {
            PInvoke.AppendMenu(menu, flags, (nuint)id, new PCWSTR(p));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            var data = Data(0);
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
            _added = false;
        }

        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = default;
        }

        if (!_hwnd.IsNull)
        {
            PInvoke.DestroyWindow(_hwnd);
            _hwnd = default;
        }

        GC.KeepAlive(_wndProc);
    }

    private void Add()
    {
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON
            | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data))
        {
            return;
        }

        data.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in data);
        _added = true;
    }

    private NOTIFYICONDATAW Data(NOTIFY_ICON_DATA_FLAGS flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        data.szTip = _tooltip;
        return data;
    }

    private void RegisterClass()
    {
        fixed (char* className = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = PInvoke.GetModuleHandle((char*)null),
                lpszClassName = className,
            };
            // A second registration (same process, after a dispose) fails harmlessly: the class exists.
            PInvoke.RegisterClassEx(in wc);
        }
    }

    private LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4: the message is in the low word of lParam.
            switch ((uint)(lParam.Value & 0xFFFF))
            {
                case PInvoke.WM_LBUTTONUP:
                    LeftClick?.Invoke(IconRect);
                    return new LRESULT(0);
                case PInvoke.WM_RBUTTONUP:
                case PInvoke.WM_CONTEXTMENU:
                    ShowContextMenu();
                    return new LRESULT(0);
                default:
                    return new LRESULT(0);
            }
        }

        if (msg == _taskbarCreated && !_disposed)
        {
            // Explorer restarted: every icon is gone and must be added again.
            _added = false;
            if (!_icon.IsNull)
            {
                Add();
            }

            Invalidated?.Invoke();
            return new LRESULT(0);
        }

        if (msg is PInvoke.WM_SETTINGCHANGE or PInvoke.WM_DPICHANGED)
        {
            Invalidated?.Invoke();
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}

public enum TrayMenuItem
{
    Open = 1,
    Settings = 2,
    Quit = 3,
}
