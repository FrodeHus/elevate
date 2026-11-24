using System.Collections.ObjectModel;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>What the one button on a package row does.</summary>
public enum PackageRowAction
{
    None,
    Request,
    RequestAgain,
    Cancel,
}

/// <summary>One line in the access packages view: a package, a request or an assignment.</summary>
public sealed class PackageRow
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Detail { get; init; }

    /// <summary>The state pill's text; empty for a package that can simply be requested.</summary>
    public string StateText { get; init; } = string.Empty;

    /// <summary>"Caution" for pending, "Success" for delivered, "Critical" for denied or failed, "Neutral" for canceled.</summary>
    public string StateTint { get; init; } = "Neutral";

    public PackageRowAction Action { get; init; }

    /// <summary>The package behind Request and Request again.</summary>
    public AccessPackage? Package { get; init; }

    public bool ButtonEnabled { get; init; } = true;

    public Visibility DetailVisibility => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility StateVisibility => StateText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ButtonVisibility => Action == PackageRowAction.None ? Visibility.Collapsed : Visibility.Visible;

    public string ButtonText => Action switch
    {
        PackageRowAction.Request => "Request",
        PackageRowAction.RequestAgain => "Request again",
        PackageRowAction.Cancel => ButtonEnabled ? "Cancel request" : "Cancelling…",
        _ => string.Empty,
    };

    /// <summary>Request is the one prominent control; the rest stay quiet.</summary>
    public Style ButtonStyle => (Style)Application.Current.Resources[Action == PackageRowAction.Request ? "SmallAccentButtonStyle" : "SmallButtonStyle"];

    // MARK: Text

    public static string Label(AccessPackageRequestState s) => s switch
    {
        AccessPackageRequestState.Submitted => "submitted",
        AccessPackageRequestState.PendingApproval => "pending approval",
        AccessPackageRequestState.Delivering => "delivering",
        AccessPackageRequestState.Delivered => "delivered",
        AccessPackageRequestState.DeliveryFailed => "delivery failed",
        AccessPackageRequestState.Denied => "denied",
        AccessPackageRequestState.Scheduled => "scheduled",
        AccessPackageRequestState.Canceled => "canceled",
        AccessPackageRequestState.PartiallyDelivered => "partially delivered",
        _ => "unknown",
    };

    public static string Tint(AccessPackageRequestState s) => s switch
    {
        AccessPackageRequestState.Submitted or AccessPackageRequestState.PendingApproval or AccessPackageRequestState.Scheduled => "Caution",
        AccessPackageRequestState.Delivering or AccessPackageRequestState.Delivered or AccessPackageRequestState.PartiallyDelivered => "Success",
        AccessPackageRequestState.Denied or AccessPackageRequestState.DeliveryFailed => "Critical",
        _ => "Neutral",
    };

    public static bool ExpiresSoon(AccessPackageAssignment a, DateTimeOffset now) =>
        a.ExpiresAt is { } end && end - now < TimeSpan.FromDays(7);

    public static string RequestCaption(AccessPackageRequest r)
    {
        var parts = new List<string>();
        if (r.CreatedAt is { } d)
        {
            parts.Add("Requested " + d.ToLocalTime().ToString("d MMM yyyy HH:mm"));
        }

        if (!string.IsNullOrEmpty(r.Justification))
        {
            parts.Add("“" + r.Justification + "”");
        }

        return string.Join(" · ", parts);
    }

    public static string AssignmentCaption(AccessPackageAssignment a)
    {
        var parts = new List<string> { a.ExpiresAt is { } end ? "Expires " + end.ToLocalTime().ToString("d MMM yyyy") : "No expiry" };
        if (!string.IsNullOrEmpty(a.PolicyName))
        {
            parts.Add("Policy: " + a.PolicyName);
        }

        return string.Join(" · ", parts);
    }

    public static string DeclinedCaption(AccessPackageRequest r)
    {
        var parts = new List<string>();
        if ((r.CompletedAt ?? r.CreatedAt) is { } d)
        {
            var verb = r.State switch
            {
                AccessPackageRequestState.Canceled => "Canceled",
                AccessPackageRequestState.DeliveryFailed => "Failed",
                _ => "Decided",
            };
            parts.Add(verb + " " + d.ToLocalTime().ToString("d MMM yyyy HH:mm"));
        }

        if (r.State == AccessPackageRequestState.Denied && !string.IsNullOrEmpty(r.Justification))
        {
            parts.Add("Your reason: “" + r.Justification + "”");
        }

        if (r.State != AccessPackageRequestState.Denied && !string.IsNullOrEmpty(r.Status))
        {
            parts.Add(r.Status);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// What (if anything) to show in place of the rows: null when there are rows, the tab's own
    /// empty caption when there is nothing to filter, or a "no matches" caption when the search
    /// narrowed a non-empty collection down to nothing.
    /// </summary>
    public static string? EmptyCaption(int total, int filtered, string query, string emptyText)
    {
        if (filtered > 0)
        {
            return null;
        }

        if (total == 0 || !PanelFilter.IsActive(query))
        {
            return emptyText;
        }

        return $"No matches for “{query.Trim()}”.";
    }
}

/// <summary>
/// Per-tenant access packages in the flyout's main column: what can be requested, what is pending,
/// held or declined. Port of the macOS <c>AccessPackagesView</c>. The lists come from the model's
/// stored snapshot; the requestable packages are fetched here and held only while the view is bound.
/// The tab lives in the model, so it survives a trip through the request form.
/// </summary>
public sealed partial class AccessPackagesContent : UserControl
{
    private AppModel _model = null!;
    private bool _bound;
    private TenantKey _key = new(string.Empty, string.Empty);
    private readonly ObservableCollection<PackageRow> _rows = [];
    private readonly HashSet<string> _cancelling = [];
    private IReadOnlyList<AccessPackage> _packages = [];
    private string? _packagesError;
    private bool _loadingPackages;
    private string? _actionError;
    private bool _syncingTab;
    // Bumped by every Bind and Unbind, so a read that finishes after the view moved on is dropped.
    private int _version;

    public AccessPackagesContent()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
    }

    /// <summary>Starts showing this tenant's packages and reads them afresh.</summary>
    public void Bind(AppModel model, TenantKey key)
    {
        Unbind();
        _model = model;
        _bound = true;
        _key = key;
        Search.Text = string.Empty;
        model.Changed += OnModelChanged;
        Update();
        _ = ReloadAsync();
    }

    /// <summary>Stops listening to the model and drops what was read.</summary>
    public void Unbind()
    {
        _version++;
        if (_bound)
        {
            _model.Changed -= OnModelChanged;
        }

        _bound = false;
        _packages = [];
        _packagesError = null;
        _loadingPackages = false;
        _actionError = null;
        _cancelling.Clear();
        _rows.Clear();
    }

    private void OnModelChanged(object? sender, EventArgs e) => Update();

    private AccessPackageSnapshot Snapshot => _model.AccessPackageSnapshot(_key) ?? new AccessPackageSnapshot();

    private string Query => Search.Text;

    private bool Matches(string name, string? detail) =>
        !PanelFilter.IsActive(Query) || PanelFilter.Matches(Query, name) || (detail is not null && PanelFilter.Matches(Query, detail));

    // MARK: Rows

    private void Update()
    {
        if (!_bound)
        {
            return;
        }

        SyncTabs();
        var consentError = _model.AccessPackageConsentError(_key)
            ?? (_packagesError is not null && _packagesError == AppModel.ConsentMessage ? _packagesError : null);
        Consent.Visibility = consentError is null ? Visibility.Collapsed : Visibility.Visible;
        List.Visibility = consentError is null ? Visibility.Visible : Visibility.Collapsed;
        if (consentError is not null)
        {
            ConsentDetail.Text = consentError;
            ConsentLink.Visibility = _model.AdminConsentUrl(_key.IdentityId, _key.TenantId) is null ? Visibility.Collapsed : Visibility.Visible;
            Empty.Visibility = Visibility.Collapsed;
        }
        else
        {
            var (rows, caption) = _model.AccessPackagesTab switch
            {
                AccessPackageTab.Available => AvailableRows(),
                AccessPackageTab.Requested => RequestedRows(),
                AccessPackageTab.Assigned => AssignedRows(),
                _ => DeclinedRows(),
            };
            Replace(rows);
            Empty.Text = caption ?? string.Empty;
            Empty.Visibility = caption is null ? Visibility.Collapsed : Visibility.Visible;
        }

        var pollError = _model.AccessPackageErrors.GetValueOrDefault(_key);
        var error = _actionError ?? (pollError is not null && pollError != AppModel.ConsentMessage ? pollError : null);
        Error.Message = error ?? string.Empty;
        Error.IsOpen = error is not null;

        Updated.Text = _model.AccessPackagesPolledAt(_key) is { } at ? "Updated " + PanelListBuilder.Relative(at, DateTimeOffset.UtcNow) : "Not updated yet";
        var working = _loadingPackages || _model.AccessPackagesPolling.Contains(_key);
        Working.IsActive = working;
        Working.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !working;
    }

    /// <summary>Replaces the rows wholesale; the lists are short and a full swap keeps the code simple.</summary>
    private void Replace(IReadOnlyList<PackageRow> rows)
    {
        if (_rows.Count == rows.Count && _rows.Zip(rows).All(p => Same(p.First, p.Second)))
        {
            return;
        }

        _rows.Clear();
        foreach (var row in rows)
        {
            _rows.Add(row);
        }
    }

    private static bool Same(PackageRow a, PackageRow b) =>
        a.Id == b.Id && a.Name == b.Name && a.Detail == b.Detail && a.StateText == b.StateText && a.StateTint == b.StateTint
        && a.Action == b.Action && a.ButtonEnabled == b.ButtonEnabled;

    private (IReadOnlyList<PackageRow> Rows, string? Caption) AvailableRows()
    {
        var snapshot = Snapshot;
        var rows = new List<PackageRow>();
        foreach (var package in _packages.Where(p => Matches(p.DisplayName, p.Description)))
        {
            var pending = snapshot.Requests.FirstOrDefault(r => r.PackageId == package.Id && r.State.IsOpen());
            var held = snapshot.Assignments.Any(a => a.PackageId == package.Id && a.State == AccessPackageAssignmentState.Delivered);
            rows.Add(new PackageRow
            {
                Id = package.Id,
                Name = package.DisplayName,
                Detail = package.Description,
                StateText = pending is not null ? PackageRow.Label(pending.State) : held ? "assigned" : string.Empty,
                StateTint = pending is not null ? PackageRow.Tint(pending.State) : "Success",
                Action = pending is null && !held ? PackageRowAction.Request : PackageRowAction.None,
                Package = package,
            });
        }

        string? caption;
        if (_packagesError is not null)
        {
            caption = _packagesError;
        }
        else if (_loadingPackages && _packages.Count == 0)
        {
            caption = null;
        }
        else
        {
            caption = PackageRow.EmptyCaption(_packages.Count, rows.Count, Query, "No access packages are available for you to request.");
        }

        return (rows, caption);
    }

    private (IReadOnlyList<PackageRow> Rows, string? Caption) RequestedRows()
    {
        var open = Snapshot.Requests.Where(r => r.State.IsOpen()).OrderByDescending(r => r.CreatedAt ?? DateTimeOffset.MinValue).ToList();
        var rows = open.Where(r => Matches(r.PackageName, r.Justification)).Select(r => new PackageRow
        {
            Id = r.Id,
            Name = r.PackageName,
            Detail = PackageRow.RequestCaption(r),
            StateText = PackageRow.Label(r.State),
            StateTint = PackageRow.Tint(r.State),
            Action = r.State.IsCancellable() ? PackageRowAction.Cancel : PackageRowAction.None,
            ButtonEnabled = !_cancelling.Contains(r.Id),
        }).ToList();
        return (rows, PackageRow.EmptyCaption(open.Count, rows.Count, Query, "No requests in progress."));
    }

    private (IReadOnlyList<PackageRow> Rows, string? Caption) AssignedRows()
    {
        var now = DateTimeOffset.UtcNow;
        var held = Snapshot.Assignments.Where(a => a.State == AccessPackageAssignmentState.Delivered).ToList();
        var rows = held.Where(a => Matches(a.PackageName, a.PolicyName)).Select(a =>
        {
            var soon = PackageRow.ExpiresSoon(a, now);
            var package = soon ? _packages.FirstOrDefault(p => p.Id == a.PackageId) : null;
            return new PackageRow
            {
                Id = a.Id,
                Name = a.PackageName,
                Detail = PackageRow.AssignmentCaption(a),
                StateText = soon ? "expires soon" : "delivered",
                StateTint = soon ? "Caution" : "Success",
                Action = package is null ? PackageRowAction.None : PackageRowAction.RequestAgain,
                Package = package,
            };
        }).ToList();
        return (rows, PackageRow.EmptyCaption(held.Count, rows.Count, Query, "No access packages are assigned to you."));
    }

    private (IReadOnlyList<PackageRow> Rows, string? Caption) DeclinedRows()
    {
        var ended = Snapshot.Requests.Where(r => r.State.IsDeclined())
            .OrderByDescending(r => r.CompletedAt ?? r.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();
        var rows = ended.Where(r => Matches(r.PackageName, r.Justification)).Select(r =>
        {
            var package = r.State == AccessPackageRequestState.Canceled ? null : _packages.FirstOrDefault(p => p.Id == r.PackageId);
            return new PackageRow
            {
                Id = r.Id,
                Name = r.PackageName,
                Detail = PackageRow.DeclinedCaption(r),
                StateText = PackageRow.Label(r.State),
                StateTint = PackageRow.Tint(r.State),
                Action = package is null ? PackageRowAction.None : PackageRowAction.RequestAgain,
                Package = package,
            };
        }).ToList();
        return (rows, PackageRow.EmptyCaption(ended.Count, rows.Count, Query, "No denied, failed or canceled requests."));
    }

    // MARK: Loading

    /// <summary>A forced poll of the stored lists and a fresh read of the requestable packages, side by side.</summary>
    private async Task ReloadAsync()
    {
        var version = _version;
        _actionError = null;
        _loadingPackages = true;
        Update();
        var poll = _model.PollAccessPackagesAsync(_key);
        try
        {
            var packages = await _model.RequestablePackagesAsync(_key);
            if (version != _version)
            {
                return;
            }

            _packages = packages;
            _packagesError = null;
        }
        catch (Exception e)
        {
            if (version != _version)
            {
                return;
            }

            _packagesError = AppModel.Describe(e);
        }

        _loadingPackages = false;
        await poll;
        if (version == _version)
        {
            Update();
        }
    }

    private async Task CancelAsync(PackageRow row)
    {
        var version = _version;
        _cancelling.Add(row.Id);
        Update();
        try
        {
            await _model.CancelPackageRequestAsync(_key, row.Id);
            _actionError = null;
        }
        catch (Exception e)
        {
            _actionError = $"Could not cancel {row.Name}: {AppModel.Describe(e)}";
        }

        if (version != _version)
        {
            return;
        }

        _cancelling.Remove(row.Id);
        Update();
    }

    // MARK: Handlers

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingTab || !_bound)
        {
            return;
        }

        // The model notifies, which redraws through Update.
        _model.AccessPackagesTab = sender.SelectedItem == RequestedTab ? AccessPackageTab.Requested
            : sender.SelectedItem == AssignedTab ? AccessPackageTab.Assigned
            : sender.SelectedItem == DeclinedTab ? AccessPackageTab.Declined
            : AccessPackageTab.Available;
        _ = _model.PollAccessPackagesAsync(_key);
    }

    /// <summary>The model owns the tab; the bar follows it without raising a change of its own.</summary>
    private void SyncTabs()
    {
        var wanted = _model.AccessPackagesTab switch
        {
            AccessPackageTab.Requested => RequestedTab,
            AccessPackageTab.Assigned => AssignedTab,
            AccessPackageTab.Declined => DeclinedTab,
            _ => AvailableTab,
        };
        if (Tabs.SelectedItem != wanted)
        {
            _syncingTab = true;
            Tabs.SelectedItem = wanted;
            _syncingTab = false;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_bound)
        {
            Update();
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private void OnConsentLink(object sender, RoutedEventArgs e)
    {
        if (_model.AdminConsentUrl(_key.IdentityId, _key.TenantId) is { } url)
        {
            _ = Windows.System.Launcher.LaunchUriAsync(url);
        }
    }

    private void OnRowButton(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PackageRow row)
        {
            return;
        }

        switch (row.Action)
        {
            case PackageRowAction.Request or PackageRowAction.RequestAgain when row.Package is { } package:
                _model.BeginPackageRequest(package);
                break;
            case PackageRowAction.Cancel:
                _ = CancelAsync(row);
                break;
            default:
                break;
        }
    }
}
