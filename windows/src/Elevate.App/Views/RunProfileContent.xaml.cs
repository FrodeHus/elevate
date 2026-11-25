using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Elevate.App.Views;

/// <summary>
/// The run form for one profile, drawn from its <see cref="ProfileRunSession"/> in the app model:
/// every entry grouped by tenant, a duration picker per activatable entry, the shared reason,
/// ticket and start, and per-row outcomes. The run window hosts it at 600 wide, the flyout compact
/// at 380. Port of the macOS <c>ProfileRunContent</c>.
/// </summary>
public sealed partial class RunProfileContent : UserControl
{
    private sealed class Row
    {
        public required RoleKey Key { get; init; }

        public DurationPicker? Duration { get; init; }

        /// <summary>Present on activatable rows only; unchecking omits the role from this run alone.</summary>
        public CheckBox? Include { get; init; }

        public StackPanel? Name { get; set; }

        public required TextBlock Status { get; init; }

        public ActivationStatusIcon? Ring { get; init; }
    }

    private readonly List<Row> _rows = [];
    private AppModel? _model;
    private Guid _profileId;
    private bool _compact;
    private ProfileRunSession? _session;
    private string _layout = string.Empty;
    // True while the controls are being set from the session, so the handlers do not write it back.
    private bool _syncing;

    public RunProfileContent()
    {
        InitializeComponent();
        DialogWindows.DefaultButton(Root, SubmitButton);
        DialogWindows.ReasonFirst(Reason, SubmitButton);
        Loaded += (_, _) =>
        {
            Attach();
            Refresh();
        };
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Raised by Cancel and by Done; the host closes itself.</summary>
    public event EventHandler? Done;

    public void Bind(AppModel model, Guid profileId, bool compact)
    {
        ArgumentNullException.ThrowIfNull(model);
        Detach();
        _model = model;
        _profileId = profileId;
        _compact = compact;
        // The flyout scrolls the whole form; the run window caps the role list on its own.
        RowsScroll.MaxHeight = compact ? double.PositiveInfinity : 320;
        RowsScroll.VerticalScrollBarVisibility = compact ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _session = null;
        _layout = string.Empty;
        Attach();
        Refresh();
    }

    public void FocusReason()
    {
        Reason.Focus(FocusState.Programmatic);
        Reason.SelectAll();
    }

    private void Attach()
    {
        if (_model is not null)
        {
            _model.Changed -= OnModelChanged;
            _model.Changed += OnModelChanged;
        }
    }

    private void Detach()
    {
        if (_model is not null)
        {
            _model.Changed -= OnModelChanged;
        }
    }

    /// <summary>For good: a closing window calls it because Unloaded is not reliably raised for one, and a dead control must not redraw or touch the model.</summary>
    internal void Unbind()
    {
        Detach();
        _model = null;
        _session = null;
    }

    private void OnModelChanged(object? sender, EventArgs e) => Refresh();

    // Fixed columns: the picker and the status keep their width even when a row has nothing to
    // report, so the rows line up; "7 h 30 min" fits the picker without truncation.
    private const double DurationColumn = 130;
    private const double StatusColumn = 160;

    // A WinUI CheckBox with no content and zero padding is its 20 px glyph box.
    private const double CheckBoxGlyphWidth = 20;

    private void Refresh()
    {
        if (_model is null || _syncing)
        {
            return;
        }

        // The session is created here when the host did not: a flyout can bind before any run began.
        if (!_model.RunSessions.TryGetValue(_profileId, out var session))
        {
            if (_model.Profile(_profileId) is null)
            {
                return;
            }

            session = _model.RunSession(_profileId);
        }

        _syncing = true;
        try
        {
            // Only a change of which rows exist, not of a duration or a result, rebuilds them: a
            // picker the user has open must survive the redraw.
            var layout = string.Join('|', session.Items.Select(i => $"{i.RoleKey}/{i.Disposition}/{i.Role?.DisplayName}"));
            if (!ReferenceEquals(session, _session) || layout != _layout)
            {
                _session = session;
                _layout = layout;
                Build(session);
            }

            SetText(Reason, session.Justification);
            SetText(TicketNumber, session.TicketNumber);
            SetText(TicketSystem, session.TicketSystem);
            ScheduleToggle.IsOn = session.ScheduleStart;
            StartDate.Visibility = StartTime.Visibility = session.ScheduleStart ? Visibility.Visible : Visibility.Collapsed;
            if (StartDate.Date.Date != session.StartAt.Date) StartDate.Date = session.StartAt;
            var time = new TimeSpan(session.StartAt.Hour, session.StartAt.Minute - session.StartAt.Minute % 5, 0);
            if (StartTime.Time != time) StartTime.Time = time;

            foreach (var row in _rows)
            {
                UpdateStatus(row, session);
                // Compact rows put the status on its own line: with nothing to say, the line goes.
                if (_compact && row.Status.Parent is FrameworkElement cell)
                {
                    cell.Visibility = row.Status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                }
            }

            UpdateProgress(session);
            UpdateButtons(session);
        }
        finally
        {
            _syncing = false;
        }

        // Looking at a finished run is reading it; the reopen rule then lets the session go.
        if (session.Finished && !session.ResultsSeen)
        {
            _model.MarkRunSeen(_profileId);
        }
    }

    private static void SetText(TextBox box, string text)
    {
        if (box.Text != text) box.Text = text;
    }

    private void Build(ProfileRunSession session)
    {
        var model = _model!;
        _rows.Clear();
        Rows.Children.Clear();
        var divider = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        foreach (var tenantKey in session.Items.Select(i => i.RoleKey.TenantKey).Distinct())
        {
            var box = TenantGroupBox.Create(model, tenantKey, out var boxRows);
            var first = true;
            foreach (var item in session.Items.Where(i => i.RoleKey.TenantKey == tenantKey))
            {
                var activate = item.Disposition == ProfilePlanDisposition.Activate;
                var key = item.RoleKey;
                var row = new Row
                {
                    Key = key,
                    Duration = activate ? new DurationPicker { Maximum = item.Role?.Policy.MaximumDuration ?? RolePolicy.ManualDefault.MaximumDuration, Duration = item.Duration } : null,
                    Include = activate ? new CheckBox { IsChecked = !session.Excluded.Contains(key), MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(0, 0, -4, 0), VerticalAlignment = VerticalAlignment.Center } : null,
                    Status = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                    Ring = activate ? new ActivationStatusIcon() : null,
                };
                var name = item.Role?.DisplayName ?? model.SummaryName(key);
                if (row.Include is { } include)
                {
                    AutomationProperties.SetName(include, $"Include {name}");
                    include.Checked += (_, _) => OnIncludeChanged(key, true);
                    include.Unchecked += (_, _) => OnIncludeChanged(key, false);
                }

                if (row.Duration is { } picker)
                {
                    AutomationProperties.SetName(picker, $"Duration for {name}");
                    picker.SelectionChanged += (_, _) =>
                    {
                        if (!_syncing) session.SetDuration(key, picker.Duration);
                    };
                }

                row.Name = TenantGroupBox.NameCell(name, item.Role?.Detail, key, model.IsPrivileged(key));
                var line = _compact ? CompactRow(row) : FullRow(row);
                if (!first)
                {
                    line.BorderBrush = divider;
                    line.BorderThickness = new Thickness(0, 1, 0, 0);
                }

                first = false;
                _rows.Add(row);
                boxRows.Children.Add(line);
            }

            Rows.Children.Add(box);
        }
    }

    /// <summary>Checkbox at the leading edge, the name trimming inside the remaining width. Skipped rows have nothing to choose; a placeholder the width of the glyph keeps the names lined up.</summary>
    private static Grid Lead(Row row)
    {
        var lead = new Grid { ColumnSpacing = 8 };
        lead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lead.Children.Add(row.Include is { } include
            ? include
            : (UIElement)new Border { Width = CheckBoxGlyphWidth, Margin = new Thickness(0, 0, -4, 0) });
        Grid.SetColumn(row.Name!, 1);
        lead.Children.Add(row.Name!);
        return lead;
    }

    private static Grid FullRow(Row row)
    {
        var grid = TenantGroupBox.RowGrid(DurationColumn, StatusColumn);
        grid.Children.Add(Lead(row));
        if (row.Duration is { } picker)
        {
            picker.VerticalAlignment = VerticalAlignment.Center;
            picker.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(picker, 1);
            grid.Children.Add(picker);
        }

        var status = TenantGroupBox.StatusCell(row.Ring, row.Status, HorizontalAlignment.Right);
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);
        return grid;
    }

    /// <summary>
    /// The 380-wide layout: the duration is a small borderless drop-down (<c>5 h ⌄</c>) at the trailing
    /// edge of the name's line; the status sits on the detail line beneath the name.
    /// </summary>
    private static Grid CompactRow(Row row)
    {
        var grid = new Grid { Padding = new Thickness(10, 6, 10, 6), MinHeight = 40, ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(Lead(row));
        var status = TenantGroupBox.StatusCell(row.Ring, row.Status, HorizontalAlignment.Left);
        status.Margin = new Thickness(CheckBoxGlyphWidth + 4, 1, 0, 0);
        stack.Children.Add(status);
        grid.Children.Add(stack);
        if (row.Duration is { } picker)
        {
            picker.MinWidth = 0;
            picker.MinHeight = 28;
            picker.FontSize = 12;
            picker.Padding = new Thickness(8, 0, 0, 0);
            picker.BorderThickness = new Thickness(0);
            picker.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            picker.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(picker, 1);
            grid.Children.Add(picker);
        }

        return grid;
    }

    private void UpdateStatus(Row row, ProfileRunSession session)
    {
        var resources = Application.Current.Resources;
        var item = session.Items.FirstOrDefault(i => i.RoleKey == row.Key);
        var status = row.Status;
        var secondary = (Brush)resources["TextFillColorSecondaryBrush"];
        status.Foreground = secondary;
        ToolTipService.SetToolTip(status, null);
        if (item is null)
        {
            return;
        }

        var included = !session.Excluded.Contains(row.Key);
        var result = session.RowResults.GetValueOrDefault(row.Key);
        if (row.Include is { } include)
        {
            include.IsChecked = included;
            include.IsEnabled = !session.Running && !session.Finished;
            if (row.Name is { } name) name.Opacity = included ? 1 : 0.6;
            if (row.Duration is { } duration)
            {
                // A re-plan keeps the rows but may carry other durations.
                if (duration.Duration != item.Duration) duration.Duration = item.Duration;
                duration.IsEnabled = included && !session.Finished;
            }
            if (!included)
            {
                row.Ring?.SetPhase(ActivationIconPhase.Hidden);
                status.Text = session.Finished ? "unchecked · skipped" : "unchecked";
                return;
            }
        }
        else if (row.Name is { } skipped)
        {
            skipped.Opacity = 0.6;
        }

        row.Ring?.SetPhase(item.Disposition != ProfilePlanDisposition.Activate ? ActivationIconPhase.Hidden : result switch
        {
            ActivationResult.Activated => ActivationIconPhase.Success,
            null when session.Running => ActivationIconPhase.Working,
            _ => ActivationIconPhase.Hidden,
        });

        switch (item.Disposition)
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

        switch (result)
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
                if (session.Running)
                {
                    status.Text = "Activating…";
                }
                else if (item.Role?.Policy is { } policy && PolicyNotes.Caption(policy) is { } notes)
                {
                    status.Text = notes;
                    ToolTipService.SetToolTip(status, PolicyNotes.Explanation(policy));
                }
                else
                {
                    status.Text = string.Empty;
                }

                break;
        }
    }

    private void UpdateProgress(ProfileRunSession session)
    {
        // With one or two roles the rows already are the progress.
        var show = (session.Running || session.Finished) && session.Attempted >= 3;
        ProgressRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        // The flyout is too narrow for the bar and a long count side by side: the count goes below.
        Grid.SetRow(ProgressText, _compact ? 1 : 0);
        Grid.SetColumn(ProgressText, _compact ? 0 : 1);
        Grid.SetColumnSpan(Segments, _compact ? 2 : 1);
        ActivationProgressBar.Update(Segments, ProgressText,
            [.. session.ToActivate.Select(i => session.RowResults.GetValueOrDefault(i.RoleKey))], session.Running);
    }

    private void UpdateButtons(ProfileRunSession session)
    {
        Inputs.Visibility = session.Finished ? Visibility.Collapsed : Visibility.Visible;
        TicketRow.Visibility = session.NeedsTicket ? Visibility.Visible : Visibility.Collapsed;
        SubmitButton.Content = $"Activate {session.ToActivate.Count}";
        SubmitButton.IsEnabled = session.CanSubmit(DateTimeOffset.Now) && _model!.IsOnline;
        SubmitButton.Visibility = session.Finished ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = session.Finished ? "Done" : "Cancel";
        CancelButton.IsEnabled = !session.Running;
        Running.IsActive = session.Running;
        Running.Visibility = session.Running ? Visibility.Visible : Visibility.Collapsed;
        // A failed or cancelled run produced no outcomes; say so instead of showing a blank result.
        Failure.Message = session.Failure ?? string.Empty;
        Failure.IsOpen = session.Failure is not null;
        CancelButton.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnIncludeChanged(RoleKey key, bool included)
    {
        if (_syncing || _session is not { } session)
        {
            return;
        }

        if (included) session.Excluded.Remove(key); else session.Excluded.Add(key);
        Refresh();
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _session is not { } session)
        {
            return;
        }

        session.Justification = Reason.Text;
        session.TicketNumber = TicketNumber.Text;
        session.TicketSystem = TicketSystem.Text;
        Refresh();
    }

    private void OnScheduleToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing || _session is not { } session)
        {
            return;
        }

        session.ScheduleStart = ScheduleToggle.IsOn;
        // The time picker shows the start rounded to five minutes: schedule what it shows.
        WriteStart();
    }

    private void OnStartChanged(object? sender, DatePickerValueChangedEventArgs e) => WriteStart();

    private void OnStartTimeChanged(object? sender, TimePickerValueChangedEventArgs e) => WriteStart();

    private void WriteStart()
    {
        if (_syncing || _session is not { } session)
        {
            return;
        }

        var date = StartDate.Date;
        session.StartAt = new DateTimeOffset(date.Year, date.Month, date.Day, StartTime.Time.Hours, StartTime.Time.Minutes, 0, DateTimeOffset.Now.Offset);
        Refresh();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Done?.Invoke(this, EventArgs.Empty);

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        if (_model is { } model)
        {
            await model.SubmitRunAsync(_profileId);
        }
    }
}
