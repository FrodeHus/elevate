using System.Drawing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;

namespace Elevate.App.Shell;

/// <summary>Where the taskbar is and where a flyout of a given size should go next to the tray.</summary>
internal static class WindowPlacement
{
    private const int Margin = 12;

    public enum Edge
    {
        Left,
        Top,
        Right,
        Bottom,
    }

    public readonly record struct Taskbar(RECT Rect, Edge Edge);

    /// <summary>The taskbar's rectangle and edge in screen pixels, or null when the shell will not say.</summary>
    public static unsafe Taskbar? FindTaskbar()
    {
        var data = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
        if (PInvoke.SHAppBarMessage(PInvoke.ABM_GETTASKBARPOS, ref data) == 0)
        {
            return null;
        }

        var edge = data.uEdge switch
        {
            0 => Edge.Left,
            1 => Edge.Top,
            2 => Edge.Right,
            _ => Edge.Bottom,
        };
        return new Taskbar(data.rc, edge);
    }

    /// <summary>The work area (screen minus taskbar) of the monitor holding <paramref name="point"/>.</summary>
    public static unsafe RECT WorkAreaAt(Point point)
    {
        var monitor = PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return info.rcWork;
        }

        return new RECT { left = 0, top = 0, right = 1920, bottom = 1080 };
    }

    /// <summary>
    /// Top-left corner for a <paramref name="width"/>×<paramref name="height"/> pixel flyout, anchored
    /// to the tray icon (or the cursor) and kept inside the work area, above (or beside) the taskbar.
    /// </summary>
    public static Point FlyoutOrigin(int width, int height, RECT? anchor)
    {
        PInvoke.GetCursorPos(out var cursor);
        var anchorX = anchor is { } a ? (a.left + a.right) / 2 : cursor.X;
        var anchorY = anchor is { } b ? (b.top + b.bottom) / 2 : cursor.Y;
        var work = WorkAreaAt(new Point(anchorX, anchorY));
        var taskbar = FindTaskbar();

        int x, y;
        switch (taskbar?.Edge ?? Edge.Bottom)
        {
            case Edge.Top:
                x = anchorX - width / 2;
                y = work.top + Margin;
                break;
            case Edge.Left:
                x = work.left + Margin;
                y = anchorY - height / 2;
                break;
            case Edge.Right:
                x = work.right - width - Margin;
                y = anchorY - height / 2;
                break;
            default:
                x = anchorX - width / 2;
                y = work.bottom - height - Margin;
                break;
        }

        x = Math.Max(work.left + Margin, Math.Min(x, work.right - width - Margin));
        y = Math.Max(work.top + Margin, Math.Min(y, work.bottom - height - Margin));
        return new Point(x, y);
    }
}
