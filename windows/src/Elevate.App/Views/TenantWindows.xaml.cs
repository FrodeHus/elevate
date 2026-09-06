using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Discovery;
using Elevate.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>One tenant found by Azure Resource Manager, in the discovery checklist.</summary>
public sealed class DiscoveredItem(DiscoveredTenant tenant, bool tracked) : ObservableObject
{
    private bool _chosen = tracked;

    public DiscoveredTenant Tenant { get; } = tenant;

    public string DisplayName => Tenant.DisplayName;

    public string Caption => Tenant.DefaultDomain ?? Tenant.TenantId;

    /// <summary>Already tracked tenants show checked and disabled.</summary>
    public bool Tracked { get; } = tracked;

    public bool Enabled => !Tracked;

    public bool Chosen
    {
        get => _chosen;
        set => SetProperty(ref _chosen, value);
    }
}

public enum TenantWindowMode
{
    Add,
    Discover,
}

/// <summary>"Add tenant" (a domain or id) and "Discover tenants" (pick from ARM) for one account. Port of the macOS <c>TenantSheets</c>.</summary>
public sealed partial class TenantWindow : Window
{
    private readonly AppModel _model;
    private readonly string _identityId;
    private readonly TenantWindowMode _mode;
    private readonly ObservableCollection<DiscoveredItem> _found = [];
    private bool _working;

    public TenantWindow(AppModel model, string identityId, TenantWindowMode mode)
    {
        InitializeComponent();
        _model = model;
        _identityId = identityId;
        _mode = mode;
        var upn = model.Identity(identityId)?.Upn ?? identityId;
        if (mode == TenantWindowMode.Add)
        {
            DialogWindows.Configure(this, "Add tenant", 440, 230, Root, autoHeight: true);
            Heading.Text = $"Add tenant for {upn}";
            AddPane.Visibility = Visibility.Visible;
            PrimaryButton.Content = "Add";
        }
        else
        {
            DialogWindows.Configure(this, "Discover tenants", 480, 420, Root);
            Heading.Text = $"Tenants for {upn}";
            DiscoverPane.Visibility = Visibility.Visible;
            PrimaryButton.Content = "Track selected";
            Found.ItemsSource = _found;
            _ = DiscoverAsync();
        }

        DialogWindows.DefaultButton(Root, PrimaryButton);
        Update();
    }

    public string IdentityId => _identityId;

    public TenantWindowMode Mode => _mode;

    private void Update()
    {
        PrimaryButton.IsEnabled = !_working && (_mode == TenantWindowMode.Add
            ? Input.Text.Trim().Length > 0
            : _found.Any(i => i.Chosen && !i.Tracked));
        CancelButton.IsEnabled = !_working;
        Working.IsActive = _working;
        Working.Visibility = _working ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e) => Update();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (_working)
        {
            return;
        }

        _working = true;
        Error.IsOpen = false;
        Update();
        try
        {
            if (_mode == TenantWindowMode.Add)
            {
                await _model.AddTenantAsync(_identityId, Input.Text.Trim());
            }
            else
            {
                await _model.TrackTenantsAsync(_identityId, _found.Where(i => i.Chosen && !i.Tracked).Select(i => i.Tenant));
            }

            Close();
            return;
        }
        catch (Exception ex)
        {
            Error.Message = AppModel.Describe(ex);
            Error.IsOpen = true;
        }

        _working = false;
        Update();
    }

    private async Task DiscoverAsync()
    {
        try
        {
            var tenants = await _model.DiscoverTenantsAsync(_identityId);
            foreach (var t in tenants)
            {
                var tracked = _model.Tenant(new TenantKey(_identityId, t.TenantId)) is not null;
                var item = new DiscoveredItem(t, tracked);
                item.PropertyChanged += (_, _) => Update();
                _found.Add(item);
            }

            if (_found.Count == 0)
            {
                Error.Severity = InfoBarSeverity.Informational;
                Error.Message = "Azure Resource Manager lists no tenants for this account.";
                Error.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            Error.Message = AppModel.Describe(ex);
            Error.IsOpen = true;
        }

        Loading.Visibility = Visibility.Collapsed;
        Found.Visibility = Visibility.Visible;
        Update();
    }
}
