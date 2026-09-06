using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>
/// Single and bulk activation in one window. Duration is a ComboBox in 30-minute steps capped at
/// the policy maximum, the reason is prefilled from the last activation, ticket fields appear only
/// when a policy demands them, and the Status column shows each row's outcome in place. Port of
/// the macOS <c>ActivationView</c>.
/// </summary>
public sealed partial class ActivationWindow : Window
{
    private sealed class Item
    {
        public required EligibleRole Role { get; init; }

        public required DurationPicker Duration { get; init; }

        public TextBlock? Status { get; init; }

        public ProgressRing? Ring { get; init; }
    }

    private readonly AppModel _model;
    private readonly IReadOnlyList<RoleKey> _keys;
    private readonly List<Item> _items = [];
    private bool _running;

    public ActivationWindow(AppModel model, IReadOnlyList<RoleKey> keys)
    {
        InitializeComponent();
        _model = model;
        _keys = keys;
        var isBulk = keys.Count > 1;
        DialogWindows.Configure(this, isBulk ? $"Activate {keys.Count} roles" : "Activate role", isBulk ? 560 : 420, isBulk ? 520 : 440, Root, autoHeight: true);
        DialogWindows.DefaultButton(Root, SubmitButton);
        Load();
        _model.Changed += OnModelChanged;
        Closed += (_, _) => _model.Changed -= OnModelChanged;
    }

    public IReadOnlyList<RoleKey> Keys => _keys;

    private bool IsBulk => _keys.Count > 1;

    /// <summary>
    /// Extend re-activates by deactivating first, so a scheduled start would revoke access now and
    /// hand it back later. The row is hidden, and scheduling forced off, whenever anything in the
    /// window is already active.
    /// </summary>
    private bool HasActiveItem => _items.Any(i => _model.Assignment(i.Role.Key)?.Status.Kind == AssignmentStatusKind.Active);

    private bool NeedsTicket => _items.Any(i => i.Role.Policy.RequiresTicket);

    private bool JustificationRequired => _items.Any(i => i.Role.Policy.RequiresJustification);

    private bool IsScheduling => ScheduleToggle.IsOn && !HasActiveItem;

    private DateTimeOffset StartAt
    {
        get
        {
            var date = StartDate.Date;
            return new DateTimeOffset(date.Year, date.Month, date.Day, StartTime.Time.Hours, StartTime.Time.Minutes, 0, DateTimeOffset.Now.Offset);
        }
    }

    private bool CanSubmit =>
        !_running && _items.Count > 0
        && (!JustificationRequired || !string.IsNullOrWhiteSpace(Reason.Text))
        && (!NeedsTicket || !string.IsNullOrWhiteSpace(TicketNumber.Text))
        // A start that has slipped into the past while the window sat open is not submittable.
        && !(IsScheduling && StartAt <= DateTimeOffset.Now)
        && _model.IsOnline;

    private void Load()
    {
        foreach (var key in _keys)
        {
            if (_model.Role(key) is not { } role)
            {
                continue;
            }

            var remembered = _model.Remembered(key)?.LastDuration;
            var duration = remembered is { } r && r < role.Policy.DefaultDuration ? r : role.Policy.DefaultDuration;
            if (duration > role.Policy.MaximumDuration)
            {
                duration = role.Policy.MaximumDuration;
            }

            var picker = IsBulk ? new DurationPicker { Maximum = role.Policy.MaximumDuration } : SingleDuration;
            picker.Maximum = role.Policy.MaximumDuration;
            picker.Duration = remembered ?? role.Policy.DefaultDuration;
            _items.Add(new Item { Role = role, Duration = picker, Status = IsBulk ? new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center } : null, Ring = IsBulk ? new ProgressRing { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed } : null });
        }

        Heading.Text = IsBulk ? $"Activate {_keys.Count} roles" : _items.FirstOrDefault()?.Role.DisplayName ?? "Activate role";
        var tenantKeys = _items.Select(i => i.Role.Key.TenantKey).Distinct().ToList();
        Subtitle.Text = tenantKeys.Count == 1
            ? $"{_model.Tenant(tenantKeys[0])?.DisplayName ?? tenantKeys[0].TenantId} · {_model.Identity(tenantKeys[0].IdentityId)?.Upn ?? tenantKeys[0].IdentityId}"
            : $"{tenantKeys.Count} tenants";
        Reason.Text = _keys.Select(k => _model.Remembered(k)?.Justification).FirstOrDefault(j => !string.IsNullOrEmpty(j)) ?? string.Empty;
        TicketRow.Visibility = NeedsTicket ? Visibility.Visible : Visibility.Collapsed;
        ScheduleRow.Visibility = HasActiveItem ? Visibility.Collapsed : Visibility.Visible;
        var start = DateTimeOffset.Now.AddHours(1);
        StartDate.Date = start;
        StartTime.Time = new TimeSpan(start.Hour, start.Minute - start.Minute % 5, 0);
        SubmitButton.Content = IsBulk ? "Activate all" : "Activate";
        _model.ClearProgress(_keys);

        if (IsBulk)
        {
            SinglePane.Visibility = Visibility.Collapsed;
            BulkPane.Visibility = Visibility.Visible;
            BuildBulkRows(tenantKeys);
        }
        else if (_items.Count > 0)
        {
            var role = _items[0].Role;
            SingleApproval.Visibility = role.Policy.RequiresApproval ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateSubmit();
    }

    private void BuildBulkRows(IReadOnlyList<TenantKey> tenantKeys)
    {
        BulkRows.Children.Clear();
        BulkRows.Children.Add(HeaderRow());
        foreach (var tenantKey in tenantKeys)
        {
            var caption = new TextBlock
            {
                Text = $"{_model.Identity(tenantKey.IdentityId)?.Upn ?? tenantKey.IdentityId} · {_model.Tenant(tenantKey)?.DisplayName ?? tenantKey.TenantId}",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Padding = new Thickness(10, 8, 10, 4),
            };
            BulkRows.Children.Add(caption);
            foreach (var item in _items.Where(i => i.Role.Key.TenantKey == tenantKey))
            {
                BulkRows.Children.Add(Row(item));
            }
        }
    }

    private static Grid HeaderRow()
    {
        var grid = RowGrid();
        grid.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerOnMicaBaseAltFillColorSecondaryBrush"];
        grid.Children.Add(Cell("Role", 0));
        grid.Children.Add(Cell("Duration", 1));
        grid.Children.Add(Cell("Status", 2));
        return grid;
    }

    private static TextBlock Cell(string text, int column)
    {
        var block = new TextBlock { Text = text, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        block.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        Grid.SetColumn(block, column);
        return block;
    }

    private static Grid RowGrid()
    {
        var grid = new Grid { Padding = new Thickness(10, 6, 10, 6), ColumnSpacing = 10, MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        grid.BorderThickness = new Thickness(0, 1, 0, 0);
        return grid;
    }

    private Grid Row(Item item)
    {
        var grid = RowGrid();
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = item.Role.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis });
        var captionParts = new List<string> { Kind(item.Role.Key) };
        if (item.Role.Policy.RequiresApproval)
        {
            captionParts.Add("Requires approval");
        }

        captionParts.Add($"max {DurationPicker.Label(item.Role.Policy.MaximumDuration)}");
        name.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", captionParts),
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        grid.Children.Add(name);
        Grid.SetColumn(item.Duration, 1);
        item.Duration.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(item.Duration);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(item.Ring!);
        status.Children.Add(item.Status!);
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);
        UpdateStatus(item);
        return grid;
    }

    private static string Kind(RoleKey key) => key.Scope.Kind switch
    {
        RoleScopeKind.EntraDirectory => "Entra directory role",
        RoleScopeKind.AzureResource => "Azure resource role",
        _ => "Group",
    };

    private void UpdateStatus(Item item)
    {
        if (item.Status is null || item.Ring is null)
        {
            return;
        }

        var resources = Application.Current.Resources;
        var progress = _model.Progress.GetValueOrDefault(item.Role.Key);
        item.Ring.Visibility = Visibility.Collapsed;
        item.Ring.IsActive = false;
        ToolTipService.SetToolTip(item.Status, null);
        switch (progress)
        {
            case ActivationResult.Activated:
                item.Status.Text = "Active";
                item.Status.Foreground = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorSuccessBrush"];
                break;
            case ActivationResult.Scheduled:
                item.Status.Text = "Scheduled";
                item.Status.Foreground = (Microsoft.UI.Xaml.Media.Brush)resources["AccentFillColorDefaultBrush"];
                break;
            case ActivationResult.PendingApproval:
                item.Status.Text = "Pending approval";
                item.Status.Foreground = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorCautionBrush"];
                break;
            case ActivationResult.Failed failed:
                item.Status.Text = failed.Error.UserMessage;
                item.Status.Foreground = (Microsoft.UI.Xaml.Media.Brush)resources["SystemFillColorCriticalBrush"];
                item.Status.TextTrimming = TextTrimming.CharacterEllipsis;
                ToolTipService.SetToolTip(item.Status, failed.Error.UserMessage);
                break;
            default:
                if (_running || _model.InFlight.Contains(item.Role.Key))
                {
                    item.Status.Text = string.Empty;
                    item.Ring.Visibility = Visibility.Visible;
                    item.Ring.IsActive = true;
                }
                else
                {
                    item.Status.Text = item.Role.Policy.RequiresApproval ? "Approval" : "Ready";
                    item.Status.Foreground = (Microsoft.UI.Xaml.Media.Brush)resources[item.Role.Policy.RequiresApproval ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"];
                }

                break;
        }
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        foreach (var item in _items)
        {
            UpdateStatus(item);
        }

        if (!IsBulk && _items.Count > 0 && _model.Progress.GetValueOrDefault(_items[0].Role.Key) is ActivationResult.Failed failed)
        {
            SingleError.Message = failed.Error.UserMessage;
            SingleError.IsOpen = true;
        }

        UpdateSubmit();
    }

    private void UpdateSubmit()
    {
        SubmitButton.IsEnabled = CanSubmit;
        CancelButton.IsEnabled = !_running;
        Running.IsActive = _running;
        Running.Visibility = _running ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e) => UpdateSubmit();

    private void OnScheduleToggled(object sender, RoutedEventArgs e)
    {
        var on = ScheduleToggle.IsOn;
        StartDate.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        StartTime.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateSubmit();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        if (!CanSubmit)
        {
            return;
        }

        _running = true;
        SingleError.IsOpen = false;
        Failure.IsOpen = false;
        UpdateSubmit();
        OnModelChanged(this, EventArgs.Empty);
        var ticket = NeedsTicket && !string.IsNullOrWhiteSpace(TicketNumber.Text)
            ? new TicketInfo(TicketNumber.Text.Trim(), TicketSystem.Text.Trim())
            : null;
        // Two minutes of headroom: a start the service sees as "now" would activate immediately.
        DateTimeOffset? start = IsScheduling ? Max(StartAt, DateTimeOffset.Now.AddMinutes(2)) : null;
        var requests = _items.Select(i => new ActivationRequest(
            i.Role.Key, i.Duration.Duration, Reason.Text.Trim(), ticket, i.Role.Policy.AuthenticationContext, start)).ToList();
        try
        {
            await _model.ActivateAsync(requests);
        }
        catch (Exception ex)
        {
            Failure.Message = ex.Message;
            Failure.IsOpen = true;
        }

        _running = false;
        UpdateSubmit();
        OnModelChanged(this, EventArgs.Empty);
        var allOk = requests.All(r => _model.Progress.GetValueOrDefault(r.RoleKey) is not ActivationResult.Failed);
        if (allOk)
        {
            Close();
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
