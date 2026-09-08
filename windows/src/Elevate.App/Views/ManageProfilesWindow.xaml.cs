using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Elevate.App.Views;

/// <summary>One profile in the Profiles window's list.</summary>
public sealed class ProfileListItem(Guid id) : ObservableObject
{
    private string _name = string.Empty;
    private string _caption = string.Empty;
    private string _glyph = "";
    private string _starLabel = "Not pinned";
    private Brush? _starBrush;

    public Guid Id { get; } = id;

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public string Caption { get => _caption; set => SetProperty(ref _caption, value); }

    public string Glyph { get => _glyph; set => SetProperty(ref _glyph, value); }

    public string StarLabel { get => _starLabel; set => SetProperty(ref _starLabel, value); }

    public Brush? StarBrush { get => _starBrush; set => SetProperty(ref _starBrush, value); }

    public void Update(ActivationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Name = profile.Name;
        Caption = ProfileSummary.Caption(profile.Entries);
        Glyph = profile.Pinned ? "" : "";
        StarLabel = profile.Pinned ? "Pinned" : "Not pinned";
        StarBrush = (Brush)Application.Current.Resources[profile.Pinned ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"];
    }
}

/// <summary>
/// The Profiles window: every profile on the left, the selected one edited in place on the right.
/// Changes apply as they are made; Done only closes. Port of the macOS <c>ManageProfilesView</c>.
/// </summary>
public sealed partial class ManageProfilesWindow : Window
{
    private const double DurationColumn = 130;
    private const double RemoveColumn = 36;

    private readonly AppModel _model;
    private readonly ObservableCollection<ProfileListItem> _items = [];
    private Guid? _selected;
    private bool _filling;
    private string _rolesSignature = string.Empty;

    public ManageProfilesWindow(AppModel model)
    {
        InitializeComponent();
        _model = model;
        DialogWindows.Configure(this, "Profiles", 760, 480, Root);
        ProfileList.ItemsSource = _items;
        _selected = model.ProfileToEdit ?? model.Profiles.FirstOrDefault()?.Id;
        model.ProfileToEdit = null;
        FillList();
        ShowEditor();
        _model.Changed += OnModelChanged;
        Closed += (_, _) => _model.Changed -= OnModelChanged;
    }

    private ActivationProfile? Selected => _selected is { } id ? _model.Profile(id) : null;

    private IEnumerable<ActivationProfile> Filtered => _model.Profiles.Where(p => PanelFilter.Matches(Search.Text, p.Name));

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_model.ProfileToEdit is { } wanted)
        {
            _selected = wanted;
            _model.ProfileToEdit = null;
            Search.Text = string.Empty;
        }

        if (_selected is { } current && _model.Profile(current) is null)
        {
            _selected = _model.Profiles.FirstOrDefault()?.Id;
        }

        FillList();
        ShowEditor();
    }

    // MARK: The list

    /// <summary>Reconciles by id so the ListView keeps its elements, its selection and its scroll position.</summary>
    private void FillList()
    {
        _filling = true;
        try
        {
            var wanted = Filtered.ToList();
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                if (!wanted.Any(p => p.Id == _items[i].Id))
                {
                    _items.RemoveAt(i);
                }
            }

            for (var i = 0; i < wanted.Count; i++)
            {
                var profile = wanted[i];
                var at = -1;
                for (var j = i; j < _items.Count; j++)
                {
                    if (_items[j].Id == profile.Id)
                    {
                        at = j;
                        break;
                    }
                }

                if (at < 0)
                {
                    _items.Insert(i, new ProfileListItem(profile.Id));
                }
                else if (at != i)
                {
                    _items.Move(at, i);
                }

                _items[i].Update(profile);
            }

            // Reordering only makes sense over the whole list; while filtering the handles are off.
            var unfiltered = Search.Text.Trim().Length == 0;
            ProfileList.CanReorderItems = unfiltered;
            ProfileList.CanDragItems = unfiltered;
            ProfileList.SelectedItem = _items.FirstOrDefault(i => i.Id == _selected);
        }
        finally
        {
            _filling = false;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => FillList();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        CommitName();
        _selected = (ProfileList.SelectedItem as ProfileListItem)?.Id;
        ShowEditor();
    }

    private void OnReordered(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.Items.FirstOrDefault() is not ProfileListItem moved)
        {
            return;
        }

        var from = _model.Profiles.ToList().FindIndex(p => p.Id == moved.Id);
        var to = _items.IndexOf(moved);
        if (from < 0 || to < 0 || from == to)
        {
            return;
        }

        // SwiftUI move semantics: the destination is the slot before the move, so a move down skips itself.
        _model.MoveProfiles([from], to > from ? to + 1 : to);
    }

    private void OnNewProfile(object sender, RoutedEventArgs e)
    {
        CommitName();
        var profile = _model.NewProfile();
        Search.Text = string.Empty;
        _selected = profile.Id;
        FillList();
        ShowEditor();
        NameBox.Focus(FocusState.Programmatic);
        NameBox.SelectAll();
    }

    // MARK: The editor

    private void ShowEditor()
    {
        var profile = Selected;
        Editor.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyEditor.Visibility = profile is null ? Visibility.Visible : Visibility.Collapsed;
        if (profile is null)
        {
            EmptyTitle.Text = _model.Profiles.Count == 0 ? "No profiles yet" : "Select a profile";
            _rolesSignature = string.Empty;
            return;
        }

        _filling = true;
        try
        {
            // A rename in progress must not be thrown away by a model change.
            if (NameBox.FocusState == FocusState.Unfocused && !string.Equals(NameBox.Text, profile.Name, StringComparison.Ordinal))
            {
                NameBox.Text = profile.Name;
            }

            PinSwitch.IsOn = profile.Pinned;
            HotKeySwitch.IsOn = _model.Settings.HotKeyProfileId == profile.Id;
            HotKeyCaption.Text = _model.Settings.HotKey is { } key ? key.Display : "No shortcut recorded.";
            RunButton.IsEnabled = profile.Entries.Count > 0;
            var signature = profile.Id + "|" + string.Join(",", profile.Entries.Select(e => $"{e.RoleKey}:{e.LastDuration}"))
                + "|" + _model.Roles.Values.Sum(l => l.Count) + "|" + _model.Busy.Count;
            if (signature != _rolesSignature)
            {
                _rolesSignature = signature;
                BuildRoles(profile);
            }
        }
        finally
        {
            _filling = false;
        }
    }

    private void BuildRoles(ActivationProfile profile)
    {
        RoleGroups.Children.Clear();
        var resources = Application.Current.Resources;
        var secondary = (Brush)resources["TextFillColorSecondaryBrush"];
        if (profile.Entries.Count == 0)
        {
            RoleGroups.Children.Add(new TextBlock
            {
                Text = "No roles yet. \"Add roles…\" picks from every account and tenant.",
                FontSize = 12,
                Foreground = secondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4),
            });
            return;
        }

        var divider = (Brush)resources["DividerStrokeColorDefaultBrush"];
        var id = profile.Id;
        foreach (var tenantKey in profile.Entries.Select(e => e.RoleKey.TenantKey).Distinct())
        {
            var box = TenantGroupBox.Create(_model, tenantKey, out var rows);
            var first = true;
            foreach (var entry in profile.Entries.Where(e => e.RoleKey.TenantKey == tenantKey))
            {
                var key = entry.RoleKey;
                var role = _model.Role(key);
                var policy = role?.Policy ?? RolePolicy.ManualDefault;
                var grid = TenantGroupBox.RowGrid(DurationColumn, RemoveColumn);
                if (!first)
                {
                    grid.BorderBrush = divider;
                    grid.BorderThickness = new Thickness(0, 1, 0, 0);
                }

                first = false;
                grid.Children.Add(TenantGroupBox.NameCell(_model.SummaryName(key), Caption(key, role, policy), key));

                var picker = new DurationPicker { Maximum = policy.MaximumDuration, Duration = Proposed(entry, policy) };
                picker.VerticalAlignment = VerticalAlignment.Center;
                picker.HorizontalAlignment = HorizontalAlignment.Stretch;
                AutomationProperties.SetName(picker, $"Duration for {_model.SummaryName(key)}");
                picker.SelectionChanged += (_, _) =>
                {
                    if (!_filling)
                    {
                        _model.SetProfileEntryDuration(id, key, picker.Duration);
                    }
                };
                Grid.SetColumn(picker, 1);
                grid.Children.Add(picker);

                var remove = new Button
                {
                    Style = (Style)resources["SubtleButtonStyle"],
                    Width = 28,
                    Height = 28,
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = new FontIcon { Glyph = "", FontSize = 12 },
                };
                AutomationProperties.SetName(remove, $"Remove {_model.SummaryName(key)}");
                ToolTipService.SetToolTip(remove, "Remove from profile");
                remove.Click += (_, _) => _model.RemoveProfileEntry(id, key);
                Grid.SetColumn(remove, 2);
                grid.Children.Add(remove);
                rows.Children.Add(grid);
            }

            RoleGroups.Children.Add(box);
        }
    }

    /// <summary>"Entra · MFA", "Azure · rg-prod · resource group", "Group · not loaded".</summary>
    private static string Caption(RoleKey key, EligibleRole? role, RolePolicy policy)
    {
        var parts = new List<string>
        {
            key.Scope.Kind switch
            {
                RoleScopeKind.EntraDirectory => "Entra",
                RoleScopeKind.AzureResource => "Azure",
                _ => "Group",
            },
        };
        if (role?.Detail is { } detail)
        {
            parts.Add(detail);
        }

        if (PolicyNotes.Caption(policy) is { } notes)
        {
            parts.Add(notes);
        }

        if (role is null)
        {
            parts.Add("not loaded");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>What the next run would propose today: the entry's own duration, else memory, else the policy.</summary>
    private TimeSpan Proposed(ActivationProfile.Entry entry, RolePolicy policy)
    {
        var wanted = entry.LastDuration ?? _model.Remembered(entry.RoleKey)?.LastDuration ?? policy.DefaultDuration;
        return wanted < policy.MaximumDuration ? wanted : policy.MaximumDuration;
    }

    private void CommitName()
    {
        if (Selected is not { } profile)
        {
            return;
        }

        var trimmed = NameBox.Text.Trim();
        if (trimmed.Length == 0)
        {
            NameBox.Text = profile.Name;
        }
        else if (!string.Equals(trimmed, profile.Name, StringComparison.Ordinal))
        {
            _model.RenameProfile(profile.Id, trimmed);
        }
    }

    private void OnNameLostFocus(object sender, RoutedEventArgs e) => CommitName();

    private void OnNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitName();
            e.Handled = true;
        }
    }

    private void OnPinToggled(object sender, RoutedEventArgs e)
    {
        if (_filling || Selected is not { } profile)
        {
            return;
        }

        if (_model.SetPinned(profile.Id, PinSwitch.IsOn))
        {
            PinHint.Visibility = Visibility.Collapsed;
            return;
        }

        PinHint.Text = ProfileActions.PinRefusedHint;
        PinHint.Visibility = Visibility.Visible;
        _filling = true;
        PinSwitch.IsOn = false;
        _filling = false;
    }

    private void OnHotKeyToggled(object sender, RoutedEventArgs e)
    {
        if (_filling || Selected is not { } profile)
        {
            return;
        }

        _model.SetHotKeyProfile(HotKeySwitch.IsOn ? profile.Id : null);
    }

    private void OnSettings(object sender, RoutedEventArgs e) => App.Current.OpenSettings();

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile)
        {
            return;
        }

        CommitName();
        _ = ProfileActions.RunAsync(_model, profile.Id, silentlyIfPossible: false);
    }

    private async void OnAddRoles(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile)
        {
            return;
        }

        CommitName();
        var dialog = new AddRolesDialog(_model, profile) { XamlRoot = Root.XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && _model.Profile(profile.Id) is not null)
        {
            _model.AddProfileEntries(profile.Id, dialog.Picked);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is { } profile)
        {
            _ = ProfileActions.ConfirmDeleteAsync(_model, profile.Id, Root.XamlRoot);
        }
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        CommitName();
        Close();
    }
}
