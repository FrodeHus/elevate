using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>
/// Policy choice plus justification for one package; hands off to My Access when the chosen
/// policy asks questions Elevate does not collect. Port of the macOS <c>RequestPackageSheet</c>.
/// </summary>
public sealed partial class RequestPackageWindow : Window
{
    private readonly AppModel _model;
    private readonly TenantKey _key;
    private readonly AccessPackage _package;
    private readonly Action _onSubmitted;
    private IReadOnlyList<PolicyRequirement> _requirements = [];
    private bool _loading = true;
    private bool _submitting;

    public RequestPackageWindow(AppModel model, TenantKey key, AccessPackage package, Action onSubmitted)
    {
        InitializeComponent();
        _model = model;
        _key = key;
        _package = package;
        _onSubmitted = onSubmitted;
        DialogWindows.Configure(this, "Request access package", 440, 300, Root, autoHeight: true);
        Heading.Text = $"Request {package.DisplayName}";
        if (!string.IsNullOrEmpty(package.Description))
        {
            Description.Text = package.Description;
            Description.Visibility = Visibility.Visible;
        }

        DialogWindows.DefaultButton(Root, PrimaryButton);
        Update();
        _ = LoadAsync();
    }

    public string PackageId => _package.Id;

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
        Loading.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
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

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _requirements = await _model.PackageRequirementsAsync(_key, _package.Id);
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
            LoadError.Message = AppModel.Describe(e);
            LoadError.IsOpen = true;
        }
        finally
        {
            _loading = false;
        }

        Update();
        if (FormPane.Visibility == Visibility.Visible)
        {
            Justification.Focus(FocusState.Programmatic);
        }
    }

    private void OnPolicyChanged(object sender, SelectionChangedEventArgs e) => Update();

    private void OnJustificationChanged(object sender, TextChangedEventArgs e) => Update();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected || _submitting)
        {
            return;
        }

        if (selected.RequiresAnswers)
        {
            _ = Windows.System.Launcher.LaunchUriAsync(AccessPackageProvider.MyAccessUrl(_key.TenantId, _package.Id));
            Close();
            return;
        }

        if (!CanSubmit)
        {
            return;
        }

        _submitting = true;
        SubmitError.IsOpen = false;
        Update();
        try
        {
            await _model.RequestPackageAsync(_key, _package.Id, _requirements.Count > 1 ? selected.Id : null, Justification.Text.Trim());
            _onSubmitted();
            Close();
            return;
        }
        catch (Exception ex)
        {
            SubmitError.Message = AppModel.Describe(ex);
            SubmitError.IsOpen = true;
        }
        finally
        {
            _submitting = false;
        }

        Update();
    }
}
