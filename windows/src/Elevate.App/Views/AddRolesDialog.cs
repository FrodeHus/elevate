using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Elevate.App.Views;

/// <summary>
/// Picks eligible roles from every account and tenant to add to a profile. Roles the profile
/// already holds are listed but disabled, so a search that finds nothing new says why. Port of
/// the macOS <c>AddRolesPicker</c>.
/// </summary>
internal sealed partial class AddRolesDialog : ContentDialog
{
    private readonly AppModel _model;
    private readonly HashSet<RoleKey> _inProfile;
    private readonly HashSet<RoleKey> _picked = [];
    private readonly TextBox _search = new() { PlaceholderText = "Search roles" };
    private readonly StackPanel _groups = new() { Spacing = 8 };
    private readonly SelectorBar _kinds = new();
    private RoleScopeKind? _kind;

    public AddRolesDialog(AppModel model, ActivationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _model = model;
        _inProfile = profile.Entries.Select(e => e.RoleKey).ToHashSet();
        Title = "Add roles";
        PrimaryButtonText = "Add";
        CloseButtonText = "Cancel";
        IsPrimaryButtonEnabled = false;
        DefaultButton = ContentDialogButton.Primary;

        var content = new StackPanel { Spacing = 8, Width = 440 };
        _search.TextChanged += (_, _) => Fill();
        content.Children.Add(_search);
        _kinds.Items.Add(new SelectorBarItem { Text = "All", Tag = null });
        _kinds.Items.Add(new SelectorBarItem { Text = "Entra", Tag = RoleScopeKind.EntraDirectory });
        _kinds.Items.Add(new SelectorBarItem { Text = "Azure", Tag = RoleScopeKind.AzureResource });
        _kinds.Items.Add(new SelectorBarItem { Text = "Groups", Tag = RoleScopeKind.Group });
        _kinds.SelectedItem = _kinds.Items[0];
        _kinds.SelectionChanged += (s, _) =>
        {
            _kind = s.SelectedItem?.Tag as RoleScopeKind?;
            Fill();
        };
        content.Children.Add(_kinds);
        content.Children.Add(new ScrollViewer { MaxHeight = 320, Content = _groups, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        content.Children.Add(new TextBlock
        {
            Text = "Eligible roles across all accounts; tenants still loading are marked.",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Content = content;
        Fill();
    }

    /// <summary>The keys ticked when the dialog closed with Add.</summary>
    public IReadOnlyList<RoleKey> Picked => [.. _picked];

    private void Fill()
    {
        _groups.Children.Clear();
        var resources = Application.Current.Resources;
        var secondary = (Brush)resources["TextFillColorSecondaryBrush"];
        var divider = (Brush)resources["DividerStrokeColorDefaultBrush"];
        var any = false;
        foreach (var identity in _model.Identities)
        {
            foreach (var tenant in _model.TenantsFor(identity.Id))
            {
                var roles = _model.RolesFor(tenant.Key)
                    .Where(r => _kind is null || r.Key.Scope.Kind == _kind)
                    .Where(r => PanelFilter.Matches(_search.Text, r, tenant.DisplayName, identity.Upn))
                    .OrderBy(r => r.Key.Scope.Kind)
                    .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                var busy = _model.Busy.Contains(tenant.Key);
                if (roles.Count == 0 && !busy)
                {
                    continue;
                }

                any = true;
                var box = TenantGroupBox.Create(_model, tenant.Key, out var rows);
                if (busy)
                {
                    var loading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Padding = new Thickness(10, 4, 10, 6) };
                    loading.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true });
                    loading.Children.Add(new TextBlock { Text = "loading…", FontSize = 12, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center });
                    rows.Children.Add(loading);
                }

                var first = true;
                foreach (var role in roles)
                {
                    var row = Row(role);
                    if (!first)
                    {
                        row.BorderBrush = divider;
                        row.BorderThickness = new Thickness(0, 1, 0, 0);
                    }

                    first = false;
                    rows.Children.Add(row);
                }

                _groups.Children.Add(box);
            }
        }

        if (!any)
        {
            _groups.Children.Add(new TextBlock
            {
                Text = _search.Text.Trim().Length == 0 ? "No eligible roles loaded yet. Refresh the flyout and try again." : "No matches",
                FontSize = 12,
                Foreground = secondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4),
            });
        }

        UpdateButton();
    }

    private Grid Row(EligibleRole role)
    {
        var resources = Application.Current.Resources;
        var key = role.Key;
        var already = _inProfile.Contains(key);
        var viewOnly = key.Scope.Kind == RoleScopeKind.EntraDirectory ? _model.EntraViewOnlyReason(key.TenantKey) : null;
        var grid = new Grid { Padding = new Thickness(10, 4, 10, 4), ColumnSpacing = 10, MinHeight = 36, Opacity = already ? 0.6 : 1 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = role.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis });
        if (role.Detail is { } detail)
        {
            label.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Foreground = (Brush)resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        var check = new CheckBox
        {
            Content = label,
            IsChecked = already || _picked.Contains(key),
            IsEnabled = !already && viewOnly is null,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, role.DisplayName);
        if (viewOnly is not null)
        {
            ToolTipService.SetToolTip(check, viewOnly);
        }

        check.Checked += (_, _) =>
        {
            _picked.Add(key);
            UpdateButton();
        };
        check.Unchecked += (_, _) =>
        {
            _picked.Remove(key);
            UpdateButton();
        };
        grid.Children.Add(check);

        var trailing = new TextBlock
        {
            Text = already ? "in profile" : Trailing(role),
            FontSize = 11,
            Foreground = (Brush)resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(trailing, 1);
        grid.Children.Add(trailing);
        return grid;
    }

    private static string Trailing(EligibleRole role)
    {
        var kind = role.Key.Scope.Kind switch
        {
            RoleScopeKind.EntraDirectory => "Entra",
            RoleScopeKind.AzureResource => "Azure",
            _ => "Group",
        };
        return PolicyNotes.Caption(role.Policy) is { } notes ? $"{kind} · {notes}" : kind;
    }

    private void UpdateButton()
    {
        PrimaryButtonText = $"Add {_picked.Count}";
        IsPrimaryButtonEnabled = _picked.Count > 0;
    }
}
