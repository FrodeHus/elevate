using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>
/// Policy choice plus justification for one package, in the flyout's main column; hands off to My
/// Access when the chosen policy asks questions Elevate does not collect. Port of the macOS
/// <c>RequestPackageView</c>. Submitting and cancelling go through the model, which returns the panel
/// to the package list.
/// </summary>
public sealed partial class RequestPackageContent : UserControl
{
    private AppModel _model = null!;
    private TenantKey _key = new(string.Empty, string.Empty);
    private AccessPackage _package = new(string.Empty, string.Empty);
    private bool _bound;
    private IReadOnlyList<PolicyRequirement> _requirements = [];
    private bool _loading = true;
    private bool _submitting;
    // Bumped by every Bind and Unbind, so a read or submit that finishes after the form was left is dropped.
    private int _version;

    public RequestPackageContent()
    {
        InitializeComponent();
    }

    /// <summary>Starts a fresh form for this package and reads its policies.</summary>
    public void Bind(AppModel model, TenantKey key, AccessPackage package)
    {
        Unbind();
        _model = model;
        _key = key;
        _package = package;
        _bound = true;
        _loading = true;
        _submitting = false;
        _requirements = [];
        Policies.Items.Clear();
        Justification.Text = string.Empty;
        LoadError.IsOpen = false;
        SubmitError.IsOpen = false;
        Heading.Text = $"Request {package.DisplayName}";
        Description.Text = package.Description ?? string.Empty;
        Description.Visibility = string.IsNullOrEmpty(package.Description) ? Visibility.Collapsed : Visibility.Visible;
        Update();
        _ = LoadAsync(_version);
    }

    /// <summary>Lets the form go; whatever is still in flight lands nowhere.</summary>
    public void Unbind()
    {
        _version++;
        _bound = false;
    }

    private PolicyRequirement? Selected =>
        _requirements.Count == 0 ? null
        : Policies.SelectedIndex >= 0 && Policies.SelectedIndex < _requirements.Count ? _requirements[Policies.SelectedIndex]
        : _requirements[0];

    private bool CanSubmit => !_submitting && Selected is { RequiresAnswers: false } && Justification.Text.Trim().Length > 0;

    /// <summary>"Engineering · 90 days · approval required": the label the picker shows per policy.</summary>
    public static string PolicyLabel(PolicyRequirement r)
    {
        var parts = new List<string> { r.DisplayName };
        if (!string.IsNullOrEmpty(r.Description))
        {
            parts.Add(r.Description);
        }

        parts.Add(r.IsApprovalRequired ? "approval required" : "no approval");
        return string.Join(" · ", parts);
    }

    private void Update()
    {
        if (!_bound)
        {
            return;
        }

        LoadingRow.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        var loaded = !_loading && !LoadError.IsOpen;
        NoPolicy.Visibility = loaded && _requirements.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PolicyPane.Visibility = loaded && _requirements.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var selected = Selected;
        var questions = loaded && selected is { RequiresAnswers: true };
        QuestionsPane.Visibility = questions ? Visibility.Visible : Visibility.Collapsed;
        FormPane.Visibility = loaded && selected is { RequiresAnswers: false } ? Visibility.Visible : Visibility.Collapsed;
        if (questions)
        {
            QuestionsDetail.Text = $"Policy “{selected!.DisplayName}” requires answers that Elevate does not collect. Continue in the My Access portal, then check the Requested tab here.";
        }

        PrimaryButton.Content = questions ? "Open in My Access" : "Submit Request";
        PrimaryButton.IsEnabled = questions || CanSubmit;
        CancelButton.IsEnabled = !_submitting;
        Working.IsActive = _submitting;
        Working.Visibility = _submitting ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadAsync(int version)
    {
        try
        {
            var requirements = await _model.PackageRequirementsAsync(_key, _package.Id);
            if (version != _version)
            {
                return;
            }

            _requirements = requirements;
            LoadError.IsOpen = false;
            Policies.Items.Clear();
            foreach (var r in _requirements)
            {
                Policies.Items.Add(PolicyLabel(r));
            }

            if (_requirements.Count > 0)
            {
                Policies.SelectedIndex = 0;
                PolicyHint.Text = $"{_requirements.Count} policies let you request this package. The policy sets the duration and who approves.";
            }
        }
        catch (Exception e)
        {
            if (version != _version)
            {
                return;
            }

            LoadError.Message = AppModel.Describe(e);
            LoadError.IsOpen = true;
        }

        _loading = false;
        Update();
        if (FormPane.Visibility == Visibility.Visible)
        {
            Justification.Focus(FocusState.Programmatic);
        }
    }

    private void OnPolicyChanged(object sender, SelectionChangedEventArgs e) => Update();

    /// <summary>For the flyout reopening on the form; the form's own focus waits for the requirements to load.</summary>
    public void FocusJustification()
    {
        if (FormPane.Visibility == Visibility.Visible)
        {
            Justification.Focus(FocusState.Programmatic);
        }
    }

    private void OnJustificationChanged(object sender, TextChangedEventArgs e) => Update();

    private void OnCancel(object sender, RoutedEventArgs e) => _model.EndPackageRequest();

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected || _submitting)
        {
            return;
        }

        if (selected.RequiresAnswers)
        {
            _ = Windows.System.Launcher.LaunchUriAsync(AccessPackageProvider.MyAccessUrl(_key.TenantId, _package.Id));
            _model.EndPackageRequest();
            return;
        }

        if (!CanSubmit)
        {
            return;
        }

        var version = _version;
        _submitting = true;
        SubmitError.IsOpen = false;
        Update();
        try
        {
            await _model.RequestPackageAsync(_key, _package.Id, _requirements.Count > 1 ? selected.Id : null, Justification.Text.Trim());
            // Left while it was being sent: the request still went through, only the view is gone.
            if (version == _version)
            {
                _model.FinishPackageRequest();
            }

            return;
        }
        catch (Exception ex)
        {
            if (version != _version)
            {
                return;
            }

            SubmitError.Message = AppModel.Describe(ex);
            SubmitError.IsOpen = true;
        }

        _submitting = false;
        Update();
    }
}
