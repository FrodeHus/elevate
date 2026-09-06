using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Elevate.App.Views;

/// <summary>Rename, reorder, run, edit and delete profiles. Port of the macOS <c>ManageProfilesView</c>.</summary>
public sealed partial class ManageProfilesWindow : Window
{
    private readonly AppModel _model;
    private readonly Dictionary<Guid, TextBox> _names = [];

    public ManageProfilesWindow(AppModel model)
    {
        InitializeComponent();
        _model = model;
        DialogWindows.Configure(this, "Profiles", 560, 420, Root, autoHeight: true);
        DialogWindows.DefaultButton(Root, DoneButton);
        Build();
        _model.Changed += OnModelChanged;
        Closed += (_, _) => _model.Changed -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        // Rebuild only when the set or order changed; a rename in progress must not be thrown away.
        var current = _model.Profiles.Select(p => p.Id).ToList();
        if (!current.SequenceEqual(_names.Keys))
        {
            CommitAll();
            Build();
        }
    }

    private void Build()
    {
        Rows.Children.Clear();
        _names.Clear();
        var profiles = _model.Profiles;
        Empty.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListBorder.Visibility = profiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var divider = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            var id = profile.Id;
            var row = new Grid { Padding = new Thickness(10, 6, 10, 6), ColumnSpacing = 8, MinHeight = 44, BorderBrush = divider, BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var order = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            order.Children.Add(IconButton("", "Move up", i > 0, () => _model.MoveProfiles([Index(id)], Index(id) - 1)));
            order.Children.Add(IconButton("", "Move down", i < profiles.Count - 1, () => _model.MoveProfiles([Index(id)], Index(id) + 2)));
            row.Children.Add(order);

            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            var box = new TextBox { Text = profile.Name, PlaceholderText = "Name", BorderThickness = new Thickness(0), Background = null };
            box.LostFocus += (_, _) => Commit(id);
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    Commit(id);
                    e.Handled = true;
                }
            };
            _names[id] = box;
            name.Children.Add(box);
            name.Children.Add(new TextBlock { Text = ProfileSummary.Caption(profile.Entries), FontSize = 12, Foreground = secondary, Margin = new Thickness(11, 0, 0, 0) });
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var run = new Button { Content = "Run", Style = (Style)Application.Current.Resources["SmallButtonStyle"] };
            run.Click += (_, _) =>
            {
                CommitAll();
                _model.RequestRun(id);
                App.Current.OpenRunProfile(id);
            };
            var edit = new Button { Content = "Edit", Style = (Style)Application.Current.Resources["SmallButtonStyle"] };
            ToolTipService.SetToolTip(edit, "Reopens the selection in the flyout; use \"Update profile\" when done");
            edit.Click += (_, _) =>
            {
                CommitAll();
                _model.BeginEditing(id);
                EditingHint.Text = $"\"{_model.Profile(id)?.Name ?? profile.Name}\" is loaded into the flyout's selection. Open the Elevate flyout, adjust the ticks across the Entra, Azure and Groups pivots, then press Update profile.";
                EditingHint.Visibility = Visibility.Visible;
            };
            actions.Children.Add(run);
            actions.Children.Add(edit);
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);

            var delete = IconButton("", $"Delete {profile.Name}", true, () => _model.DeleteProfile(id));
            Grid.SetColumn(delete, 3);
            row.Children.Add(delete);
            Rows.Children.Add(row);
        }
    }

    private int Index(Guid id)
    {
        var profiles = _model.Profiles;
        for (var i = 0; i < profiles.Count; i++)
        {
            if (profiles[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static Button IconButton(string glyph, string name, bool enabled, Action action)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Width = 28,
            Height = 28,
            IsEnabled = enabled,
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    private void Commit(Guid id)
    {
        if (_names.TryGetValue(id, out var box))
        {
            _model.RenameProfile(id, box.Text);
        }
    }

    private void CommitAll()
    {
        foreach (var id in _names.Keys.ToList())
        {
            Commit(id);
        }
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        CommitAll();
        Close();
    }
}
