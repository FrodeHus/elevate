using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Elevate.App.Views;

/// <summary>The Approve/Deny window for one pending approval request. Port of the macOS <c>DecisionView</c>.</summary>
public sealed partial class DecisionWindow : Window
{
    private readonly AppModel _model;
    private readonly string _requestId;
    private readonly bool _approve;
    private bool _running;

    public DecisionWindow(AppModel model, string requestId, bool approve)
    {
        InitializeComponent();
        _model = model;
        _requestId = requestId;
        _approve = approve;
        DialogWindows.Configure(this, approve ? "Approve request" : "Deny request", 460, 420, Root, autoHeight: true);
        DialogWindows.DefaultButton(Root, SubmitButton);
        Heading.Text = approve ? "Approve request" : "Deny request";
        SubmitButton.Content = approve ? "Approve" : "Deny";
        Justification.Text = model.Settings.LastApprovalJustification;
        Fill();
        _model.Changed += OnModelChanged;
        Closed += (_, _) => _model.Changed -= OnModelChanged;
        Justification.Focus(FocusState.Programmatic);
    }

    public string RequestId => _requestId;

    public bool Approve => _approve;

    /// <summary>
    /// The live row: it disappears once decided elsewhere or dropped by a refresh. Looked up in the
    /// unfiltered set so an active panel search never hides the request the window is showing.
    /// </summary>
    private ApprovalRequest? Request => _model.Approval(_requestId);

    /// <summary>Denying a request has to say why; approving may be wordless.</summary>
    private bool CanSubmit => !_running && Request is not null && (_approve || !string.IsNullOrWhiteSpace(Justification.Text));

    private void Fill()
    {
        Details.Children.Clear();
        if (Request is not { } r)
        {
            Details.Children.Add(new TextBlock { Text = "This request is no longer pending." });
            Inputs.Visibility = Visibility.Collapsed;
            SubmitButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Close";
            return;
        }

        Details.Children.Add(Line("Requester", r.RequesterName));
        Details.Children.Add(Line("Role", r.ScopeCaption is { } scope ? $"{r.TargetName} · {scope}" : r.TargetName));
        Details.Children.Add(Line("Tenant", _model.ApprovalTenantName(r)));
        if (r.RequestedDuration is { } d)
        {
            Details.Children.Add(Line("Duration", Countdown.Label(d)));
        }

        Details.Children.Add(Line("Reason", r.Justification ?? "No reason given"));
    }

    private static Grid Line(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        var text = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (Request is null && !_running)
        {
            Fill();
        }

        if (_model.ApprovalErrors.TryGetValue(_requestId, out var error))
        {
            Error.Message = error;
            Error.IsOpen = true;
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

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        if (!CanSubmit || Request is not { } request)
        {
            return;
        }

        _running = true;
        Error.IsOpen = false;
        UpdateSubmit();
        var ok = await _model.DecideAsync(request, _approve, Justification.Text.Trim());
        _running = false;
        UpdateSubmit();
        if (ok)
        {
            Close();
        }
    }
}
