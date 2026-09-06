using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Elevate.App.Services;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Elevate.App.Views;

/// <summary>The dot at the start of a row, from the assignment's status.</summary>
public enum RowStatus
{
    None,
    Active,
    Scheduled,
    Pending,
    Provisioning,
    Failed,
}

/// <summary>Base of everything the panel list shows.</summary>
public abstract class PanelItem : ObservableObject
{
    /// <summary>Stable identity for reconciliation.</summary>
    public abstract string Key { get; }
}

/// <summary>A caption row: an empty pivot, a groups-off reason.</summary>
public sealed class NoteRow(string key, string text) : PanelItem
{
    private string _text = text;

    public override string Key { get; } = key;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }
}

/// <summary>One eligible role (or one active assignment in the summary), with everything its row shows.</summary>
public sealed class RoleRow : PanelItem
{
    public RoleRow(RoleKey roleKey, bool isSummary)
    {
        RoleKey = roleKey;
        IsSummary = isSummary;
        Key = (isSummary ? "active:" : "role:") + roleKey;
    }

    public override string Key { get; }

    public RoleKey RoleKey { get; }

    /// <summary>Rows in the pinned "Active now" group never offer Activate and carry a tenant · account caption.</summary>
    public bool IsSummary { get; }

    private string _name = string.Empty;
    private string? _detail;
    private string? _detailTooltip;
    private string? _via;
    private bool _isManual;
    private RowStatus _status;
    private string _countdown = string.Empty;
    private bool _countdownSoon;
    private string? _statusText;
    private string? _failedText;
    private bool _showActivate;
    private bool _showExtend;
    private bool _showDeactivate;
    private bool _deactivateEnabled;
    private string? _deactivateTooltip;
    private bool _showCancel;
    private bool _showCancelPending;
    private bool _inFlight;
    private bool _selectMode;
    private bool _selected;
    private bool _selectEnabled;
    private string? _viewOnlyReason;
    private bool _online = true;

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public string? Detail { get => _detail; set => SetProperty(ref _detail, value); }

    public string? DetailTooltip { get => _detailTooltip; set => SetProperty(ref _detailTooltip, value); }

    public string? Via { get => _via; set => SetProperty(ref _via, value); }

    public bool IsManual { get => _isManual; set => SetProperty(ref _isManual, value); }

    public RowStatus Status { get => _status; set => SetProperty(ref _status, value); }

    public string Countdown { get => _countdown; set => SetProperty(ref _countdown, value); }

    public bool CountdownSoon { get => _countdownSoon; set => SetProperty(ref _countdownSoon, value); }

    /// <summary>"awaiting approval", "provisioning", "starts in 2 h".</summary>
    public string? StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    public string? FailedText { get => _failedText; set => SetProperty(ref _failedText, value); }

    public bool ShowActivate { get => _showActivate; set => SetProperty(ref _showActivate, value); }

    public bool ShowExtend { get => _showExtend; set => SetProperty(ref _showExtend, value); }

    public bool ShowDeactivate { get => _showDeactivate; set => SetProperty(ref _showDeactivate, value); }

    public bool DeactivateEnabled { get => _deactivateEnabled; set => SetProperty(ref _deactivateEnabled, value); }

    public string? DeactivateTooltip { get => _deactivateTooltip; set => SetProperty(ref _deactivateTooltip, value); }

    /// <summary>Cancel for a scheduled activation.</summary>
    public bool ShowCancel { get => _showCancel; set => SetProperty(ref _showCancel, value); }

    /// <summary>Cancel for a request awaiting approval.</summary>
    public bool ShowCancelPending { get => _showCancelPending; set => SetProperty(ref _showCancelPending, value); }

    public bool InFlight { get => _inFlight; set => SetProperty(ref _inFlight, value); }

    public bool SelectMode { get => _selectMode; set => SetProperty(ref _selectMode, value); }

    public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }

    public bool SelectEnabled { get => _selectEnabled; set => SetProperty(ref _selectEnabled, value); }

    public string? ViewOnlyReason { get => _viewOnlyReason; set => SetProperty(ref _viewOnlyReason, value); }

    public bool Online { get => _online; set => SetProperty(ref _online, value); }

    // Derived, for x:Bind (recomputed through the notifications of the properties above).
    public Visibility DetailVisibility => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ViaVisibility => string.IsNullOrEmpty(Via) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ManualVisibility => IsManual ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CountdownVisibility => Status == RowStatus.Active ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StatusTextVisibility => string.IsNullOrEmpty(StatusText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FailedVisibility => string.IsNullOrEmpty(FailedText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ActivateVisibility => ShowActivate && !SelectMode && !InFlight ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExtendVisibility => ShowExtend && !InFlight ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DeactivateVisibility => ShowDeactivate && !InFlight ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CancelVisibility => ShowCancel && !InFlight ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CancelPendingVisibility => ShowCancelPending && !InFlight ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ProvisioningVisibility => Status == RowStatus.Provisioning && !InFlight ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InFlightVisibility => InFlight ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SelectVisibility => SelectMode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ViewOnlyVisibility => Status == RowStatus.None && ViewOnlyReason is not null && !SelectMode ? Visibility.Visible : Visibility.Collapsed;

    public Brush CountdownBrush => (Brush)Application.Current.Resources[CountdownSoon ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"];

    public Windows.UI.Text.FontWeight CountdownWeight => CountdownSoon ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public bool ActivateEnabled => Online;

    public bool ExtendEnabled => Online;

    public bool CancelEnabled => Online;

    /// <summary>Copies every displayed value from a freshly built row of the same key.</summary>
    public void CopyFrom(RoleRow other)
    {
        Name = other.Name;
        Detail = other.Detail;
        DetailTooltip = other.DetailTooltip;
        Via = other.Via;
        IsManual = other.IsManual;
        Status = other.Status;
        Countdown = other.Countdown;
        CountdownSoon = other.CountdownSoon;
        StatusText = other.StatusText;
        FailedText = other.FailedText;
        ShowActivate = other.ShowActivate;
        ShowExtend = other.ShowExtend;
        ShowDeactivate = other.ShowDeactivate;
        DeactivateEnabled = other.DeactivateEnabled;
        DeactivateTooltip = other.DeactivateTooltip;
        ShowCancel = other.ShowCancel;
        ShowCancelPending = other.ShowCancelPending;
        InFlight = other.InFlight;
        SelectMode = other.SelectMode;
        Selected = other.Selected;
        SelectEnabled = other.SelectEnabled;
        ViewOnlyReason = other.ViewOnlyReason;
        Online = other.Online;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Re-raises every derived property after a change, so x:Bind picks the new value up.</summary>
    public void RaiseAll() => OnPropertyChanged(string.Empty);
}

/// <summary>One request awaiting this user's decision, in the pinned "Approvals" group.</summary>
public sealed class ApprovalRow(string requestId) : PanelItem
{
    private string _requesterName = string.Empty;
    private string _target = string.Empty;
    private string _caption = string.Empty;
    private string? _justification;
    private string? _error;
    private bool _inFlight;
    private bool _canDecide;
    private bool _online = true;

    public override string Key { get; } = "approval:" + requestId;

    public string RequestId { get; } = requestId;

    public string RequesterName { get => _requesterName; set => SetProperty(ref _requesterName, value); }

    /// <summary>"Reader · rg-ops · resource group": the target with its scope caption.</summary>
    public string Target { get => _target; set => SetProperty(ref _target, value); }

    /// <summary>"Contoso · 04:00 · 2 hours ago".</summary>
    public string Caption { get => _caption; set => SetProperty(ref _caption, value); }

    public string? Justification { get => _justification; set => SetProperty(ref _justification, value); }

    public string? Error { get => _error; set => SetProperty(ref _error, value); }

    public bool InFlight { get => _inFlight; set => SetProperty(ref _inFlight, value); }

    /// <summary>Only an activation can be decided through the APIs; extend, renew and other point at the portal.</summary>
    public bool CanDecide { get => _canDecide; set => SetProperty(ref _canDecide, value); }

    public bool Online { get => _online; set => SetProperty(ref _online, value); }

    public Visibility InFlightVisibility => InFlight ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DecideVisibility => CanDecide && !InFlight ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PortalVisibility => !CanDecide && !InFlight ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ErrorVisibility => string.IsNullOrEmpty(Error) ? Visibility.Collapsed : Visibility.Visible;

    public void CopyFrom(ApprovalRow other)
    {
        RequesterName = other.RequesterName;
        Target = other.Target;
        Caption = other.Caption;
        Justification = other.Justification;
        Error = other.Error;
        InFlight = other.InFlight;
        CanDecide = other.CanDecide;
        Online = other.Online;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>One saved profile, as a chip in the profiles row.</summary>
public sealed class ProfileChip(Guid id) : ObservableObject
{
    private string _name = string.Empty;
    private string _caption = string.Empty;

    public Guid Id { get; } = id;

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public string Caption { get => _caption; set => SetProperty(ref _caption, value); }

    public string Tooltip => $"Run {Name}. Ctrl-click to run with the last reason and durations";
}

public enum GroupKind
{
    Approvals,
    ActiveNow,
    Identity,
    Tenant,
}

/// <summary>
/// One ListView group: the pinned "Active now" summary, an account row, or a tenant header. The
/// group is the collection of its rows, which is the shape a grouped CollectionViewSource tracks
/// for changes; the header binds to the properties.
/// </summary>
public sealed class PanelGroup : ObservableCollection<PanelItem>
{
    public PanelGroup(string key, GroupKind kind)
    {
        Key = key;
        Kind = kind;
    }

    public string Key { get; }

    public GroupKind Kind { get; }

    public string? IdentityId { get; set; }

    public TenantKey? TenantKey { get; set; }

    private string _title = string.Empty;
    private string? _caption;
    private string _initials = string.Empty;
    private int _activeCount;
    private bool _expanded = true;
    private bool _isHome;
    private string? _viewOnlyReason;
    private bool _manual;
    private string? _azureOff;
    private string? _groupsOff;
    private string? _error;
    private bool _busy;

    public string Title { get => _title; set => Set(ref _title, value); }

    public string? Caption { get => _caption; set => Set(ref _caption, value); }

    public string Initials { get => _initials; set => Set(ref _initials, value); }

    public int ActiveCount { get => _activeCount; set => Set(ref _activeCount, value); }

    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }

    public bool IsHome { get => _isHome; set => Set(ref _isHome, value); }

    public string? ViewOnlyReason { get => _viewOnlyReason; set => Set(ref _viewOnlyReason, value); }

    public bool Manual { get => _manual; set => Set(ref _manual, value); }

    public string? AzureOff { get => _azureOff; set => Set(ref _azureOff, value); }

    public string? GroupsOff { get => _groupsOff; set => Set(ref _groupsOff, value); }

    public string? Error { get => _error; set => Set(ref _error, value); }

    public bool Busy { get => _busy; set => Set(ref _busy, value); }

    // Derived, for x:Bind.
    public string ActiveText => ActiveCount > 0 ? $"{ActiveCount} active" : string.Empty;

    public Visibility ActiveVisibility => ActiveCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    private int _pendingCount;

    /// <summary>Requests in the pinned "Approvals" group.</summary>
    public int PendingCount { get => _pendingCount; set => Set(ref _pendingCount, value); }

    public string PendingText => PendingCount > 0 ? $"{PendingCount} pending" : string.Empty;

    public Visibility PendingVisibility => PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CaptionVisibility => string.IsNullOrEmpty(Caption) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility AvatarVisibility => Kind == GroupKind.Identity ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MenuVisibility => Kind is GroupKind.ActiveNow or GroupKind.Approvals ? Visibility.Collapsed : Visibility.Visible;

    public Visibility HomeVisibility => IsHome && Kind == GroupKind.Tenant ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ViewOnlyVisibility => ViewOnlyReason is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ManualVisibility => Manual ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AzureOffVisibility => AzureOff is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility GroupsOffVisibility => GroupsOff is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ErrorVisibility => Error is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility BusyVisibility => Busy ? Visibility.Visible : Visibility.Collapsed;

    public string ChevronGlyph => Expanded ? "" : "";

    public Brush HeaderBrush => (Brush)Application.Current.Resources[
        Kind == GroupKind.Identity ? "LayerOnMicaBaseAltFillColorDefaultBrush" : "LayerOnMicaBaseAltFillColorSecondaryBrush"];

    public void CopyFrom(PanelGroup other)
    {
        IdentityId = other.IdentityId;
        TenantKey = other.TenantKey;
        Title = other.Title;
        Caption = other.Caption;
        Initials = other.Initials;
        ActiveCount = other.ActiveCount;
        PendingCount = other.PendingCount;
        Expanded = other.Expanded;
        IsHome = other.IsHome;
        ViewOnlyReason = other.ViewOnlyReason;
        Manual = other.Manual;
        AzureOff = other.AzureOff;
        GroupsOff = other.GroupsOff;
        Error = other.Error;
        Busy = other.Busy;
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }
}

/// <summary>Picks the row template by item type.</summary>
public sealed partial class PanelItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Role { get; set; }

    public DataTemplate? Note { get; set; }

    public DataTemplate? Approval { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        RoleRow => Role,
        ApprovalRow => Approval,
        _ => Note,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>Builds the panel's groups and rows from the model, and merges them into the live collections.</summary>
public static class PanelListBuilder
{
    /// <summary>The five-minute lock PIM enforces after an activation before it can be deactivated.</summary>
    public static readonly TimeSpan DeactivationLock = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<PanelGroup> Build(AppModel model, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(model);
        var groups = new List<PanelGroup>();

        var approvals = model.ApprovalsOrdered;
        if (approvals.Count > 0)
        {
            var group = new PanelGroup("approvals", GroupKind.Approvals)
            {
                Title = "Approvals",
                PendingCount = approvals.Count,
                Expanded = !model.CollapsedApprovals,
            };
            if (group.Expanded)
            {
                foreach (var request in approvals)
                {
                    group.Add(ApprovalRowFor(model, request, now));
                }
            }

            groups.Add(group);
        }

        var active = model.ActiveAssignmentsOrdered;
        if (active.Count > 0)
        {
            var group = new PanelGroup("active-now", GroupKind.ActiveNow)
            {
                Title = "Active now",
                ActiveCount = active.Count,
                Expanded = !model.CollapsedActive,
            };
            if (group.Expanded)
            {
                foreach (var assignment in active)
                {
                    group.Add(SummaryRow(model, assignment, now));
                }
            }

            groups.Add(group);
        }

        var identities = model.VisibleIdentities;
        if (identities.Count == 0 && model.IsFiltering)
        {
            var none = new PanelGroup("no-matches", GroupKind.Tenant) { Title = string.Empty };
            none.Add(new NoteRow("no-matches", "No matches"));
            groups.Add(none);
        }

        foreach (var identity in identities)
        {
            var tenants = model.VisibleTenants(identity.Id);
            var expanded = !model.CollapsedIdentities.Contains(identity.Id);
            if (tenants.Count == 1)
            {
                // One tenant: fold it into the account row and skip the tenant header.
                var only = tenants[0];
                var group = IdentityGroup(model, identity, only);
                if (expanded)
                {
                    AddTenantRows(model, group, only, now);
                }

                groups.Add(group);
                continue;
            }

            groups.Add(IdentityGroup(model, identity, null));
            if (!expanded)
            {
                continue;
            }

            foreach (var tenant in tenants)
            {
                var group = TenantGroup(model, identity, tenant);
                if (group.Expanded)
                {
                    AddTenantRows(model, group, tenant, now);
                }

                groups.Add(group);
            }
        }

        return groups;
    }

    private static PanelGroup IdentityGroup(AppModel model, Identity identity, TenantContext? soleTenant)
    {
        var group = new PanelGroup("identity:" + identity.Id, GroupKind.Identity)
        {
            IdentityId = identity.Id,
            TenantKey = soleTenant?.Key,
            Title = identity.Upn,
            Initials = Initials(identity),
            Expanded = !model.CollapsedIdentities.Contains(identity.Id),
        };
        var caption = new List<string>();
        if (soleTenant is not null)
        {
            caption.Add(soleTenant.DisplayName);
            if (soleTenant.Source == TenantSource.Home)
            {
                caption.Add("home");
            }
        }

        caption.Add(identity.SignInMethod.Kind == SignInMethodKind.OwnApp ? "Own app" : identity.SignInMethod.DisplayName);
        group.Caption = string.Join(" · ", caption);
        // Account-level badge follows the tenants: it disappears once every tenant's token proves the write scope.
        var tenants = model.TenantsFor(identity.Id);
        group.ViewOnlyReason = tenants.Select(t => model.EntraViewOnlyReason(t.Key)).FirstOrDefault(r => r is not null)
            ?? (tenants.Count == 0 ? identity.SignInMethod.EntraViewOnlyReason : null);
        if (soleTenant is not null)
        {
            ApplyPills(model, group, soleTenant);
            group.ActiveCount = model.ActiveCount(soleTenant.Key);
            group.IsHome = soleTenant.Source == TenantSource.Home;
        }

        return group;
    }

    private static PanelGroup TenantGroup(AppModel model, Identity identity, TenantContext tenant)
    {
        var group = new PanelGroup("tenant:" + tenant.IdentityId + ":" + tenant.TenantId, GroupKind.Tenant)
        {
            IdentityId = identity.Id,
            TenantKey = tenant.Key,
            Title = tenant.DisplayName,
            Expanded = !model.CollapsedTenants.Contains(tenant.Key),
            IsHome = tenant.Source == TenantSource.Home,
            ViewOnlyReason = model.EntraViewOnlyReason(tenant.Key),
            ActiveCount = model.ActiveCount(tenant.Key),
        };
        ApplyPills(model, group, tenant);
        return group;
    }

    private static void ApplyPills(AppModel model, PanelGroup group, TenantContext tenant)
    {
        group.Manual = tenant.DiscoveryMode == DiscoveryMode.ManualRoles;
        group.AzureOff = tenant.AzureUnavailableReason;
        group.GroupsOff = tenant.GroupsUnavailableReason;
        group.Error = model.TenantErrors.GetValueOrDefault(tenant.Key) ?? tenant.LastDiscoveryError;
        group.Busy = model.Busy.Contains(tenant.Key);
    }

    private static void AddTenantRows(AppModel model, PanelGroup group, TenantContext tenant, DateTimeOffset now)
    {
        var roles = model.RolesFor(tenant.Key, model.PanelTab);
        var prefix = group.Key + ":";
        if (model.PanelTab == PanelTab.Groups && model.GroupsUnavailableReason(tenant.Key) is { } reason)
        {
            group.Add(new NoteRow(prefix + "note", reason));
        }
        else if (roles.Count == 0)
        {
            group.Add(new NoteRow(prefix + "note", EmptyText(model, tenant)));
        }

        foreach (var role in roles)
        {
            group.Add(RoleRowFor(model, role, now));
        }
    }

    private static string EmptyText(AppModel model, TenantContext tenant) => (model.PanelTab, tenant.DiscoveryMode) switch
    {
        (PanelTab.Groups, _) => "No eligible groups.",
        (PanelTab.Azure, DiscoveryMode.ManualRoles) => "No roles configured.",
        (PanelTab.Azure, _) => tenant.AzureUnavailableReason ?? "No eligible Azure resource roles.",
        (PanelTab.Roles, DiscoveryMode.ManualRoles) => "No roles configured.",
        _ => model.EntraViewOnlyReason(tenant.Key) is not null
            ? "This account supports Azure resource roles only; see the Azure pivot."
            : "No eligible Entra roles.",
    };

    private static RoleRow RoleRowFor(AppModel model, EligibleRole role, DateTimeOffset now)
    {
        var row = new RoleRow(role.Key, isSummary: false)
        {
            Name = role.DisplayName,
            Via = role.ViaGroup is { } via ? (via == "group" ? "via group" : $"via {via}") : null,
            IsManual = role.Source == RoleSource.Manual,
        };
        var detail = new List<string>();
        if (role.Detail is { } d)
        {
            detail.Add(d);
        }

        if (role.Policy.RequiresApproval)
        {
            detail.Add("Approval required");
        }

        row.Detail = detail.Count > 0 ? string.Join(" · ", detail) : null;
        row.DetailTooltip = role.Key.Scope is AzureResourceScope azure ? azure.Scope : role.Detail;
        var viewOnly = role.Key.Scope.Kind == RoleScopeKind.EntraDirectory ? model.EntraViewOnlyReason(role.Key.TenantKey) : null;
        Fill(model, row, model.Assignment(role.Key), role.Policy, viewOnly, now, allowActivate: true);
        return row;
    }

    private static ApprovalRow ApprovalRowFor(AppModel model, ApprovalRequest request, DateTimeOffset now)
    {
        var caption = new List<string> { model.ApprovalTenantName(request) };
        if (request.RequestedDuration is { } d)
        {
            caption.Add(Countdown.Label(d));
        }

        if (request.CreatedAt is { } created)
        {
            caption.Add(Relative(created, now));
        }

        return new ApprovalRow(request.Id)
        {
            RequesterName = request.RequesterName,
            Target = string.IsNullOrEmpty(request.ScopeCaption) ? request.TargetName : $"{request.TargetName} · {request.ScopeCaption}",
            Caption = string.Join(" · ", caption),
            Justification = request.Justification ?? "No reason given",
            Error = model.ApprovalErrors.GetValueOrDefault(request.Id),
            InFlight = model.DecisionInFlight.Contains(request.Id),
            CanDecide = request.Action == ApprovalAction.Activate,
            Online = model.IsOnline,
        };
    }

    /// <summary>"just now", "5 minutes ago", "2 hours ago", "3 days ago".</summary>
    public static string Relative(DateTimeOffset date, DateTimeOffset now)
    {
        var elapsed = now - date;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            var m = (int)elapsed.TotalMinutes;
            return m == 1 ? "1 minute ago" : $"{m} minutes ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            var h = (int)elapsed.TotalHours;
            return h == 1 ? "1 hour ago" : $"{h} hours ago";
        }

        var days = (int)elapsed.TotalDays;
        return days == 1 ? "yesterday" : $"{days} days ago";
    }

    private static RoleRow SummaryRow(AppModel model, ActiveAssignment assignment, DateTimeOffset now)
    {
        var key = assignment.RoleKey;
        var row = new RoleRow(key, isSummary: true)
        {
            Name = model.SummaryName(key),
            Detail = $"{model.Tenant(key.TenantKey)?.DisplayName ?? key.TenantId} · {model.Identity(key.IdentityId)?.Upn ?? key.IdentityId}",
        };
        Fill(model, row, assignment, model.Role(key)?.Policy ?? RolePolicy.ManualDefault, null, now, allowActivate: false);
        return row;
    }

    /// <summary>The time- and status-dependent part of a row; also what the countdown timer recomputes.</summary>
    public static void Fill(AppModel model, RoleRow row, ActiveAssignment? assignment, RolePolicy policy, string? viewOnlyReason, DateTimeOffset now, bool allowActivate)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(row);
        var key = row.RoleKey;
        row.InFlight = model.InFlight.Contains(key);
        row.Online = model.IsOnline;
        row.SelectMode = model.SelectMode && !row.IsSummary;
        row.Selected = model.Selection.Contains(key);
        row.ViewOnlyReason = viewOnlyReason;
        row.ShowActivate = false;
        row.ShowExtend = false;
        row.ShowDeactivate = false;
        row.ShowCancel = false;
        row.ShowCancelPending = false;
        row.StatusText = null;
        row.FailedText = null;
        row.Countdown = string.Empty;
        row.CountdownSoon = false;
        row.DeactivateTooltip = null;

        switch (assignment?.Status.Kind)
        {
            case AssignmentStatusKind.Active:
            {
                row.Status = RowStatus.Active;
                var remaining = assignment.EndDateTime is { } end ? Countdown.Remaining(end, now) : null;
                row.Countdown = remaining is { } r ? Countdown.Label(r) : string.Empty;
                row.CountdownSoon = remaining is { } left && left <= TimeSpan.FromMinutes(10);
                var lockedFor = DeactivationLock - (now - assignment.StartDateTime);
                row.ShowDeactivate = true;
                row.DeactivateEnabled = lockedFor <= TimeSpan.Zero && model.IsOnline;
                row.DeactivateTooltip = lockedFor > TimeSpan.Zero
                    ? $"Can be deactivated in {Math.Ceiling(lockedFor.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s (Entra enforces 5 minutes)"
                    : "Deactivate this role now";
                row.ShowExtend = lockedFor <= TimeSpan.Zero && ExtendWindow.CanExtend(assignment, policy, now);
                row.SelectEnabled = false;
                break;
            }

            case AssignmentStatusKind.Scheduled:
                row.Status = RowStatus.Scheduled;
                row.StatusText = $"starts in {Countdown.Until(assignment.StartDateTime, now)}";
                row.ShowCancel = true;
                row.SelectEnabled = false;
                break;
            case AssignmentStatusKind.PendingApproval:
                row.Status = RowStatus.Pending;
                row.StatusText = "awaiting approval";
                row.ShowCancelPending = true;
                row.SelectEnabled = false;
                break;
            case AssignmentStatusKind.PendingProvisioning:
                row.Status = RowStatus.Provisioning;
                row.StatusText = "provisioning";
                row.SelectEnabled = false;
                break;
            case AssignmentStatusKind.Failed:
                row.Status = RowStatus.Failed;
                row.FailedText = assignment.Status.FailureReason;
                row.SelectEnabled = viewOnlyReason is null;
                break;
            default:
                row.Status = RowStatus.None;
                row.ShowActivate = allowActivate && viewOnlyReason is null;
                row.SelectEnabled = viewOnlyReason is null;
                break;
        }

        row.RaiseAll();
    }

    private static string Initials(Identity identity)
    {
        var source = string.IsNullOrWhiteSpace(identity.DisplayName) || identity.DisplayName == identity.Upn
            ? identity.Upn.Split('@')[0].Replace('.', ' ')
            : identity.DisplayName;
        var parts = source.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
        return initials.Length == 0 ? "?" : initials;
    }

    /// <summary>Merges <paramref name="desired"/> into <paramref name="target"/> by key, keeping existing objects (and the list's scroll position).</summary>
    public static void Reconcile(ObservableCollection<PanelGroup> target, IReadOnlyList<PanelGroup> desired)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);
        ReconcileList(target, desired, g => g.Key, (existing, fresh) =>
        {
            existing.CopyFrom(fresh);
            ReconcileList(existing, fresh, i => i.Key, (e, f) =>
            {
                switch (e)
                {
                    case RoleRow row when f is RoleRow freshRow:
                        row.CopyFrom(freshRow);
                        break;
                    case NoteRow note when f is NoteRow freshNote:
                        note.Text = freshNote.Text;
                        break;
                    case ApprovalRow approval when f is ApprovalRow freshApproval:
                        approval.CopyFrom(freshApproval);
                        break;
                    default:
                        break;
                }
            });
        });
    }

    private static void ReconcileList<T>(ObservableCollection<T> target, IReadOnlyList<T> desired, Func<T, string> key, Action<T, T> update)
        where T : class
    {
        // Drop what is gone.
        var wanted = desired.Select(key).ToHashSet(StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(key(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        // Walk the desired order, inserting or moving into place.
        for (var i = 0; i < desired.Count; i++)
        {
            var k = key(desired[i]);
            var at = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (key(target[j]) == k)
                {
                    at = j;
                    break;
                }
            }

            if (at < 0)
            {
                target.Insert(i, desired[i]);
            }
            else
            {
                if (at != i)
                {
                    target.Move(at, i);
                }

                update(target[i], desired[i]);
            }
        }
    }
}
