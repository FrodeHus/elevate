using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Elevate.Core.Support;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Elevate.App.Tray;

/// <summary>
/// Draws the tray glyph at runtime so the count badge stays crisp at every DPI: the app icon's
/// double chevron, the active count beside it, a warning dot when something expires soon and a
/// hollow dot while an activation awaits approval. Monochrome, following the taskbar theme.
/// </summary>
internal static class TrayIconRenderer
{
    /// <summary>
    /// A new icon for <paramref name="status"/>, with an orange dot at the bottom right while
    /// <paramref name="pendingApprovals"/> requests await this user's decision; the caller owns (and must destroy) the handle.
    /// </summary>
    public static HICON Render(PanelStatus status, int pendingApprovals = 0)
    {
        var size = Math.Max(16, (int)Math.Round(16 * PInvoke.GetDpiForSystem() / 96.0));
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var ink = TaskbarIsLight() ? Color.FromArgb(0x1B, 0x1B, 0x1B) : Color.White;
        var scale = size / 16f;
        var hasCount = status.ActiveCount > 0;
        // With a count the chevron moves left and shrinks to leave room for the digits.
        var glyphSize = hasCount ? 9f * scale : 12f * scale;
        var glyphLeft = hasCount ? 0.5f * scale : 2f * scale;
        var glyphTop = (size - glyphSize) / 2f;
        DrawChevrons(g, ink, glyphLeft, glyphTop, glyphSize, scale);

        if (hasCount)
        {
            var text = status.ActiveCount > 9 ? "9+" : status.ActiveCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var font = new Font("Segoe UI", 8.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(ink);
            var textSize = g.MeasureString(text, font);
            g.DrawString(text, font, brush, size - textSize.Width + 1f * scale, (size - textSize.Height) / 2f);
        }

        if (status.ExpiringSoon)
        {
            using var warn = new SolidBrush(Color.FromArgb(0xFC, 0xE1, 0x00));
            var d = 5f * scale;
            g.FillEllipse(warn, size - d, 0, d, d);
        }
        else if (status.PendingApproval)
        {
            using var pen = new Pen(ink, 1f * scale);
            var d = 4f * scale;
            g.DrawEllipse(pen, size - d - 0.5f * scale, 0.5f * scale, d, d);
        }

        if (pendingApprovals > 0)
        {
            using var approvals = new SolidBrush(Color.FromArgb(0xF7, 0x63, 0x0C));
            var d = 5f * scale;
            g.FillEllipse(approvals, size - d, size - d, d, d);
        }

        return new HICON(bitmap.GetHicon());
    }

    /// <summary>The two chevrons of the app icon (SVG "M5 12l7-7 7 7" and "M5 19l7-7 7 7" in a 24-unit box).</summary>
    private static void DrawChevrons(Graphics g, Color ink, float left, float top, float size, float scale)
    {
        var unit = size / 24f;
        using var pen = new Pen(ink, Math.Max(1.2f, 3f * unit))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var faint = new Pen(Color.FromArgb(128, ink), pen.Width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        PointF P(float x, float y) => new(left + x * unit, top + y * unit);
        g.DrawLines(pen, [P(5, 12), P(12, 5), P(19, 12)]);
        g.DrawLines(faint, [P(5, 19), P(12, 12), P(19, 19)]);
        _ = scale;
    }

    /// <summary>The taskbar follows the system (not the app) theme; light means dark ink.</summary>
    private static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
