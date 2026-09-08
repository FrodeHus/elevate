using System.Collections.ObjectModel;
using Elevate.App.Shell;
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

/// <summary>One line in the access packages window: a package, a request or an assignment.</summary>
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
/// Per-tenant access packages: what can be requested, what is pending, held or declined. Port of
/// the macOS <c>AccessPackagesView</c>. The lists come from the model's stored snapshot; the
/// requestable packages are fetched by the window and held only while it is open.
/// </summary>
public sealed partial class AccessPackagesWindow : Window
{
    private enum Tab
    {
        Available,
        Requested,
        Assigned,
        Declined,
    }

    private readonly AppModel _model;
    private readonly TenantKey _key;
    private readonly ObservableCollection<PackageRow> _rows = [];
    private readonly HashSet<string> _cancelling = [];
    private IReadOnlyList<AccessPackage> _packages = [];
    private string? _packagesError;
    private bool _loadingPackages;
    private string? _actionError;
    private Tab _tab = Tab.Available;

    public AccessPackagesWindow(AppModel model, TenantKey key)
    {
        InitializeComponent();
        _model = model;
        _key = key;
        var tenant = model.Tenant(key)?.DisplayName ?? key.TenantId;
        DialogWindows.Configure(this, $"Access packages in {tenant}", 560, 520, Root);
        List.ItemsSource = _rows;
        model.Changed += OnModelChanged;
        Closed += (_, _) => model.Changed -= OnModelChanged;
        Update();
        _ = ReloadAsync();
    }

    public TenantKey TenantKey => _key;

    private void OnModelChanged(object? sender, EventArgs e) => Update();

    private AccessPackageSnapshot Snapshot => _model.AccessPackageSnapshot(_key) ?? new AccessPackageSnapshot();

    private string Query => Search.Text;

    private bool Matches(string name, string? detail) =>
        !PanelFilter.IsActive(Query) || PanelFilter.Matches(Query, name) || (detail is not null && PanelFilter.Matches(Query, detail));

    // MARK: Rows

    private void Update()
    {
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
            var (rows, caption) = _tab switch
            {
                Tab.Available => AvailableRows(),
                Tab.Requested => RequestedRows(),
                Tab.Assigned => AssignedRows(),
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
        _actionError = null;
        _loadingPackages = true;
        Update();
        var poll = _model.PollAccessPackagesAsync(_key);
        try
        {
            _packages = await _model.RequestablePackagesAsync(_key);
            _packagesError = null;
        }
        catch (Exception e)
        {
            _packagesError = AppModel.Describe(e);
        }
        finally
        {
            _loadingPackages = false;
        }

        await poll;
        Update();
    }

    private async Task CancelAsync(PackageRow row)
    {
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
        finally
        {
            _cancelling.Remove(row.Id);
        }

        Update();
    }

    // MARK: Handlers

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _tab = sender.SelectedItem == RequestedTab ? Tab.Requested
            : sender.SelectedItem == AssignedTab ? Tab.Assigned
            : sender.SelectedItem == DeclinedTab ? Tab.Declined
            : Tab.Available;
        Update();
        _ = _model.PollAccessPackagesAsync(_key);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Update();

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

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
                App.Current.OpenRequestPackage(_key, package, () =>
                {
                    Tabs.SelectedItem = RequestedTab;
                    DialogWindows.Front(this);
                });
                break;
            case PackageRowAction.Cancel:
                _ = CancelAsync(row);
                break;
            default:
                break;
        }
    }
}
