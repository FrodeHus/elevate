using System.Collections.ObjectModel;
using Elevate.App.Services;
using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

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
        _clock = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => Tick();
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
    }

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

        EntraCount.Count = model.ActiveCount(PanelTab.Roles);
        AzureCount.Count = model.ActiveCount(PanelTab.Azure);
        GroupsCount.Count = model.ActiveCount(PanelTab.Groups);
        SyncPivot(model.PanelTab);

        var setup = !model.IsConfigured && model.Identities.Count == 0;
        var noAccounts = !setup && model.Identities.Count == 0;
        SetupView.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
        NoAccountsView.Visibility = noAccounts ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = setup || noAccounts ? Visibility.Collapsed : Visibility.Visible;
        Pivots.Visibility = setup || noAccounts ? Visibility.Collapsed : Visibility.Visible;
        if (!setup && !noAccounts)
        {
            PanelListBuilder.Reconcile(_groups, PanelListBuilder.Build(model, DateTimeOffset.UtcNow));
        }

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
            Pivots.SelectedItem = tab switch
            {
                PanelTab.Azure => AzurePivot,
                PanelTab.Groups => GroupsPivot,
                _ => EntraPivot,
            };
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

    private void OnPivotChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingPivot || _model is null)
        {
            return;
        }

        _model.PanelTab = sender.SelectedItem == AzurePivot ? PanelTab.Azure
            : sender.SelectedItem == GroupsPivot ? PanelTab.Groups
            : PanelTab.Roles;
    }

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (_model is not null)
        {
            _model.Notice = null;
        }
    }

    // MARK: Row actions

    private static RoleRow? Row(object sender) => (sender as FrameworkElement)?.DataContext as RoleRow;

    private void OnActivateClick(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row)
        {
            App.Current.OpenActivation([row.RoleKey]);
        }
    }

    private void OnExtendClick(object sender, RoutedEventArgs e) => OnActivateClick(sender, e);

    private void OnDeactivateClick(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row && _model is not null)
        {
            _ = _model.DeactivateAsync(row.RoleKey);
        }
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

    private void OnGroupMenu(object sender, RoutedEventArgs e)
    {
        if (Group(sender) is not { } group || _model is null || sender is not FrameworkElement anchor)
        {
            return;
        }

        var menu = new MenuFlyout();
        if (group.Kind == GroupKind.Identity && group.IdentityId is { } identityId)
        {
            menu.Items.Add(Item("Discover tenants…", () => App.Current.OpenDiscoverTenants(identityId)));
            menu.Items.Add(Item("Add tenant…", () => App.Current.OpenAddTenant(identityId)));
            if (group.TenantKey is { } soleKey && _model.Tenant(soleKey) is { } sole)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                AddTenantItems(menu, sole);
            }

            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Sign out", () =>
            {
                if (_model.Identity(identityId) is { } identity)
                {
                    _model.SignOut(identity);
                }
            }));
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
        menu.Items.Add(Item("Configure known PIM roles…", () => App.Current.OpenConfigureRoles(tenant.Key)));
        menu.Items.Add(Item("Retry discovery", () => _ = model.RetryDiscoveryAsync(tenant.Key)));
        if ((tenant.DiscoveryMode == DiscoveryMode.ManualRoles || tenant.GroupsUnavailableReason is not null)
            && model.AdminConsentUrl(tenant.IdentityId, tenant.TenantId) is { } url)
        {
            menu.Items.Add(Item("Open admin consent link…", () => _ = Windows.System.Launcher.LaunchUriAsync(url)));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var remove = Item("Remove tenant", () => model.RemoveTenant(tenant.Key));
        remove.IsEnabled = tenant.Source != TenantSource.Home;
        menu.Items.Add(remove);
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

    private void OnSettings(object sender, RoutedEventArgs e) => App.Current.OpenSettings();

    private void OnQuit(object sender, RoutedEventArgs e) => App.Current.Quit();
}
