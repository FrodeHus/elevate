using Elevate.App.Services;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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

/// <summary>
/// The Entra / Azure / Groups picker, styled like the macOS segmented control: one capsule, the
/// selected segment filled with the accent, the active count inside the label ("Entra 1").
/// </summary>
public sealed partial class SegmentedPivots : ContentControl
{
    private static readonly PanelTab[] Tabs = [PanelTab.Roles, PanelTab.Azure, PanelTab.Groups];

    private readonly ToggleButton[] _buttons = new ToggleButton[3];
    private readonly int[] _counts = new int[3];
    private PanelTab _selected = PanelTab.Roles;
    private bool _applying;

    public SegmentedPivots()
    {
        var resources = Application.Current.Resources;
        var grid = new Grid { ColumnSpacing = 2 };
        for (var i = 0; i < Tabs.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var button = new ToggleButton
            {
                Height = 30,
                MinHeight = 30,
                Padding = new Thickness(6, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Content = AppModel.Title(Tabs[i]),
            };
            var tab = Tabs[i];
            button.Click += (_, _) =>
            {
                if (_applying)
                {
                    return;
                }

                // A segment stays selected when clicked again; the picker always has one.
                Selected = tab;
                SelectionChanged?.Invoke(this, tab);
            };
            Grid.SetColumn(button, i);
            grid.Children.Add(button);
            _buttons[i] = button;
        }

        Content = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3),
            Background = (Brush)resources["ControlFillColorDefaultBrush"],
            BorderBrush = (Brush)resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        Padding = new Thickness(0);
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Apply();
    }

    /// <summary>Raised when the user picks a segment; not when <see cref="Selected"/> is set from code.</summary>
    public event EventHandler<PanelTab>? SelectionChanged;

    public PanelTab Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Apply();
        }
    }

    /// <summary>The active count shown inside the segment's label; hidden when zero, like the macOS picker.</summary>
    public void SetCount(PanelTab tab, int count)
    {
        _counts[Array.IndexOf(Tabs, tab)] = count;
        Apply();
    }

    private void Apply()
    {
        _applying = true;
        try
        {
            for (var i = 0; i < Tabs.Length; i++)
            {
                var name = AppModel.Title(Tabs[i]);
                _buttons[i].Content = _counts[i] > 0 ? $"{name} {_counts[i].ToString(System.Globalization.CultureInfo.InvariantCulture)}" : name;
                _buttons[i].IsChecked = Tabs[i] == _selected;
            }
        }
        finally
        {
            _applying = false;
        }
    }
}

/// <summary>
/// A boxed group of sheet rows that belong to one tenant: the tenant name leads in bold, the
/// account address is the caption, and a soft rounded fill ties the rows to their header so a
/// mixed-tenant window reads at a glance instead of by parsing "user · tenant" lines. Port of the
/// macOS <c>TenantGroup</c>; the activation and profile run windows both build their tables from it.
/// </summary>
public static class TenantGroupBox
{
    /// <summary>The box for one tenant; add the rows to <paramref name="rows"/>.</summary>
    public static Border Create(AppModel model, TenantKey tenantKey, out StackPanel rows)
    {
        ArgumentNullException.ThrowIfNull(model);
        var resources = Application.Current.Resources;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Padding = new Thickness(10, 6, 10, 4) };
        header.Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 12,
            Foreground = (Brush)resources["AccentFillColorDefaultBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = model.Tenant(tenantKey)?.DisplayName ?? tenantKey.TenantId,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock { Text = "·", Foreground = (Brush)resources["TextFillColorTertiaryBrush"], VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock
        {
            Text = model.Identity(tenantKey.IdentityId)?.Upn ?? tenantKey.IdentityId,
            FontSize = 12,
            Foreground = (Brush)resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        AutomationProperties.SetName(header, $"{model.Tenant(tenantKey)?.DisplayName ?? tenantKey.TenantId}, {model.Identity(tenantKey.IdentityId)?.Upn ?? tenantKey.IdentityId}");

        rows = new StackPanel();
        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(rows);
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = (Brush)resources["SubtleFillColorSecondaryBrush"],
            Padding = new Thickness(0, 0, 0, 2),
            Child = content,
        };
    }

    /// <summary>The fixed columns every tenant row shares: name (fills), duration, status. Rows line up even when a cell is empty.</summary>
    public static Grid RowGrid(double durationWidth, double statusWidth)
    {
        var grid = new Grid { Padding = new Thickness(10, 6, 10, 6), ColumnSpacing = 10, MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(durationWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(statusWidth) });
        return grid;
    }

    /// <summary>The role name with its scope caption underneath (an Azure role's "rg-prod · resource group"), the full ARM path as tooltip.</summary>
    public static StackPanel NameCell(string name, string? detail, RoleKey key, double opacity = 1)
    {
        ArgumentNullException.ThrowIfNull(key);
        var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Opacity = opacity };
        cell.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        if (detail is not null)
        {
            var caption = new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTipService.SetToolTip(caption, key.Scope is AzureResourceScope azure ? azure.Scope : detail);
            cell.Children.Add(caption);
        }

        return cell;
    }

    /// <summary>
    /// The status cell: an optional ring, then text that trims inside the fixed column. A Grid rather
    /// than a horizontal StackPanel, which would hand the text unbounded width and let it overflow.
    /// </summary>
    public static Grid StatusCell(ProgressRing? ring, TextBlock text, HorizontalAlignment alignment)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cell = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (ring is not null)
        {
            ring.VerticalAlignment = VerticalAlignment.Center;
            cell.Children.Add(ring);
        }

        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.HorizontalAlignment = alignment;
        Grid.SetColumn(text, 1);
        cell.Children.Add(text);
        return cell;
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
