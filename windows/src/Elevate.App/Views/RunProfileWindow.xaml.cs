using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Elevate.App.Views;

/// <summary>
/// Plans and runs one profile: every entry grouped by account and tenant, a duration picker per
/// activatable entry, the shared reason, ticket and start, and per-row outcomes. Port of the macOS
/// <c>RunProfileView</c>.
/// </summary>
public sealed partial class RunProfileWindow : Window
{
    private sealed class Row
    {
        public required ProfilePlanItem Item { get; set; }

        public DurationPicker? Duration { get; init; }

        public TextBlock? Status { get; init; }

        public ProgressRing? Ring { get; init; }
    }

    private readonly AppModel _model;
    private readonly Guid _profileId;
    private readonly List<Row> _rows = [];
    private bool _running;
    private bool _finished;
    private int _seenRunRequest;

    public RunProfileWindow(AppModel model, Guid profileId)
    {
        InitializeComponent();
        _model = model;
        _profileId = profileId;
        DialogWindows.Configure(this, "Run profile", 600, 560, Root, autoHeight: true);
        DialogWindows.DefaultButton(Root, SubmitButton);
        _seenRunRequest = model.RunRequests.GetValueOrDefault(profileId);
        Load();
        _model.Changed += OnModelChanged;
        Closed += (_, _) => _model.Changed -= OnModelChanged;
    }

    public Guid ProfileId => _profileId;

    private ActivationProfile? Profile => _model.Profile(_profileId);

    private IEnumerable<Row> ToActivate => _rows.Where(r => r.Item.Disposition == ProfilePlanDisposition.Activate);

    private bool NeedsTicket => ToActivate.Any(r => r.Item.Role?.Policy.RequiresTicket == true);

    private bool JustificationRequired => ToActivate.Any(r => r.Item.Role?.Policy.RequiresJustification == true);

    private DateTimeOffset StartAt
    {
        get
        {
            var date = StartDate.Date;
            return new DateTimeOffset(date.Year, date.Month, date.Day, StartTime.Time.Hours, StartTime.Time.Minutes, 0, DateTimeOffset.Now.Offset);
        }
    }

    private bool CanSubmit =>
        !_running && !_finished && ToActivate.Any()
        && (!JustificationRequired || !string.IsNullOrWhiteSpace(Reason.Text))
        && (!NeedsTicket || !string.IsNullOrWhiteSpace(TicketNumber.Text))
        // A start that has slipped into the past while the window sat open is not submittable.
        && !(ScheduleToggle.IsOn && StartAt <= DateTimeOffset.Now)
        && _model.IsOnline;

    private void Load()
    {
        var items = _model.Plan(_profileId);
        if (_finished || string.IsNullOrEmpty(Reason.Text))
        {
            Reason.Text = Profile?.LastJustification ?? string.Empty;
        }

        _finished = false;
        // The window is reused: a stale toggle or a start time from the last run must not carry over.
        ScheduleToggle.IsOn = false;
        var start = DateTimeOffset.Now.AddHours(1);
        StartDate.Date = start;
        StartTime.Time = new TimeSpan(start.Hour, start.Minute - start.Minute % 5, 0);
        _model.ClearProgress(items.Select(i => i.RoleKey));

        _rows.Clear();
        foreach (var item in items)
        {
            var activate = item.Disposition == ProfilePlanDisposition.Activate;
            _rows.Add(new Row
            {
                Item = item,
                Duration = activate ? new DurationPicker { Maximum = item.Role?.Policy.MaximumDuration ?? RolePolicy.ManualDefault.MaximumDuration, Duration = item.Duration } : null,
                Status = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                Ring = activate ? new ProgressRing { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed } : null,
            });
        }

        Heading.Text = $"Run \"{Profile?.Name ?? "profile"}\"";
        TicketRow.Visibility = NeedsTicket ? Visibility.Visible : Visibility.Collapsed;
        Inputs.Visibility = Visibility.Visible;
        SubmitButton.Content = $"Activate {ToActivate.Count()}";
        Build();
        UpdateSubmit();
    }

    private void Build()
    {
        Rows.Children.Clear();
        var resources = Application.Current.Resources;
        var secondary = (Brush)resources["TextFillColorSecondaryBrush"];
        var divider = (Brush)resources["DividerStrokeColorDefaultBrush"];
        foreach (var tenantKey in _rows.Select(r => r.Item.RoleKey.TenantKey).Distinct())
        {
            Rows.Children.Add(new TextBlock
            {
                Text = $"{_model.Identity(tenantKey.IdentityId)?.Upn ?? tenantKey.IdentityId} · {_model.Tenant(tenantKey)?.DisplayName ?? tenantKey.TenantId}",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = secondary,
                Padding = new Thickness(10, 8, 10, 4),
            });
            foreach (var row in _rows.Where(r => r.Item.RoleKey.TenantKey == tenantKey))
            {
                var grid = new Grid { Padding = new Thickness(10, 6, 10, 6), ColumnSpacing = 10, MinHeight = 40, BorderBrush = divider, BorderThickness = new Thickness(0, 1, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                var name = new TextBlock
                {
                    Text = row.Item.Role?.DisplayName ?? _model.SummaryName(row.Item.RoleKey),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = row.Item.Disposition == ProfilePlanDisposition.Activate ? 1 : 0.6,
                };
                grid.Children.Add(name);
                if (row.Duration is { } picker)
                {
                    picker.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(picker, 1);
                    grid.Children.Add(picker);
                }

                var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                if (row.Ring is not null)
                {
                    status.Children.Add(row.Ring);
                }

                status.Children.Add(row.Status!);
                Grid.SetColumn(status, 2);
                grid.Children.Add(status);
                Rows.Children.Add(grid);
                UpdateStatus(row);
            }
        }
    }

    private void UpdateStatus(Row row)
    {
        var resources = Application.Current.Resources;
        var status = row.Status!;
        var secondary = (Brush)resources["TextFillColorSecondaryBrush"];
        status.Foreground = secondary;
        ToolTipService.SetToolTip(status, null);
        if (row.Ring is not null)
        {
            row.Ring.Visibility = Visibility.Collapsed;
            row.Ring.IsActive = false;
        }

        switch (row.Item.Disposition)
        {
            case ProfilePlanDisposition.AlreadyActive:
                status.Text = "already active · skipped";
                return;
            case ProfilePlanDisposition.Pending:
                status.Text = "pending · skipped";
                return;
            case ProfilePlanDisposition.NotEligible:
                status.Text = "not eligible · skipped";
                status.Foreground = (Brush)resources["SystemFillColorCautionBrush"];
                return;
            case ProfilePlanDisposition.NotLoaded:
                status.Text = "loading…";
                return;
            default:
                break;
        }

        if (_finished && row.Duration is { } picker)
        {
            picker.IsEnabled = false;
        }

        switch (_model.Progress.GetValueOrDefault(row.Item.RoleKey))
        {
            case ActivationResult.Activated:
                status.Text = "Active";
                status.Foreground = (Brush)resources["SystemFillColorSuccessBrush"];
                break;
            case ActivationResult.Scheduled:
                status.Text = "Scheduled";
                status.Foreground = (Brush)resources["AccentFillColorDefaultBrush"];
                break;
            case ActivationResult.PendingApproval:
                status.Text = "Pending approval";
                status.Foreground = (Brush)resources["SystemFillColorCautionBrush"];
                break;
            case ActivationResult.Failed failed:
                status.Text = failed.Error.UserMessage;
                status.Foreground = (Brush)resources["SystemFillColorCriticalBrush"];
                ToolTipService.SetToolTip(status, failed.Error.UserMessage);
                break;
            default:
                if (_running)
                {
                    status.Text = string.Empty;
                    if (row.Ring is not null)
                    {
                        row.Ring.Visibility = Visibility.Visible;
                        row.Ring.IsActive = true;
                    }
                }
                else if (row.Item.Role?.Policy.RequiresApproval == true)
                {
                    status.Text = "approval";
                }
                else if (row.Item.Role?.Policy.RequiresMfa == true)
                {
                    status.Text = "MFA";
                }
                else
                {
                    status.Text = string.Empty;
                }

                break;
        }
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        // Re-plan when the user asks to run this profile again, never merely because the model redrew.
        var request = _model.RunRequests.GetValueOrDefault(_profileId);
        if (request != _seenRunRequest && !_running)
        {
            _seenRunRequest = request;
            Load();
            return;
        }

        if (_model.Profile(_profileId) is null && !_running)
        {
            Close();
            return;
        }

        foreach (var row in _rows)
        {
            UpdateStatus(row);
        }

        UpdateSubmit();
    }

    private void UpdateSubmit()
    {
        SubmitButton.IsEnabled = CanSubmit;
        SubmitButton.Visibility = _finished ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = _finished ? "Done" : "Cancel";
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
        Failure.IsOpen = false;
        UpdateSubmit();
        foreach (var row in _rows)
        {
            if (row.Duration is { } picker)
            {
                row.Item = row.Item with { Duration = picker.Duration };
            }

            UpdateStatus(row);
        }

        var ticket = NeedsTicket && !string.IsNullOrWhiteSpace(TicketNumber.Text)
            ? new TicketInfo(TicketNumber.Text.Trim(), TicketSystem.Text.Trim())
            : null;
        // Two minutes of headroom: a start the service sees as "now" would activate immediately.
        DateTimeOffset? start = ScheduleToggle.IsOn ? (StartAt > DateTimeOffset.Now.AddMinutes(2) ? StartAt : DateTimeOffset.Now.AddMinutes(2)) : null;
        try
        {
            await _model.RunProfileAsync(_profileId, [.. _rows.Select(r => r.Item)], Reason.Text.Trim(), ticket, start);
        }
        catch (Exception ex)
        {
            Failure.Message = ex.Message;
            Failure.IsOpen = true;
        }

        _running = false;
        _finished = true;
        Heading.Text = $"Ran \"{Profile?.Name ?? "profile"}\"";
        Inputs.Visibility = Visibility.Collapsed;
        foreach (var row in _rows)
        {
            UpdateStatus(row);
        }

        UpdateSubmit();
    }
}
