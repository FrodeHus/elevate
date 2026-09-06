using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Elevate.App.Views;

/// <summary>The 8 px status dot at the start of a row: filled by status, outlined when nothing is active.</summary>
public sealed partial class StatusDot : ContentControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(RowStatus), typeof(StatusDot), new PropertyMetadata(RowStatus.None, (d, _) => ((StatusDot)d).Apply()));

    private readonly Ellipse _dot = new() { Width = 8, Height = 8, StrokeThickness = 1.5 };

    public StatusDot()
    {
        Content = _dot;
        Padding = new Thickness(0);
        IsTabStop = false;
        Apply();
    }

    public RowStatus Status
    {
        get => (RowStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    private void Apply()
    {
        var (fill, stroke) = Status switch
        {
            RowStatus.Active => ("SystemFillColorSuccessBrush", "SystemFillColorSuccessBrush"),
            RowStatus.Scheduled => ("AccentFillColorDefaultBrush", "AccentFillColorDefaultBrush"),
            RowStatus.Pending or RowStatus.Provisioning => ("SystemFillColorCautionBrush", "SystemFillColorCautionBrush"),
            RowStatus.Failed => ("SystemFillColorCriticalBrush", "SystemFillColorCriticalBrush"),
            _ => (null, "TextFillColorTertiaryBrush"),
        };
        _dot.Fill = fill is null ? null : (Brush)Application.Current.Resources[fill];
        _dot.Stroke = (Brush)Application.Current.Resources[stroke];
    }
}

/// <summary>A small tinted capsule for a status the user should notice but not read at length; the full text is the tooltip.</summary>
public sealed partial class Pill : ContentControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Pill), new PropertyMetadata(string.Empty, (d, _) => ((Pill)d).Apply()));

    public static readonly DependencyProperty TooltipProperty = DependencyProperty.Register(
        nameof(Tooltip), typeof(string), typeof(Pill), new PropertyMetadata(null, (d, _) => ((Pill)d).Apply()));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(string), typeof(Pill), new PropertyMetadata("Neutral", (d, _) => ((Pill)d).Apply()));

    private readonly Border _border = new() { CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 1), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new() { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    public Pill()
    {
        _border.Child = _label;
        Content = _border;
        Padding = new Thickness(0);
        Margin = new Thickness(0);
        IsTabStop = false;
        VerticalAlignment = VerticalAlignment.Center;
        Apply();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Tooltip
    {
        get => (string?)GetValue(TooltipProperty);
        set => SetValue(TooltipProperty, value);
    }

    /// <summary>"Caution", "Critical", "Success" or "Neutral".</summary>
    public string Tint
    {
        get => (string)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    private void Apply()
    {
        _label.Text = Text;
        ToolTipService.SetToolTip(this, Tooltip);
        var resources = Application.Current.Resources;
        switch (Tint)
        {
            case "Caution":
                _border.Background = new SolidColorBrush(WithAlpha((Windows.UI.Color)resources["SystemFillColorCaution"], 0x24));
                _label.Foreground = (Brush)resources["SystemFillColorCautionBrush"];
                _border.BorderThickness = new Thickness(0);
                break;
            case "Critical":
                _border.Background = new SolidColorBrush(WithAlpha((Windows.UI.Color)resources["SystemFillColorCritical"], 0x1F));
                _label.Foreground = (Brush)resources["SystemFillColorCriticalBrush"];
                _border.BorderThickness = new Thickness(0);
                break;
            case "Success":
                _border.Background = new SolidColorBrush(WithAlpha((Windows.UI.Color)resources["SystemFillColorSuccess"], 0x1F));
                _label.Foreground = (Brush)resources["SystemFillColorSuccessBrush"];
                _border.BorderThickness = new Thickness(0);
                break;
            default:
                _border.Background = (Brush)resources["ControlFillColorDefaultBrush"];
                _border.BorderBrush = (Brush)resources["ControlStrokeColorDefaultBrush"];
                _border.BorderThickness = new Thickness(1);
                _label.Foreground = (Brush)resources["TextFillColorSecondaryBrush"];
                break;
        }
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) => Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
}

/// <summary>The count pill in a pivot: filled with the accent when non-zero, outlined when zero.</summary>
public sealed partial class CountPill : ContentControl
{
    private readonly Border _border = new() { CornerRadius = new CornerRadius(9), MinWidth = 18, Height = 18, Padding = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new() { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private int _count;

    public CountPill()
    {
        _border.Child = _label;
        Content = _border;
        Padding = new Thickness(0);
        IsTabStop = false;
        Apply();
    }

    public int Count
    {
        get => _count;
        set
        {
            _count = value;
            Apply();
        }
    }

    private void Apply()
    {
        var resources = Application.Current.Resources;
        _label.Text = _count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_count > 0)
        {
            _border.Background = (Brush)resources["AccentFillColorDefaultBrush"];
            _border.BorderThickness = new Thickness(0);
            _label.Foreground = (Brush)resources["TextOnAccentFillColorPrimaryBrush"];
        }
        else
        {
            _border.Background = (Brush)resources["ControlFillColorDefaultBrush"];
            _border.BorderBrush = (Brush)resources["ControlStrokeColorDefaultBrush"];
            _border.BorderThickness = new Thickness(1);
            _label.Foreground = (Brush)resources["TextFillColorTertiaryBrush"];
        }
    }
}

/// <summary>The app icon's double chevron, drawn as vector paths in the accent colour.</summary>
public sealed partial class ChevronGlyph : ContentControl
{
    public ChevronGlyph()
    {
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(Chevron(accent, 5, 12, 1.0));
        canvas.Children.Add(Chevron(accent, 5, 19, 0.5));
        var viewbox = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
        Content = viewbox;
        IsTabStop = false;
    }

    private static Polyline Chevron(Brush brush, double x, double y, double opacity) => new()
    {
        Points = [new Windows.Foundation.Point(x, y), new Windows.Foundation.Point(x + 7, y - 7), new Windows.Foundation.Point(x + 14, y)],
        Stroke = brush,
        StrokeThickness = 2.4,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Opacity = opacity,
    };
}
