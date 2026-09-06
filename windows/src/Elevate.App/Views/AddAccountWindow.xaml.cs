using Elevate.App.Services;
using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>
/// Picks the sign-in method for a new account. The own-app row needs a client id in Settings; the
/// first-party rows work out of the box through the system browser. Port of the macOS <c>AddAccountView</c>.
/// </summary>
public sealed partial class AddAccountWindow : Window
{
    private readonly AppModel _model;
    private bool _working;

    public AddAccountWindow(AppModel model, SignInMethod? preselected)
    {
        InitializeComponent();
        _model = model;
        DialogWindows.Configure(this, "Add account", 460, 560, Root, autoHeight: true);
        DialogWindows.DefaultButton(Root, ContinueButton);
        CustomClientId.Text = model.RememberedCustomClientId;
        var ownAppAvailable = model.IsAvailable(SignInMethod.OwnApp);
        OwnAppChoice.IsEnabled = ownAppAvailable;
        OwnAppCaption.Text = ownAppAvailable
            ? "Signs in with Windows (WAM) using the client ID from Settings; needs admin consent in each tenant. Supports Entra roles, Azure roles and PIM for Groups."
            : "Unavailable — configure a client ID in Settings.";
        var initial = preselected ?? (ownAppAvailable ? SignInMethod.OwnApp : SignInMethod.AzureCLI);
        (initial.Kind switch
        {
            SignInMethodKind.OwnApp => OwnAppChoice,
            SignInMethodKind.AzurePowerShell => PowerShellChoice,
            SignInMethodKind.Custom => CustomChoice,
            _ => CliChoice,
        }).IsChecked = true;
        Update();
    }

    private SignInMethod Selection
    {
        get
        {
            if (OwnAppChoice.IsChecked == true)
            {
                return SignInMethod.OwnApp;
            }

            if (CustomChoice.IsChecked == true)
            {
                var id = CustomClientId.Text.Trim();
                return id.Length > 0 ? SignInMethod.Custom(id) : SignInMethod.Custom("-");
            }

            return PowerShellChoice.IsChecked == true ? SignInMethod.AzurePowerShell : SignInMethod.AzureCLI;
        }
    }

    private void Update()
    {
        var selection = Selection;
        CustomRow.Visibility = CustomChoice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CustomHint.Visibility = CustomChoice.IsChecked == true && CustomClientId.Text.Trim().Length > 0 && !AppSettings.IsValidClientId(CustomClientId.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (selection.LimitationSummary is { } summary)
        {
            Limits.Severity = InfoBarSeverity.Warning;
            Limits.Title = summary;
            Limits.Message = $"Microsoft grants the {selection.DisplayName} no Graph PIM permissions, so Elevate skips Entra directory roles for this account entirely. Azure resource roles are discovered, activated and deactivated normally. Use your own or a custom app registration for Entra roles.";
        }
        else if (selection.IsCustom)
        {
            Limits.Severity = InfoBarSeverity.Informational;
            Limits.Title = "Capabilities depend on what the app was consented for.";
            Limits.Message = "The registration needs http://localhost as a redirect URI under the Mobile and desktop applications platform (no secret is used). Elevate reads the granted scopes from the token after sign-in: if RoleAssignmentSchedule.ReadWrite.Directory is missing, the account is marked as supporting Azure resource roles only.";
        }
        else
        {
            Limits.Severity = InfoBarSeverity.Success;
            Limits.Title = "Entra and Azure resource roles: activate and deactivate.";
            Limits.Message = "Sign-in uses the Windows account picker. The registration must list ms-appx-web://microsoft.aad.brokerplugin/{client id} and http://localhost as redirect URIs.";
        }

        ContinueButton.IsEnabled = !_working && _model.IsAvailable(selection);
        CancelButton.IsEnabled = !_working;
        Working.IsActive = _working;
        Working.Visibility = _working ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnChoiceChanged(object sender, RoutedEventArgs e) => Update();

    private void OnCustomChanged(object sender, TextChangedEventArgs e) => Update();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnContinue(object sender, RoutedEventArgs e)
    {
        if (_working)
        {
            return;
        }

        _working = true;
        Error.IsOpen = false;
        Update();
        var previousNotice = _model.Notice;
        var chosen = Selection;
        bool added;
        try
        {
            added = await _model.AddAccountAsync(chosen);
        }
        catch (Exception ex)
        {
            added = false;
            _model.Notice = ex.Message;
        }

        _working = false;
        if (added)
        {
            Close();
            return;
        }

        // The failure belongs to this window, not to the flyout's notice bar.
        Error.Message = _model.Notice ?? "Sign-in did not complete";
        Error.IsOpen = true;
        _model.Notice = previousNotice;
        Update();
    }
}
