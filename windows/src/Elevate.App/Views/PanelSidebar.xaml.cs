using System.Text;
using Elevate.App.Services;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Elevate.App.Views;

/// <summary>
/// The 136 px column beside the main view: pinned profiles on top (fixed), every account with its
/// tenants in the middle (scrolls) and Add account… at the bottom (fixed). Rebuilt only when what
/// it shows changes. Port of the macOS <c>PanelSidebar</c>.
/// </summary>
public sealed partial class PanelSidebar : UserControl
{
    private string _signature = string.Empty;

    public PanelSidebar() => InitializeComponent();

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Redraws from the model when anything the sidebar shows has changed.</summary>
    public void Draw(AppModel model)
    {
        // The theme too: the rows read theme brushes when they are built.
        var signature = Signature(model) + ActualTheme;
        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        var showAccounts = model.IsConfigured && model.Identities.Count > 0;
        var offset = TenantsScroll.VerticalOffset;
        DrawProfiles(model, showAccounts);
        TenantsPanel.Children.Clear();
        if (showAccounts)
        {
            DrawTenants(model);
        }

        FooterPanel.Children.Clear();
        FooterPanel.Children.Add(new Border { Height = 1, Margin = new Thickness(2, 0, 2, 0), Background = Res("DividerStrokeColorDefaultBrush") });
        var add = Row(selected: false, height: 28, "Add account", () => App.Current.OpenAddAccount());
        add.Margin = new Thickness(0, 6, 0, 6);
        add.Content = Line(Icon("", 11, "TextFillColorSecondaryBrush"), Label("Add account…", "TextFillColorSecondaryBrush", semibold: false), null);
        FooterPanel.Children.Add(add);
        // The rebuilt content is measured now so the scroll position can be put back.
        TenantsScroll.UpdateLayout();
        TenantsScroll.ChangeView(null, offset, null, disableAnimation: true);
    }

    // MARK: Signature

    /// <summary>Everything a row shows or does, so an unrelated model change redraws nothing.</summary>
    private static string Signature(AppModel model)
    {
        var sb = new StringBuilder();
        sb.Append(model.IsConfigured).Append('#').Append(model.PanelSelection).Append('#').Append(model.TotalActiveCount)
            .Append('#').Append(model.ProfilesCollapsed).Append(model.TenantsCollapsed).Append(model.TenantsNeedAttention);
        var hotKey = model.Settings.HotKeyProfileId is { } hk && model.Settings.HotKey is { } binding ? $"{hk}:{binding.Display}" : string.Empty;
        sb.Append("#hk:").Append(hotKey).Append("#profiles:").Append(model.Profiles.Count);
        foreach (var p in model.PinnedProfiles)
        {
            sb.Append('|').Append(p.Id).Append(':').Append(p.Name).Append(':').Append(p.Entries.Count).Append(':').Append(ProfileSummary.Caption(p.Entries));
        }

        foreach (var identity in model.Identities)
        {
            sb.Append("#i:").Append(identity.Id).Append(':').Append(identity.Upn)
                .Append(':').Append(model.CollapsedIdentities.Contains(identity.Id))
                .Append(':').Append(model.NeedsSignIn(identity.Id))
                .Append(':').Append(identity.SignInMethod)
                .Append(':').Append(model.IsMethodAllowed(identity.SignInMethod))
                .Append(':').Append(model.SignInInFlight.Contains(identity.Id));
            foreach (var tenant in model.TenantsFor(identity.Id))
            {
                var mark = model.SidebarMark(tenant.Key);
                sb.Append("|t:").Append(tenant.Key).Append(':').Append(tenant.DisplayName).Append(':').Append(mark.Kind).Append(mark.Count)
                    .Append(':').Append(TenantTip(model, tenant));
            }
        }

        return sb.ToString();
    }

    // MARK: Profiles

    private void DrawProfiles(AppModel model, bool showAccounts)
    {
        ProfilesPanel.Children.Clear();
        var profiles = model.Profiles;
        if (!showAccounts || profiles.Count == 0)
        {
            ProfilesPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ProfilesPanel.Visibility = Visibility.Visible;
        var selection = model.PanelSelection;
        var pinned = model.PinnedProfiles;
        var pinnedSelected = pinned.Any(p => selection is PanelSelection.Profile s && s.Id == p.Id);
        var allSelected = !pinnedSelected && selection is PanelSelection.Profile;

        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var collapsed = model.ProfilesCollapsed;
        heading.Children.Add(SectionHeader("Profiles", collapsed, collapsed ? Secondary(profiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null,
            collapsed ? $"{profiles.Count} profiles" : null, model.ToggleProfilesSection));
        if (!collapsed)
        {
            var all = new Button
            {
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                Height = 24,
                MinHeight = 24,
                Padding = new Thickness(7, 0, 7, 0),
                CornerRadius = new CornerRadius(12),
                Content = new TextBlock { Text = $"All {profiles.Count}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
            };
            if (allSelected)
            {
                Fill(all, "AccentFillColorDefaultBrush", OnAccent);
            }
            else
            {
                all.Foreground = Res("TextFillColorSecondaryBrush");
            }

            ToolTipService.SetToolTip(all, "All profiles: search, run, pin and manage");
            AutomationProperties.SetName(all, $"All profiles, {profiles.Count}{(allSelected ? ", selected" : string.Empty)}");
            all.Click += (_, _) => AllProfilesFlyout.Show(model, all);
            Grid.SetColumn(all, 1);
            heading.Children.Add(all);
        }

        ProfilesPanel.Children.Add(heading);

        if (!collapsed)
        {
            foreach (var profile in pinned)
            {
                ProfilesPanel.Children.Add(ProfileRow(model, profile, selection is PanelSelection.Profile s && s.Id == profile.Id));
            }
        }

        ProfilesPanel.Children.Add(new Border { Height = 1, Margin = new Thickness(2, 6, 2, 0), Background = Res("DividerStrokeColorDefaultBrush") });
    }

    private Button ProfileRow(AppModel model, ActivationProfile profile, bool selected)
    {
        var id = profile.Id;
        var caption = ProfileSummary.Caption(profile.Entries);
        var button = Row(selected, height: 30, $"{profile.Name}, {caption}", () => _ = ProfileActions.RunAsync(model, id, PanelView.IsControlDown(), inPanel: true));
        var fg = selected ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush";
        // A one-entry profile is a favorite: the star; otherwise the bolt.
        var icon = Icon(profile.Entries.Count == 1 ? "" : "", 11, selected ? fg : "TextFillColorSecondaryBrush");
        button.Content = Line(icon, Label(profile.Name, fg, semibold: true), null);
        var tip = $"{profile.Name} ({caption})";
        if (model.Settings.HotKeyProfileId == id && model.Settings.HotKey is { } hotKey)
        {
            tip += " · " + hotKey.Display;
        }

        ToolTipService.SetToolTip(button, tip + "\nClick to open. Ctrl-click to run with the last reason and durations");
        button.ContextRequested += (s, e) =>
        {
            e.Handled = true;
            ProfileActions.Menu(model, id, XamlRoot, inPanel: true).ShowAt((FrameworkElement)s);
        };
        return button;
    }

    // MARK: Tenants

    private void DrawTenants(AppModel model)
    {
        var selection = model.PanelSelection;
        var total = model.TotalActiveCount;
        var collapsed = model.TenantsCollapsed;
        FrameworkElement? summary = null;
        if (collapsed)
        {
            var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (total > 0)
            {
                marks.Children.Add(Count(total, onAccent: false));
            }

            if (model.TenantsNeedAttention)
            {
                marks.Children.Add(Icon("\uE7BA", 12, "SystemFillColorCriticalBrush"));
            }

            summary = marks;
        }

        TenantsPanel.Children.Add(SectionHeader("Tenants", collapsed, summary,
            collapsed ? $"{total} active{(model.TenantsNeedAttention ? ", needs attention" : string.Empty)}" : null, model.ToggleTenantsSection));
        if (collapsed)
        {
            return;
        }

        var allSelected = selection is PanelSelection.AllTenants;
        var allTenants = Row(allSelected, height: 28,
            total > 0 ? $"All tenants, {total} active" : "All tenants", () => model.Select(new PanelSelection.AllTenants()));
        allTenants.Content = Line(null, Label("All tenants", allSelected ? OnAccent : "TextFillColorPrimaryBrush", semibold: true), total > 0 ? Count(total, allSelected) : null);
        TenantsPanel.Children.Add(allTenants);

        foreach (var identity in model.Identities)
        {
            DrawIdentity(model, identity, selection);
        }
    }

    private void DrawIdentity(AppModel model, Identity identity, PanelSelection selection)
    {
        var id = identity.Id;
        var collapsed = model.CollapsedIdentities.Contains(id);
        var needsSignIn = model.NeedsSignIn(id);
        var tenants = model.TenantsFor(id);

        // The address toggles the account's tenants; the menu stays beside it, also for an account
        // that needs to sign in, which could not be signed out or removed without it.
        var header = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var toggle = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Height = 24,
            MinHeight = 24,
            Padding = new Thickness(6, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        var chevron = Icon(collapsed ? "" : "", 8, "TextFillColorSecondaryBrush");
        var upn = new TextBlock
        {
            Text = identity.Upn,
            FontSize = 12,
            Foreground = Res("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new Grid { ColumnSpacing = 4 };
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        label.Children.Add(chevron);
        Grid.SetColumn(upn, 1);
        label.Children.Add(upn);
        toggle.Content = label;
        ToolTipService.SetToolTip(toggle, $"{identity.Upn}, signed in with {identity.SignInMethod.DetailedName}");
        AutomationProperties.SetName(toggle, collapsed ? $"Expand account {identity.Upn}" : $"Collapse account {identity.Upn}");
        toggle.Click += (_, _) => model.ToggleIdentity(id);
        header.Children.Add(toggle);

        var sole = tenants.Count == 1 ? tenants[0] : null;
        var menu = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Width = 24,
            Height = 24,
            MinHeight = 24,
            Content = Icon("", 12, "TextFillColorSecondaryBrush"),
        };
        AutomationProperties.SetName(menu, $"Account menu for {identity.Upn}");
        menu.Click += (_, _) => PanelMenus.IdentityMenu(model, identity, sole, XamlRoot).ShowAt(menu);
        Grid.SetColumn(menu, 1);
        header.Children.Add(menu);
        TenantsPanel.Children.Add(header);

        if (needsSignIn)
        {
            TenantsPanel.Children.Add(SignInRow(model, identity));
        }

        if (collapsed)
        {
            return;
        }

        foreach (var tenant in tenants)
        {
            var row = TenantRow(model, tenant, selection is PanelSelection.Tenant t && t.Key == tenant.Key);
            if (needsSignIn)
            {
                row.IsEnabled = false;
                row.Opacity = 0.5;
            }

            TenantsPanel.Children.Add(row);
        }
    }

    private static Button SignInRow(AppModel model, Identity identity)
    {
        var allowed = model.IsMethodAllowed(identity.SignInMethod);
        var signingIn = model.SignInInFlight.Contains(identity.Id);
        var button = Row(false, height: 28, $"Sign in, {identity.Upn}", () => PanelMenus.RetrySignIn(model, identity.Id));
        Fill(button, "SystemFillColorCautionBackgroundBrush", "TextFillColorPrimaryBrush");
        FrameworkElement icon = signingIn
            ? new ProgressRing { Width = 12, Height = 12, IsActive = true }
            : Icon("", 12, "ElevateWarningBrush");
        button.Content = Line(icon, Label("Sign in", "TextFillColorPrimaryBrush", semibold: true), null);
        button.IsEnabled = allowed && !signingIn;
        ToolTipService.SetToolTip(button, allowed ? $"Sign in again as {identity.Upn}" : AppModel.DisallowedMethodCaption);
        return button;
    }

    private static string TenantTip(AppModel model, TenantContext tenant)
    {
        var lines = new List<string> { tenant.DisplayName };
        if (tenant.DiscoveryMode == DiscoveryMode.ManualRoles)
        {
            lines.Add("Roles listed by hand");
        }

        if (model.EntraViewOnlyReason(tenant.Key) is { } viewOnly)
        {
            lines.Add(viewOnly);
        }

        if (tenant.AzureUnavailableReason is { } azure)
        {
            lines.Add(azure);
        }

        if (tenant.GroupsUnavailableReason is { } groups)
        {
            lines.Add(groups);
        }

        if ((model.TenantErrors.GetValueOrDefault(tenant.Key) ?? tenant.LastDiscoveryError) is { } error)
        {
            lines.Add("Failed: " + error);
        }

        return string.Join('\n', lines);
    }

    private static Button TenantRow(AppModel model, TenantContext tenant, bool selected)
    {
        var key = tenant.Key;
        var mark = model.SidebarMark(key);
        var state = mark.Kind switch
        {
            TenantMarkKind.Active => $", {mark.Count} active",
            TenantMarkKind.Error => ", needs attention",
            _ => string.Empty,
        };
        var button = Row(selected, height: 28, tenant.DisplayName + state, () => model.Select(new PanelSelection.Tenant(key)));
        FrameworkElement? trailing = mark.Kind switch
        {
            TenantMarkKind.Active => Count(mark.Count, selected),
            TenantMarkKind.Error => Icon("\uE7BA", 12, selected ? OnAccent : "SystemFillColorCriticalBrush"),
            _ => null,
        };
        button.Content = Line(null, Label(tenant.DisplayName, selected ? OnAccent : "TextFillColorPrimaryBrush", semibold: false), trailing);
        ToolTipService.SetToolTip(button, TenantTip(model, tenant));
        return button;
    }

    // MARK: Pieces

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Res("TextFillColorSecondaryBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A section header that collapses its section: chevron, caption, and a summary while collapsed.</summary>
    private static Button SectionHeader(string title, bool collapsed, FrameworkElement? summary, string? summaryName, Action toggle)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Height = 24,
            MinHeight = 24,
            Padding = new Thickness(6, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Icon(collapsed ? "\uE76C" : "\uE70D", 8, "TextFillColorSecondaryBrush"));
        var caption = new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Res("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(caption, 1);
        grid.Children.Add(caption);
        if (summary is not null)
        {
            Grid.SetColumn(summary, 2);
            grid.Children.Add(summary);
        }

        button.Content = grid;
        AutomationProperties.SetName(button, $"{title}, {(collapsed ? "collapsed" : "expanded")}" + (summaryName is null ? string.Empty : ", " + summaryName));
        button.Click += (_, _) => toggle();
        return button;
    }

    private static FontIcon Icon(string glyph, double size, string brush) => new()
    {
        Glyph = glyph,
        FontSize = size,
        Foreground = Res(brush),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Label(string text, string brush, bool semibold) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = semibold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = Res(brush),
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private const string OnAccent = "TextOnAccentFillColorPrimaryBrush";

    private static TextBlock Count(int n, bool onAccent) => new()
    {
        Text = n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FontSize = 12,
        Foreground = Res(onAccent ? OnAccent : "SystemFillColorSuccessBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Icon (12 px column), label (takes the rest and trims) and a trailing mark.</summary>
    private static Grid Line(FrameworkElement? icon, TextBlock label, FrameworkElement? trailing)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = icon is null ? new GridLength(0) : new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (icon is not null)
        {
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Children.Add(icon);
        }

        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }

        return grid;
    }

    /// <summary>A full-width sidebar row: a flat button of fixed height, its state in the automation name.</summary>
    private static Button Row(bool selected, double height, string automationName, Action click)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Height = height,
            MinHeight = height,
            Padding = new Thickness(8, 0, 6, 0),
            CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        if (selected)
        {
            Fill(button, "AccentFillColorDefaultBrush", OnAccent);
        }

        AutomationProperties.SetName(button, selected ? automationName + ", selected" : automationName);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Pins a button's fill in every visual state, so hovering a selected row does not swap it for the hover grey.</summary>
    private static void Fill(Button button, string background, string foreground)
    {
        var fill = Res(background);
        var text = Res(foreground);
        // SubtleButtonStyle sets a transparent Background itself, which hides the resource override at rest.
        button.Background = fill;
        button.Foreground = text;
        foreach (var name in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed" })
        {
            button.Resources[name] = fill;
        }

        foreach (var name in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed" })
        {
            button.Resources[name] = text;
        }
    }
}
