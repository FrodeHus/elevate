using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Elevate.App.Views;

/// <summary>
/// The searchable list behind "All N" in the flyout: pinned profiles first, then the rest. Enter
/// runs the first match (Ctrl+Enter silently); the star pins or unpins in place. Port of the macOS
/// <c>AllProfilesPopover</c>.
/// </summary>
internal sealed class AllProfilesFlyout
{
    private readonly AppModel _model;
    private readonly Flyout _flyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly TextBox _search = new() { PlaceholderText = "Search profiles" };
    private readonly StackPanel _rows = new() { Spacing = 1 };
    private readonly TextBlock _hint = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private string? _pinHint;

    private AllProfilesFlyout(AppModel model)
    {
        _model = model;
        var resources = Application.Current.Resources;
        var content = new StackPanel { Width = 340, Spacing = 8 };
        _search.TextChanged += (_, _) => Fill();
        _search.KeyDown += OnSearchKey;
        content.Children.Add(_search);
        content.Children.Add(new ScrollViewer { MaxHeight = 320, Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var manage = new HyperlinkButton { Content = "Manage profiles…", Padding = new Thickness(4, 2, 4, 2) };
        manage.Click += (_, _) =>
        {
            _flyout.Hide();
            App.Current.OpenManageProfiles();
        };
        footer.Children.Add(manage);
        _hint.Foreground = (Brush)resources["TextFillColorSecondaryBrush"];
        _hint.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_hint, 1);
        footer.Children.Add(_hint);
        content.Children.Add(footer);
        _flyout.Content = content;
        _flyout.Opened += (_, _) =>
        {
            _model.Changed += OnModelChanged;
            _search.Focus(FocusState.Programmatic);
        };
        _flyout.Closed += (_, _) => _model.Changed -= OnModelChanged;
        Fill();
    }

    /// <summary>Opens the list under <paramref name="anchor"/>.</summary>
    public static void Show(AppModel model, FrameworkElement anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        new AllProfilesFlyout(model)._flyout.ShowAt(anchor);
    }

    private IEnumerable<ActivationProfile> Matching =>
        _model.Profiles.Where(p => PanelFilter.Matches(_search.Text, p.Name));

    private void OnModelChanged(object? sender, EventArgs e) => Fill();

    private void Fill()
    {
        _rows.Children.Clear();
        var resources = Application.Current.Resources;
        var matching = Matching.ToList();
        if (matching.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "No matches",
                FontSize = 12,
                Foreground = (Brush)resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(10, 8, 10, 8),
            });
        }

        var pinned = matching.Where(p => p.Pinned).ToList();
        var others = matching.Where(p => !p.Pinned).ToList();
        if (pinned.Count > 0)
        {
            _rows.Children.Add(Section("Pinned"));
            foreach (var p in pinned)
            {
                _rows.Children.Add(Row(p));
            }
        }

        if (others.Count > 0)
        {
            _rows.Children.Add(Section(pinned.Count == 0 ? "Profiles" : "All profiles"));
            foreach (var p in others)
            {
                _rows.Children.Add(Row(p));
            }
        }

        _hint.Text = _pinHint ?? "Enter runs the first match";
        _hint.Foreground = (Brush)resources[_pinHint is null ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush"];
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        Margin = new Thickness(8, 6, 8, 2),
    };

    private Grid Row(ActivationProfile profile)
    {
        var resources = Application.Current.Resources;
        var id = profile.Id;
        var row = new Grid { ColumnSpacing = 8, MinHeight = 36, Padding = new Thickness(6, 2, 4, 2), CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var star = new Button
        {
            Style = (Style)resources["SubtleButtonStyle"],
            Width = 28,
            Height = 28,
            Content = new FontIcon
            {
                Glyph = profile.Pinned ? "" : "",
                FontSize = 12,
                Foreground = (Brush)resources[profile.Pinned ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"],
            },
        };
        var starName = profile.Pinned ? $"Unpin {profile.Name}" : $"Pin {profile.Name}";
        AutomationProperties.SetName(star, starName);
        ToolTipService.SetToolTip(star, profile.Pinned ? "Unpin from the flyout" : "Pin to the flyout");
        star.Click += (_, _) =>
        {
            _pinHint = _model.SetPinned(id, !profile.Pinned) ? null : ProfileActions.PinRefusedHint;
            Fill();
        };
        row.Children.Add(star);

        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = profile.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        text.Children.Add(new TextBlock
        {
            Text = ProfileSummary.Caption(profile.Entries),
            FontSize = 12,
            Foreground = (Brush)resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var active = profile.Entries.Count(e => _model.Assignment(e.RoleKey)?.Status.Kind == AssignmentStatusKind.Active);
        if (active > 0)
        {
            var activeText = new TextBlock
            {
                Text = $"{active} active",
                FontSize = 12,
                Foreground = (Brush)resources["SystemFillColorSuccessBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(activeText, 2);
            row.Children.Add(activeText);
        }

        var run = new Button { Content = "Run", Style = (Style)resources["SmallButtonStyle"] };
        ToolTipService.SetToolTip(run, $"Run {profile.Name}. Ctrl-click to run with the last reason and durations");
        run.Click += (_, _) => Run(id);
        Grid.SetColumn(run, 3);
        row.Children.Add(run);

        var more = new Button
        {
            Style = (Style)resources["SubtleButtonStyle"],
            Width = 28,
            Height = 28,
            Content = new FontIcon { Glyph = "", FontSize = 12 },
        };
        AutomationProperties.SetName(more, $"More actions for {profile.Name}");
        more.Click += (_, _) => ProfileActions.Menu(_model, id, more.XamlRoot).ShowAt(more);
        Grid.SetColumn(more, 4);
        row.Children.Add(more);

        row.Tapped += (_, _) => Run(id);
        row.ContextRequested += (s, e) =>
        {
            e.Handled = true;
            ProfileActions.Menu(_model, id, ((Grid)s).XamlRoot).ShowAt((Grid)s);
        };
        row.PointerEntered += (s, _) => ((Grid)s).Background = (Brush)resources["SubtleFillColorSecondaryBrush"];
        row.PointerExited += (s, _) => ((Grid)s).Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        return row;
    }

    private void Run(Guid id)
    {
        _flyout.Hide();
        _ = ProfileActions.RunAsync(_model, id, silentlyIfPossible: PanelView.IsControlDown());
    }

    private void OnSearchKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter when Matching.FirstOrDefault() is { } first:
                e.Handled = true;
                Run(first.Id);
                break;
            case Windows.System.VirtualKey.Escape when _search.Text.Length > 0:
                e.Handled = true;
                _search.Text = string.Empty;
                break;
        }
    }
}
