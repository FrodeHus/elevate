using System.Collections.ObjectModel;
using Elevate.App.Services;
using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace Elevate.App.Views;

/// <summary>
/// The flyout's content: header, pivots, search, notices, the grouped list (pinned "Active now",
/// account rows, tenant headers, role rows), the bulk bar and the footer. Redrawn from the model on
/// every change by reconciling the row objects, so the list keeps its scroll position.
/// </summary>
public sealed partial class PanelView : UserControl
{
    private readonly ObservableCollection<PanelGroup> _groups = [];
    private readonly DispatcherQueueTimer _clock;
    private AppModel? _model;
    private FlyoutWindow? _window;
    private bool _syncingPivot;
    private bool _searchOpen;

    public PanelView()
    {
        InitializeComponent();
        GroupedSource.Source = _groups;
        QuickStartShared.Content = SharedAppConsent.QuickStartLabel;
        Accelerate(SearchToggle, Windows.System.VirtualKey.F, Windows.System.VirtualKeyModifiers.Control, "Filter roles and groups (Ctrl+F)", () =>
        {
            SearchToggle.IsChecked = SearchToggle.IsChecked != true;
            OnSearchToggle(SearchToggle, new RoutedEventArgs());
        });
        Accelerate(RefreshButton, Windows.System.VirtualKey.F5, Windows.System.VirtualKeyModifiers.None, "Refresh (F5)", () => OnRefresh(RefreshButton, new RoutedEventArgs()));
        // The comma has no VirtualKey member, and a KeyboardAccelerator refuses a raw key code (the
        // 1.2.8 startup crash), so Ctrl+, is read off the key event instead.
        ToolTipService.SetToolTip(SettingsButton, "Settings (Ctrl+,)");
        PreviewKeyDown += OnPreviewKeyDown;
        _clock = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => Tick();
        ApplyMotionSetting();
        var queue = DispatcherQueue;
        _uiSettings.AnimationsEnabledChanged += (_, _) => queue.TryEnqueue(ApplyMotionSetting);
    }

    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly TransitionCollection _listTransitions = [];

    /// <summary>
    /// Reduce Motion: with animations off in Settings > Accessibility, rows appear and disappear
    /// without the slide when a group collapses or a refresh reorders the list.
    /// </summary>
    private void ApplyMotionSetting()
    {
        if (_uiSettings.AnimationsEnabled)
        {
            if (_listTransitions.Count == 0)
            {
                _listTransitions.Add(new AddDeleteThemeTransition());
                _listTransitions.Add(new ReorderThemeTransition());
            }

            List.ItemContainerTransitions = _listTransitions;
        }
        else
        {
            List.ItemContainerTransitions = [];
        }
    }

    public void Bind(AppModel model, FlyoutWindow window)
    {
        _model = model;
        _window = window;
        model.Changed += (_, _) => Refresh();
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                _clock.Stop();
            }
            else
            {
                _clock.Start();
            }
        };
        Refresh();
    }

    /// <summary>A window-wide shortcut for a flyout control, named in the control's tooltip (plain buttons show no accelerator text of their own).</summary>
    private static void Accelerate(Control element, Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, string tooltip, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            if (!element.IsEnabled)
            {
                return;
            }

            action();
            e.Handled = true;
        };
        element.KeyboardAccelerators.Add(accelerator);
        ToolTipService.SetToolTip(element, tooltip);
    }

    /// <summary>VK_OEM_COMMA: the comma key, which <see cref="Windows.System.VirtualKey"/> does not name.</summary>
    private const int CommaKey = 0xBC;

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if ((int)e.Key == CommaKey && IsControlDown())
        {
            App.Current.OpenSettings();
            e.Handled = true;
        }
    }

    // MARK: Drawing

    /// <summary>Redraws everything from the model. Cheap enough to run on every change.</summary>
    public void Refresh()
    {
        if (_model is null)
        {
            return;
        }

        try
        {
            Draw(_model);
        }
        catch (Exception e)
        {
            // A drawing failure must not fault the model operation that raised Changed.
            App.Log("Panel refresh failed: " + e);
        }

        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised after every redraw. The flyout refits its height on it: the window fixes the root's
    /// actual size, so the root's own SizeChanged never fires when the list grows, a tenant finishes
    /// discovery or the pivot changes.
    /// </summary>
    public event EventHandler? ContentChanged;

    /// <summary>Whether the grouped list is showing; the flyout keeps a fixed height while it is.</summary>
    public bool ListVisible => List.Visibility == Visibility.Visible;

    private void Draw(AppModel model)
    {
        OfflinePill.Visibility = model.IsOnline ? Visibility.Collapsed : Visibility.Visible;
        OfflineBar.IsOpen = !model.IsOnline;
        RefreshButton.IsEnabled = model.IsOnline;
        SelectToggle.IsChecked = model.SelectMode;
        SearchToggle.IsChecked = _searchOpen;
        SearchRow.Visibility = _searchOpen ? Visibility.Visible : Visibility.Collapsed;
        if (model.Notice is { } notice)
        {
            NoticeBar.Message = notice;
            NoticeBar.IsOpen = true;
        }
        else
        {
            NoticeBar.IsOpen = false;
        }

        if (model.UpdateAvailable is { } update)
        {
            UpdateBar.Message = $"Elevate {update.Version} is available";
            UpdateBar.IsOpen = true;
        }
        else
        {
            UpdateBar.IsOpen = false;
        }

        if (model.TokenHint is { } hint)
        {
            TokenHintBar.Message = $"{TokenCacheHint.Message(hint.Account)} {TokenCacheHint.Advice} Close to hide this for the account.";
            TokenHintBar.IsOpen = true;
        }
        else
        {
            TokenHintBar.IsOpen = false;
        }

        SyncPivot(model.PanelTab);

        var setup = !model.IsConfigured && model.Identities.Count == 0;
        var noAccounts = !setup && model.Identities.Count == 0;
        SetupView.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
        NoAccountsView.Visibility = noAccounts ? Visibility.Visible : Visibility.Collapsed;
        // The quick-start route is offered only where the id can actually be changed.
        QuickStartShared.Visibility = model.Settings.IsClientIdManaged ? Visibility.Collapsed : Visibility.Visible;
        NoAccountsActions.Visibility = noAccounts && model.SharedAppAdminConsentUrl() is not null ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = setup || noAccounts ? Visibility.Collapsed : Visibility.Visible;
        Pivots.Visibility = setup || noAccounts ? Visibility.Collapsed : Visibility.Visible;
        if (!setup && !noAccounts)
        {
            PanelListBuilder.Reconcile(_groups, PanelListBuilder.Build(model, DateTimeOffset.UtcNow));
        }

        DrawProfiles(model, hidden: setup || noAccounts);

        BulkBar.Visibility = model.SelectMode ? Visibility.Visible : Visibility.Collapsed;
        if (model.SelectMode)
        {
            var count = model.SelectionCount;
            var noun = model.SelectionNoun;
            var tenants = model.Selection.Select(k => k.TenantKey).Distinct().Count();
            BulkCaption.Text = count == 0
                ? "Pick the roles to activate together"
                : $"{count} {noun}{(count == 1 ? "" : "s")} selected · {tenants} tenant{(tenants == 1 ? "" : "s")}";
            BulkActivate.Content = count == 0 ? "Activate" : $"Activate {count} {noun}{(count == 1 ? "" : "s")}";
            BulkActivate.IsEnabled = count > 0 && model.IsOnline;
            BulkProfile.IsEnabled = count > 0;
            var (entra, azure, groups) = model.SelectionBreakdown;
            var parts = new List<string>();
            if (entra > 0)
            {
                parts.Add($"{entra} Entra");
            }

            if (azure > 0)
            {
                parts.Add($"{azure} Azure");
            }

            if (groups > 0)
            {
                parts.Add($"{groups} group{(groups == 1 ? "" : "s")}");
            }

            BulkHint.Text = parts.Count > 1 ? string.Join(", ", parts) + " — switch pivots to add more" : string.Empty;
            BulkHint.Visibility = parts.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>What the row last drew, so the chips are rebuilt only when a pin, a name or the count changed.</summary>
    private string _profilesSignature = string.Empty;

    private void DrawProfiles(AppModel model, bool hidden)
    {
        var profiles = model.Profiles;
        var visible = !hidden && profiles.Count > 0;
        ProfilesRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            _profilesSignature = string.Empty;
            return;
        }

        var pinned = model.PinnedProfiles;
        var signature = string.Join("|", pinned.Select(p => $"{p.Id}:{p.Name}:{p.Entries.Count}:{p.LastJustification is null}")) + "#" + profiles.Count;
        if (signature == _profilesSignature)
        {
            return;
        }

        _profilesSignature = signature;
        AllProfilesLabel.Text = $"All {profiles.Count}";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AllProfiles, $"All profiles, {profiles.Count}");
        ProfileChips.Children.Clear();
        ProfileChips.ColumnDefinitions.Clear();
        if (pinned.Count == 0)
        {
            ProfileChips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ProfileChips.Children.Add(new TextBlock
            {
                Text = "Pin profiles to show them here",
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            return;
        }

        // Star-sized, capped columns: four chips shrink together and trim their names instead of wrapping.
        for (var i = 0; i < pinned.Count; i++)
        {
            ProfileChips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 150 });
            var chip = Chip(pinned[i]);
            Grid.SetColumn(chip, i);
            ProfileChips.Children.Add(chip);
        }
    }

    /// <summary>A pinned profile: click runs it, Ctrl-click runs it silently, right-click for the rest.</summary>
    private Button Chip(ActivationProfile profile)
    {
        var resources = Application.Current.Resources;
        var id = profile.Id;
        // No count on the chip: four names already compete for 356 px, and the tooltip and the All list carry it.
        var content = new Grid { ColumnSpacing = 5 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bolt = new FontIcon { Glyph = "", FontSize = 10, Foreground = (Microsoft.UI.Xaml.Media.Brush)resources["AccentFillColorDefaultBrush"], VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(bolt);
        var name = new TextBlock { Text = profile.Name, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        content.Children.Add(name);

        var chip = new Button
        {
            Style = (Style)resources["SmallButtonStyle"],
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = content,
        };
        ToolTipService.SetToolTip(chip, $"Run {profile.Name} ({ProfileSummary.Caption(profile.Entries)}). Ctrl-click to run with the last reason and durations");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Run {profile.Name}");
        chip.Click += (_, _) =>
        {
            if (_model is not null)
            {
                _ = ProfileActions.RunAsync(_model, id, silentlyIfPossible: IsControlDown());
            }
        };
        chip.ContextRequested += (s, e) =>
        {
            if (_model is null)
            {
                return;
            }

            e.Handled = true;
            ProfileActions.Menu(_model, id, XamlRoot).ShowAt((FrameworkElement)s);
        };
        return chip;
    }
    /// <summary>Once a second while the flyout is open: countdowns, the deactivation lock, Extend.</summary>
    private void Tick()
    {
        if (_model is null || _groups.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var group in _groups)
        {
            foreach (var item in group)
            {
                if (item is not RoleRow row || row.Status is RowStatus.None or RowStatus.Failed)
                {
                    continue;
                }

                var assignment = _model.Assignment(row.RoleKey);
                var policy = _model.Role(row.RoleKey)?.Policy ?? RolePolicy.ManualDefault;
                PanelListBuilder.Fill(_model, row, assignment, policy, row.ViewOnlyReason, now, allowActivate: !row.IsSummary);
            }
        }
    }

    private void SyncPivot(PanelTab tab)
    {
        _syncingPivot = true;
        try
        {
            Pivots.Selected = tab;
        }
        finally
        {
            _syncingPivot = false;
        }
    }

    // MARK: Header actions

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            _ = _model.RefreshAllAsync(userInitiated: true);
        }
    }

    /// <summary>The flyout opens showing everything: the filter is closed and cleared.</summary>
    public void ResetSearch()
    {
        _searchOpen = false;
        SearchBox.Text = string.Empty;
        SearchToggle.IsChecked = false;
        SearchRow.Visibility = Visibility.Collapsed;
    }

    private void OnSearchToggle(object sender, RoutedEventArgs e)
    {
        _searchOpen = SearchToggle.IsChecked == true;
        SearchRow.Visibility = _searchOpen ? Visibility.Visible : Visibility.Collapsed;
        if (_searchOpen)
        {
            SearchBox.Focus(FocusState.Programmatic);
        }
        else
        {
            SearchBox.Text = string.Empty;
            if (_model is not null)
            {
                _model.SearchQuery = string.Empty;
            }
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_model is not null)
        {
            _model.SearchQuery = SearchBox.Text;
        }
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            SearchToggle.IsChecked = false;
            OnSearchToggle(sender, e);
            e.Handled = true;
        }
    }

    private void OnSelectToggle(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            _model.SelectMode = SelectToggle.IsChecked == true;
        }
    }

    private void OnPivotChanged(object? sender, PanelTab tab)
    {
        if (_syncingPivot || _model is null)
        {
            return;
        }

        _model.PanelTab = tab;
    }

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (_model is not null)
        {
            _model.Notice = null;
        }
    }

    private void OnUpdateClosed(InfoBar sender, InfoBarClosedEventArgs args) => _model?.DismissUpdate();

    /// <summary>Only the close button dismisses for the account; Draw closes the bar programmatically when the hint moves on.</summary>
    private void OnTokenHintClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton)
        {
            _model?.DismissTokenHint();
        }
    }

    private void OnTokenHintCopy(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(TokenCacheHint.AzureCliCommand);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        TokenHintCopy.Content = "Copied";
    }

    private void OnUpdateOpen(object sender, RoutedEventArgs e)
    {
        if (_model?.UpdateAvailable is { } update)
        {
            _ = Windows.System.Launcher.LaunchUriAsync(update.Url);
        }
    }

    /// <summary>Whether Ctrl is held: the quick-activate modifier, read at click time like the macOS Option check.</summary>
    internal static bool IsControlDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // MARK: Profiles

    private void OnAllProfiles(object sender, RoutedEventArgs e)
    {
        if (_model is not null && sender is FrameworkElement anchor)
        {
            AllProfilesFlyout.Show(_model, anchor);
        }
    }

    private void OnBulkProfile(object sender, RoutedEventArgs e)
    {
        if (_model is null || _model.Selection.Count == 0)
        {
            return;
        }

        App.Current.OpenSaveProfile([.. _model.Selection.OrderBy(k => k.ToString(), StringComparer.Ordinal)]);
    }

    // MARK: Approvals

    private static ApprovalRow? ApprovalOf(object sender) => (sender as FrameworkElement)?.DataContext as ApprovalRow;

    private void OnApproveClick(object sender, RoutedEventArgs e)
    {
        if (ApprovalOf(sender) is { } row)
        {
            App.Current.OpenDecision(row.RequestId, approve: true);
        }
    }

    private void OnDenyClick(object sender, RoutedEventArgs e)
    {
        if (ApprovalOf(sender) is { } row)
        {
            App.Current.OpenDecision(row.RequestId, approve: false);
        }
    }

    private void OnNoteConfigure(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NoteRow { ConfigureTenant: { } key })
        {
            App.Current.OpenConfigureRoles(key);
        }
    }

    // MARK: Row actions

    private static RoleRow? Row(object sender) => (sender as FrameworkElement)?.DataContext as RoleRow;

    private async void OnActivateClick(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row)
        {
            return;
        }

        // Ctrl-click activates with the last reason and duration when the policy allows it.
        if (IsControlDown() && _model is not null && await _model.QuickActivateAsync(row.RoleKey))
        {
            return;
        }

        App.Current.OpenActivation([row.RoleKey]);
    }

    private void OnExtendClick(object sender, RoutedEventArgs e) => OnActivateClick(sender, e);

    private void OnDeactivateClick(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row && _model is not null)
        {
            if (_model.Active.GetValueOrDefault(row.RoleKey) is { Status.Kind: Elevate.Core.Models.AssignmentStatusKind.Active } assignment)
                _ = DeactivateRowAsync(assignment);
            else
                _ = _model.DeactivateAsync(row.RoleKey);
        }
    }

    private async Task DeactivateRowAsync(Elevate.Core.Models.ActiveAssignment assignment)
    {
        if (_model is null) return;
        var error = await _model.DeactivateAssignmentAsync(assignment);
        if (error is not null) _model.Notice = error;
    }

    private void OnCancelPendingClick(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row && _model is not null)
        {
            _ = _model.CancelPendingAsync(row.RoleKey);
        }
    }

    private void OnSelectClick(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row && _model is not null)
        {
            _model.ToggleSelection(row.RoleKey);
        }
    }

    private void OnBulkActivate(object sender, RoutedEventArgs e)
    {
        if (_model is null || _model.Selection.Count == 0)
        {
            return;
        }

        App.Current.OpenActivation([.. _model.Selection.OrderBy(k => k.ToString(), StringComparer.Ordinal)]);
    }

    // MARK: Group actions

    private static PanelGroup? Group(object sender) => (sender as FrameworkElement)?.DataContext as PanelGroup;

    private void OnToggleGroup(object sender, RoutedEventArgs e)
    {
        if (Group(sender) is not { } group || _model is null)
        {
            return;
        }

        switch (group.Kind)
        {
            case GroupKind.Approvals:
                _model.ToggleApprovals();
                break;
            case GroupKind.ActiveNow:
                _model.ToggleActive();
                break;
            case GroupKind.Identity when group.IdentityId is { } id:
                _model.ToggleIdentity(id);
                break;
            case GroupKind.Tenant when group.TenantKey is { } key:
                _model.ToggleTenant(key);
                break;
            default:
                break;
        }
    }

    /// <summary>The status glyph's flyout: every limitation with its full reason, selectable.</summary>
    private void OnAccessPackagesClick(object sender, RoutedEventArgs e)
    {
        if (Group(sender) is { TenantKey: { } key })
        {
            App.Current.OpenAccessPackages(key);
        }
    }

    private void OnIssuesClick(object sender, RoutedEventArgs e)
    {
        if (Group(sender) is not { } group || group.Issues.Count == 0 || sender is not FrameworkElement anchor)
        {
            return;
        }

        var resources = Application.Current.Resources;
        var list = new StackPanel { Spacing = 10, Width = 320 };
        foreach (var issue in group.Issues)
        {
            var entry = new StackPanel { Spacing = 2 };
            entry.Children.Add(new TextBlock { Text = issue.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            entry.Children.Add(new TextBlock
            {
                Text = issue.Detail,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)resources["TextFillColorSecondaryBrush"],
            });
            list.Children.Add(entry);
        }

        new Flyout { Content = list, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom }.ShowAt(anchor);
    }

    private void OnGroupMenu(object sender, RoutedEventArgs e)
    {
        if (Group(sender) is not { } group || _model is null || sender is not FrameworkElement anchor)
        {
            return;
        }

        var menu = new MenuFlyout();
        if (group.Kind == GroupKind.Identity && group.IdentityId is { } identityId)
        {
            if (group.SignInHelp is { } help)
            {
                // The sign-in method heads the menu instead of crowding the row.
                menu.Items.Add(new MenuFlyoutItem { Text = help, IsEnabled = false });
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            menu.Items.Add(Item("Discover tenants…", () => App.Current.OpenDiscoverTenants(identityId)));
            menu.Items.Add(Item("Add tenant…", () => App.Current.OpenAddTenant(identityId)));
            if (group.TenantKey is { } soleKey && _model.Tenant(soleKey) is { } sole)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                AddTenantItems(menu, sole);
            }

            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Sign out…", () => _ = ConfirmSignOutAsync(identityId)));
        }
        else if (group.Kind == GroupKind.Tenant && group.TenantKey is { } key && _model.Tenant(key) is { } tenant)
        {
            AddTenantItems(menu, tenant);
        }

        menu.ShowAt(anchor);
    }

    private void AddTenantItems(MenuFlyout menu, TenantContext tenant)
    {
        if (_model is null)
        {
            return;
        }

        var model = _model;
        if (tenant.AccessPackagesAvailable == true)
        {
            // First, as on macOS: the one item that opens a window of its own.
            menu.Items.Add(Item("Access packages…", () => App.Current.OpenAccessPackages(tenant.Key)));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        menu.Items.Add(Item("Configure known PIM roles…", () => App.Current.OpenConfigureRoles(tenant.Key)));
        menu.Items.Add(Item("Retry discovery", () => _ = model.RetryDiscoveryAsync(tenant.Key)));
        // Each portal opens in this tenant; the browser session picks the account.
        var open = new MenuFlyoutSubItem { Text = "Open…" };
        foreach (var portal in AdminPortal.All)
        {
            var portalUri = portal.Uri(tenant.TenantId);
            open.Items.Add(Item(portal.Title, () => _ = Windows.System.Launcher.LaunchUriAsync(portalUri)));
        }

        menu.Items.Add(open);
        if ((tenant.DiscoveryMode == DiscoveryMode.ManualRoles || tenant.GroupsUnavailableReason is not null)
            && model.AdminConsentUrl(tenant.IdentityId, tenant.TenantId) is { } url)
        {
            menu.Items.Add(Item("Open admin consent link…", () => _ = Windows.System.Launcher.LaunchUriAsync(url)));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        if (model.IsPinnedTenant(tenant.Key))
        {
            // A pinned tenant would only come back on the next launch; the menu says so rather
            // than offering a removal that does nothing.
            menu.Items.Add(new MenuFlyoutItem { Text = "Pinned by your organization", IsEnabled = false });
            return;
        }

        var remove = Item("Remove tenant…", () => _ = ConfirmRemoveTenantAsync(tenant));
        remove.IsEnabled = tenant.Source != TenantSource.Home;
        menu.Items.Add(remove);
    }

    // Signing out and removing a tenant run at once and have no undo: each says what is forgotten
    // and that the assignments themselves are untouched before it goes ahead.

    private async Task ConfirmSignOutAsync(string identityId)
    {
        if (_model is null || _model.Identity(identityId) is not { } identity)
        {
            return;
        }

        var n = _model.TenantsFor(identityId).Count;
        var tenants = n == 1 ? "its tenant" : $"its {n} tenants";
        var ok = await DialogWindows.ConfirmAsync(
            XamlRoot,
            $"Sign out {identity.Upn}?",
            $"Elevate forgets {tenants}, configured roles and profile entries for this account. Active assignments in Entra are not changed. You can add the account again later.",
            "Sign out");
        if (ok && _model.Identity(identityId) is { } still)
        {
            _model.SignOut(still);
        }
    }

    private async Task ConfirmRemoveTenantAsync(TenantContext tenant)
    {
        if (_model is null)
        {
            return;
        }

        var ok = await DialogWindows.ConfirmAsync(
            XamlRoot,
            $"Remove {tenant.DisplayName}?",
            "Its roles, configured PIM roles and profile entries are removed from Elevate. Active assignments in Entra are not changed. You can add the tenant again later.",
            "Remove tenant");
        if (ok && _model.Tenant(tenant.Key) is not null)
        {
            _model.RemoveTenant(tenant.Key);
        }
    }

    private static MenuFlyoutItem Item(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }

    // MARK: Footer

    private void OnAddAccount(object sender, RoutedEventArgs e) => App.Current.OpenAddAccount();

    private void OnContinueWithCli(object sender, RoutedEventArgs e) => App.Current.OpenAddAccount(SignInMethod.AzureCLI);

    /// <summary>
    /// The shared-app quick start: the no-SLA caveat first, then the shared id through the normal
    /// client-id change path. The setup panel only shows with no accounts, so nothing is signed out.
    /// </summary>
    private async void OnQuickStartShared(object sender, RoutedEventArgs e)
    {
        if (_model is null || !await SharedAppConsent.ConfirmAsync(XamlRoot))
        {
            return;
        }

        SetupError.Visibility = Visibility.Collapsed;
        try
        {
            _model.ApplyClientId(AppSettings.SharedClientId);
        }
        catch (Exception ex) when (ex is PimException or InvalidOperationException)
        {
            SetupError.Text = ex is PimException pim ? pim.UserMessage : ex.Message;
            SetupError.Visibility = Visibility.Visible;
        }
    }

    private void OnGrantSharedConsent(object sender, RoutedEventArgs e)
    {
        if (_model?.SharedAppAdminConsentUrl() is { } url)
        {
            _ = Windows.System.Launcher.LaunchUriAsync(url);
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e) => App.Current.OpenSettings();

    private void OnQuit(object sender, RoutedEventArgs e) => App.Current.Quit();
}
