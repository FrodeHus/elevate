using System.Collections.ObjectModel;
using Elevate.App.Services;
using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace Elevate.App.Views;

/// <summary>
/// The flyout's content: header, search, notices, the grouped list (pinned "Active now",
/// account rows, tenant headers, role rows), the bulk bar and the footer. Redrawn from the model on
/// every change by reconciling the row objects, so the list keeps its scroll position.
/// </summary>
public sealed partial class PanelView : UserControl
{
    private readonly ObservableCollection<PanelGroup> _groups = [];
    private readonly DispatcherQueueTimer _clock;
    private AppModel? _model;
    private FlyoutWindow? _window;
    // The profile the run control is bound to; null while no profile is in the main view.
    private Guid? _boundProfile;

    public PanelView()
    {
        InitializeComponent();
        GroupedSource.Source = _groups;
        QuickStartShared.Content = SharedAppConsent.QuickStartLabel;
        Accelerate(SearchBox, Windows.System.VirtualKey.F, Windows.System.VirtualKeyModifiers.Control, "Filter roles and groups (Ctrl+F)", FocusSearch);
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

        // The filter is always there while there is a list to filter.
        SearchRow.Visibility = List.Visibility;

        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised after every redraw. The flyout refits its height on it: the window fixes the root's
    /// actual size, so the root's own SizeChanged never fires when the list grows, a tenant finishes
    /// discovery or the pivot changes.
    /// </summary>
    public event EventHandler? ContentChanged;

    /// <summary>Whether the grouped list is showing; the flyout keeps a fixed height while it is.</summary>
    public bool ListVisible => List.Visibility == Visibility.Visible || ProfileView.Visibility == Visibility.Visible;

    private void Draw(AppModel model)
    {
        OfflinePill.Visibility = model.IsOnline ? Visibility.Collapsed : Visibility.Visible;
        // Absent unless the organization pushed OrganizationName with a style other than "none",
        // so an unbranded install draws the header exactly as it did before.
        DrawHeader(model);
        OfflineBar.IsOpen = !model.IsOnline;
        RefreshButton.IsEnabled = model.IsOnline;
        SelectToggle.IsChecked = model.SelectMode;
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

        if (model.StartupError is { } fatal)
        {
            StartupErrorBar.Message = fatal;
            StartupErrorBar.IsOpen = true;
        }
        else
        {
            StartupErrorBar.IsOpen = false;
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

        // Same precedence as the macOS panel: first run wins, then a startup failure stands in for the
        // body (the error bar above is what the user sees), then the empty state, then the list.
        var setup = !model.IsConfigured && model.Identities.Count == 0;
        var failed = !setup && model.StartupError is not null;
        var noAccounts = !setup && !failed && model.Identities.Count == 0;
        var bodyHidden = setup || failed || noAccounts;
        SetupView.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
        SetupOrganizationLine.Text = model.FirstRunLine ?? string.Empty;
        SetupOrganizationLine.Visibility = model.HasFirstRunLine ? Visibility.Visible : Visibility.Collapsed;
        SetupSupportLink.Content = model.SupportLinkLabel ?? string.Empty;
        SetupSupportLink.NavigateUri = model.SupportDestination;
        SetupSupportLink.Visibility = model.HasSupportLink ? Visibility.Visible : Visibility.Collapsed;
        NoAccountsView.Visibility = noAccounts ? Visibility.Visible : Visibility.Collapsed;
        // The quick-start route is offered only where the id can actually be changed.
        QuickStartShared.Visibility = model.Settings.IsClientIdManaged ? Visibility.Collapsed : Visibility.Visible;
        NoAccountsActions.Visibility = noAccounts && model.SharedAppAdminConsentUrl() is not null ? Visibility.Visible : Visibility.Collapsed;
        var profileId = !bodyHidden && model.PanelContent is PanelContent.Profile p ? p.Id : (Guid?)null;
        List.Visibility = bodyHidden || profileId is not null ? Visibility.Collapsed : Visibility.Visible;
        if (!bodyHidden && profileId is null)
        {
            PanelListBuilder.Reconcile(_groups, PanelListBuilder.Build(model, DateTimeOffset.UtcNow));
        }

        DrawProfile(model, profileId);

        Sidebar.Draw(model);

        BulkBar.Visibility = model.SelectMode && profileId is null ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>Binds the run control to the profile in the main view, and lets it go when the view is left.</summary>
    private void DrawProfile(AppModel model, Guid? profileId)
    {
        ProfileView.Visibility = profileId is null ? Visibility.Collapsed : Visibility.Visible;
        if (profileId == _boundProfile)
        {
            return;
        }

        _boundProfile = profileId;
        if (profileId is { } id)
        {
            ProfileRun.Bind(model, id, compact: true);
            ProfileView.ChangeView(0, 0, null, disableAnimation: true);
            // Reason takes focus once the view is laid out, so a draft reopens ready to type into.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ProfileRun.FocusReason);
        }
        else
        {
            ProfileRun.Unbind();
        }
    }

    /// <summary>
    /// Home and the role list carry the Elevate mark with the organization's caption and the offline
    /// pill; a tenant view carries the tenant, its home tag, its account and the tenant's own actions.
    /// </summary>
    private void DrawHeader(AppModel model)
    {
        var profile = model.PanelContent is PanelContent.Profile pc ? model.Profile(pc.Id) : null;
        var tenant = model.PanelContent is PanelContent.Tenant t ? model.Tenant(t.Key) : null;
        var identity = tenant is null ? null : model.Identity(tenant.IdentityId);
        HeaderTitle.Text = profile?.Name ?? tenant?.DisplayName ?? "Elevate";
        ProfileIcon.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;
        SelectToggle.Visibility = profile is null ? Visibility.Visible : Visibility.Collapsed;
        PinButton.Visibility = Visibility.Collapsed;
        if (profile is not null)
        {
            DrawProfileHeader(model, profile);
            return;
        }

        AutomationProperties.SetName(TenantMenuButton, "Tenant menu");
        HomeTag.Visibility = tenant?.Source == TenantSource.Home ? Visibility.Visible : Visibility.Collapsed;
        var caption = tenant is null ? model.HeaderCaption : identity?.Upn;
        OrganizationCaption.Text = caption ?? string.Empty;
        OrganizationCaption.Visibility = tenant is null ? (model.HasHeaderCaption ? Visibility.Visible : Visibility.Collapsed)
            : string.IsNullOrEmpty(caption) ? Visibility.Collapsed : Visibility.Visible;
        OfflinePill.Visibility = tenant is null && !model.IsOnline ? Visibility.Visible : Visibility.Collapsed;
        AccessPackagesButton.Visibility = tenant?.AccessPackagesAvailable == true ? Visibility.Visible : Visibility.Collapsed;
        TenantMenuButton.Visibility = tenant is null ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.PlaceholderText = tenant is null ? "Filter roles" : $"Filter roles in {tenant.DisplayName}";
    }

    /// <summary>The profile's title block: name, "N roles · M tenants" with the shortcut when bound, pin and the profile menu.</summary>
    private void DrawProfileHeader(AppModel model, ActivationProfile profile)
    {
        var tenants = profile.Entries.Select(e => e.RoleKey.TenantKey).Distinct().Count();
        var caption = $"{ProfileSummary.Caption(profile.Entries)} · {tenants} tenant{(tenants == 1 ? "" : "s")}";
        if (model.Settings.HotKeyProfileId == profile.Id && model.Settings.HotKey is { } hotKey)
        {
            caption += " · " + hotKey.Display;
        }

        HomeTag.Visibility = OfflinePill.Visibility = AccessPackagesButton.Visibility = Visibility.Collapsed;
        OrganizationCaption.Text = caption;
        OrganizationCaption.Visibility = Visibility.Visible;
        TenantMenuButton.Visibility = Visibility.Visible;
        AutomationProperties.SetName(TenantMenuButton, "Profile actions");
        // A published profile is the organization's: no pin of its own.
        PinButton.Visibility = profile.Source == ProfileSource.Managed ? Visibility.Collapsed : Visibility.Visible;
        var resources = Application.Current.Resources;
        PinGlyph.Glyph = profile.Pinned ? "" : "";
        PinGlyph.Foreground = (Microsoft.UI.Xaml.Media.Brush)resources[profile.Pinned ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
        var tip = profile.Pinned ? "Unpin from flyout" : "Pin to flyout";
        PinButton.IsEnabled = profile.Pinned || model.CanPinAnotherProfile;
        ToolTipService.SetToolTip(PinButton, PinButton.IsEnabled ? tip : ProfileActions.PinRefusedHint);
        AutomationProperties.SetName(PinButton, tip);
    }

    private void OnPinProfile(object sender, RoutedEventArgs e)
    {
        if (_model?.PanelContent is PanelContent.Profile p && _model.Profile(p.Id) is { } profile
            && !_model.SetPinned(p.Id, !profile.Pinned))
        {
            ToolTipService.SetToolTip(PinButton, ProfileActions.PinRefusedHint);
        }
    }

    private void OnHeaderAccessPackages(object sender, RoutedEventArgs e)
    {
        if (_model?.ScopeTenant is { } key)
        {
            App.Current.OpenAccessPackages(key);
        }
    }

    private void OnHeaderTenantMenu(object sender, RoutedEventArgs e)
    {
        if (_model?.PanelContent is PanelContent.Profile p)
        {
            ProfileActions.Menu(_model, p.Id, XamlRoot, inPanel: true).ShowAt((FrameworkElement)sender);
            return;
        }

        if (_model?.ScopeTenant is { } key && _model.Tenant(key) is { } tenant)
        {
            PanelMenus.TenantMenu(_model, tenant, XamlRoot).ShowAt((FrameworkElement)sender);
        }
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (_model is null || (sender as FrameworkElement)?.DataContext is not ActionRow row)
        {
            return;
        }

        switch (row.Action)
        {
            case PanelAction.BrowseAll:
                _model.Browsing = true;
                break;
            case PanelAction.SearchAllTenants:
                _model.SearchAllTenants();
                break;
            case PanelAction.SelectRoles:
                _model.SelectMode = true;
                break;
            default:
                break;
        }
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
                var row = item as RoleRow ?? (item as HeroRow)?.Role;
                if (row is null || row.Status is RowStatus.None or RowStatus.Failed)
                {
                    continue;
                }

                var assignment = _model.Assignment(row.RoleKey);
                PanelListBuilder.Fill(_model, row, assignment, row.ViewOnlyReason, now, allowActivate: !row.IsSummary);
            }
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

    /// <summary>The flyout opens showing everything: the filter is cleared.</summary>
    public void ResetSearch() => SearchBox.Text = string.Empty;

    /// <summary>The flyout opens ready to type in: called once the window is active, or the focus does not take.</summary>
    public void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_model is not null)
        {
            _model.SearchQuery = SearchBox.Text;
        }
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            // Escape clears a filter first; with nothing to clear it is left to close the flyout.
            case Windows.System.VirtualKey.Escape when SearchBox.Text.Length > 0:
                SearchBox.Text = string.Empty;
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Down:
                e.Handled = (List.ContainerFromIndex(0) as Control)?.Focus(FocusState.Keyboard) == true;
                break;
            // Enter takes the top match, but only once something was typed: on the unfiltered list
            // it would open whichever role happens to be first.
            case Windows.System.VirtualKey.Enter when SearchBox.Text.Length > 0:
                if (_groups.SelectMany(g => g).OfType<RoleRow>().FirstOrDefault(CanActivate) is { } top)
                {
                    Activate(top);
                    e.Handled = true;
                }

                break;
            default:
                break;
        }
    }

    /// <summary>Arrow keys move between rows on their own (the ListView's doing); this adds Enter, and Up from the first row back to the filter.</summary>
    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Only a focused row: Enter on a button inside one already presses that button.
        if (FocusManager.GetFocusedElement(XamlRoot) is not ListViewItem item)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter && item.Content is RoleRow row && CanActivate(row))
        {
            Activate(row);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Up && List.IndexFromContainer(item) == 0)
        {
            FocusSearch();
            e.Handled = true;
        }
    }

    /// <summary>Right-click (or the menu key on a focused row): favorites, which are pinned one-role profiles.</summary>
    private void OnListContext(UIElement sender, ContextRequestedEventArgs e)
    {
        var source = e.OriginalSource as FrameworkElement;
        var row = source is ListViewItem { Content: RoleRow focused } ? focused : source?.DataContext as RoleRow;
        if (_model is not { } model || row is null || source is null)
        {
            return;
        }

        var key = row.RoleKey;
        // The scope is part of the name: "Contributor" alone does not tell two favorites apart.
        var name = string.IsNullOrEmpty(row.Detail) ? row.Name : $"{row.Name} · {row.Detail}";
        var favorite = model.IsFavorite(key);
        var menu = new MenuFlyout();
        menu.Items.Add(PanelMenus.Item(favorite ? "Remove from favorites" : "Add to favorites", () => model.SetFavorite(key, name, !favorite)));
        e.Handled = true;
        if (e.TryGetPosition(source, out var point))
        {
            menu.ShowAt(source, point);
        }
        else
        {
            menu.ShowAt(source);
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
        if (_model is not null)
        {
            _model.PanelTab = tab;
        }
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

    private void OnBulkProfile(object sender, RoutedEventArgs e)
    {
        if (_model is null || _model.Selection.Count == 0)
        {
            return;
        }

        App.Current.OpenSaveProfile((RoleKey[])[.. _model.Selection.OrderBy(k => k.ToString(), StringComparer.Ordinal)]);
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

    private void OnActivateClick(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row)
        {
            Activate(row);
        }
    }

    /// <summary>Whether the row offers Activate or Extend right now; what Enter may press.</summary>
    private static bool CanActivate(RoleRow row) =>
        row.ActivateVisibility == Visibility.Visible || row.ExtendVisibility == Visibility.Visible;

    private async void Activate(RoleRow row)
    {
        // Ctrl-click activates with the last reason and duration when the policy allows it.
        if (IsControlDown() && _model is not null && await _model.QuickActivateAsync(row.RoleKey))
        {
            return;
        }

        App.Current.OpenActivation((RoleKey[])[row.RoleKey]);
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

    private static ScopeRow? Scope(object sender) => (sender as FrameworkElement)?.DataContext as ScopeRow;

    /// <summary>
    /// The subtree checkbox: every eligibility under this scope in one press. The model finds the
    /// node again from the current tree rather than the row holding one, so a refresh that arrived
    /// between the draw and the click cannot select something that is no longer there.
    /// </summary>
    private void OnScopeSelectClick(object sender, RoutedEventArgs e)
    {
        if (Scope(sender) is not { } row || _model is null || Node(row) is not { } node)
        {
            return;
        }

        _model.ToggleSubtree(node);
    }

    private void OnScopeChevronClick(object sender, RoutedEventArgs e)
    {
        if (Scope(sender) is not { } row || _model is null || Node(row) is not { } node)
        {
            return;
        }

        _model.ToggleScope(row.TenantKey, node);
    }

    /// <summary>The tree node a scope row stands for, as the model sees it now.</summary>
    private ScopeNode? Node(ScopeRow row) =>
        _model is null ? null : Find(_model.AzureTree(row.TenantKey), row.Scope);

    private static ScopeNode? Find(IEnumerable<ScopeNode> nodes, string scope)
    {
        foreach (var node in nodes)
        {
            if (node.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (Find(node.Children, scope) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnBulkActivate(object sender, RoutedEventArgs e)
    {
        if (_model is null || _model.Selection.Count == 0)
        {
            return;
        }

        App.Current.OpenActivation((RoleKey[])[.. _model.Selection.OrderBy(k => k.ToString(), StringComparer.Ordinal)]);
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

        if (group.Kind == GroupKind.Identity && group.IdentityId is { } identityId && _model.Identity(identityId) is { } identity)
        {
            var sole = group.TenantKey is { } soleKey ? _model.Tenant(soleKey) : null;
            PanelMenus.IdentityMenu(_model, identity, sole, XamlRoot).ShowAt(anchor);
        }
        else if (group.Kind == GroupKind.Tenant && group.TenantKey is { } key && _model.Tenant(key) is { } tenant)
        {
            PanelMenus.TenantMenu(_model, tenant, XamlRoot).ShowAt(anchor);
        }
    }

    /// <summary>The account row's Sign in button: re-runs the account's own sign-in method and clears the flag.</summary>
    private void OnSignInAgainClick(object sender, RoutedEventArgs e)
    {
        if (_model is not null && Group(sender) is { IdentityId: { } identityId })
        {
            PanelMenus.RetrySignIn(_model, identityId);
        }
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
