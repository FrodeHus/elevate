using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Elevate.App.Views;

/// <summary>One persistent results surface for a single assignment or an exact profile execution.</summary>
internal sealed class DeactivationWindow : Window
{
    private sealed record Row(ActiveAssignment Assignment, TextBlock Status, ActivationStatusIcon Icon, CheckBox? Include)
    {
        public bool Completed { get; set; }
        public bool Finished { get; set; }
        public string? Error { get; set; }
        /// <summary>An unchecked role is kept for this pass only; it stays in the run for a later one.</summary>
        public bool Included => Include?.IsChecked != false;
    }
    private readonly AppModel _model;
    private readonly List<Row> _rows = [];
    private readonly Button _submit = new() { Content = "Deactivate" };
    private readonly Button _done = new() { Content = "Done" };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _running;

    public DeactivationWindow(AppModel model, ProfileRun run)
        : this(model, run.ProfileName, run.Entries.Where(e => !e.Completed), run.StartedAt, run.ProfileId) { }

    public DeactivationWindow(AppModel model, ActiveAssignment assignment)
        : this(model, model.SummaryName(assignment.RoleKey), [new ProfileRun.Entry(assignment, model.SummaryName(assignment.RoleKey))], null) { }

    private DeactivationWindow(AppModel model, string name, IEnumerable<ProfileRun.Entry> entries, DateTimeOffset? startedAt, Guid? profileId = null)
    {
        _model = model;
        var root = new Grid { Padding = new Thickness(22) };
        var content = new StackPanel { Spacing = 14 };
        root.Children.Add(content);
        Content = root;
        content.Children.Add(new TextBlock { Text = $"Deactivate \"{name}\"", FontSize = 20, TextWrapping = TextWrapping.Wrap });
        if (startedAt is { } date)
            content.Children.Add(new TextBlock { Text = $"Run {date.ToLocalTime():g}", FontSize = 12 });
        content.Children.Add(new TextBlock
        {
            Text = "Only the assignments shown here will be deactivated. Other profiles using the same access will also lose that access.",
            TextWrapping = TextWrapping.Wrap,
        });
        var list = new StackPanel { Spacing = 12 };
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420 });
        // A single assignment is already a choice; only a run's list gets per-role checkboxes.
        var selectable = profileId is not null;
        foreach (var entry in entries)
        {
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var icon = new ActivationStatusIcon { Downward = true };
            var line = new StackPanel { Spacing = 4 };
            CheckBox? include = null;
            if (selectable)
            {
                include = new CheckBox { IsChecked = true, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(0, 0, -4, 0), VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(include, $"Include {entry.DisplayName}");
                include.Checked += (_, _) => Refresh();
                include.Unchecked += (_, _) => Refresh();
                var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                title.Children.Add(include);
                title.Children.Add(new TextBlock { Text = entry.DisplayName, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                line.Children.Add(title);
            }
            else
            {
                line.Children.Add(new TextBlock { Text = entry.DisplayName, TextWrapping = TextWrapping.Wrap });
            }
            line.Children.Add(new TextBlock { Text = $"{model.Identity(entry.Assignment.RoleKey.IdentityId)?.Upn} · {model.Tenant(entry.Assignment.RoleKey.TenantKey)?.DisplayName}", FontSize = 12 });
            if (model.Role(entry.Assignment.RoleKey)?.Detail is { } detail)
                line.Children.Add(new TextBlock { Text = detail, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            var sharedProfiles = model.Profiles.Where(p => p.Id != profileId
                && p.Entries.Any(e => e.RoleKey == entry.Assignment.RoleKey)).Select(p => p.Name).Distinct().ToList();
            if (sharedProfiles.Count > 0)
                line.Children.Add(new TextBlock { Text = "Also used by: " + string.Join(", ", sharedProfiles), FontSize = 12, TextWrapping = TextWrapping.Wrap });
            var result = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            result.Children.Add(icon);
            result.Children.Add(status);
            line.Children.Add(result);
            list.Children.Add(line);
            _rows.Add(new Row(entry.Assignment, status, icon, include));
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_submit);
        buttons.Children.Add(_done);
        content.Children.Add(buttons);
        _done.Click += (_, _) => Close();
        _submit.Click += async (_, _) => await SubmitAsync();
        _timer.Tick += (_, _) => Refresh();
        Closed += (_, _) => _timer.Stop();
        DialogWindows.Configure(this, "Deactivate", 600, 500, root, autoHeight: true);
        Refresh();
        _timer.Start();
        _ = RefreshAssignmentsAsync();
    }

    private async Task RefreshAssignmentsAsync()
    {
        foreach (var tenant in _rows.Select(r => r.Assignment.RoleKey.TenantKey).Distinct())
            await _model.RefreshAsync(tenant);
        Refresh();
    }

    private void Refresh()
    {
        if (_running) return;
        foreach (var row in _rows)
        {
            if (row.Include is { } include) include.IsEnabled = !row.Finished;
            row.Status.Text = row.Completed ? "Deactivated"
                : !row.Included ? "Unchecked · kept"
                : row.Error ?? _model.DeactivationRefusal(row.Assignment) ?? "Ready to deactivate";
            row.Status.Foreground = (Brush)Application.Current.Resources[row.Completed ? "SystemFillColorSuccessBrush"
                : row.Error is null || !row.Included ? "TextFillColorSecondaryBrush" : row.Finished ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush"];
            ToolTipService.SetToolTip(row.Status, row.Status.Text);
        }
        _submit.IsEnabled = _model.IsOnline && _rows.Any(r => !r.Finished && r.Included && !_model.InFlight.Contains(r.Assignment.RoleKey));
    }

    private async Task SubmitAsync()
    {
        _running = true;
        _submit.IsEnabled = _done.IsEnabled = false;
        foreach (var row in _rows) if (row.Include is { } include) include.IsEnabled = false;
        foreach (var row in _rows.Where(r => !r.Finished && r.Included))
        {
            row.Error = null;
            row.Status.Text = "Deactivating…";
            row.Icon.SetPhase(ActivationIconPhase.Working);
            try
            {
                row.Error = await _model.DeactivateAssignmentAsync(row.Assignment);
                row.Completed = row.Error is null;
                row.Finished = row.Completed || _model.ProfileAssignmentCompleted(row.Assignment);
            }
            catch (Exception ex) { row.Error = ex.Message; }
            row.Icon.SetPhase(row.Completed ? ActivationIconPhase.Success : ActivationIconPhase.Hidden);
            row.Status.Text = row.Completed ? "Deactivated" : row.Error;
        }
        _running = false;
        _done.IsEnabled = true;
        _submit.Content = "Retry remaining";
        Refresh();
    }
}
