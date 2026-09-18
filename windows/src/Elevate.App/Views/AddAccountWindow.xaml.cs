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
        PinnedClientId.Text = model.RememberedPinnedClientId;
        var ownAppAvailable = model.IsAvailable(SignInMethod.OwnApp);
        // The Entra row is usable through either registration, so it stays enabled when only a
        // registration of the account's own is reachable.
        OwnAppChoice.IsEnabled = ownAppAvailable || model.CanPin;
        SettingsRegistration.Content = $"Use the registration in Settings ({model.SettingsRegistrationLabel})";
        SettingsRegistration.IsEnabled = ownAppAvailable;
        // Hidden entirely when the organization manages the client id: there is nothing to choose.
        PinnedRegistration.Visibility = model.CanPin ? Visibility.Visible : Visibility.Collapsed;
        if (ownAppAvailable || !model.CanPin)
        {
            SettingsRegistration.IsChecked = true;
        }
        else
        {
            PinnedRegistration.IsChecked = true;
        }

        OwnAppCaption.Text = !ownAppAvailable && !model.CanPin
            ? "Unavailable — configure a client ID in Settings."
            : model.UsesSharedApp
                ? "Signs in with Windows (WAM) using the shared Elevate app (no SLA) configured in Settings; an administrator must grant consent once per tenant. Supports Entra roles, Azure roles and PIM for Groups."
                : "Signs in with Windows (WAM) using the client ID from Settings; needs admin consent in each tenant. Supports Entra roles, Azure roles and PIM for Groups.";
        // A method the organization does not permit is not offered at all: its row goes, and so
        // does the client-id box under "Other app (browser sign-in)" when custom registrations are withheld.
        var offered = model.AvailableMethods;
        OwnAppChoice.Visibility = Offered(offered, SignInMethodKind.OwnApp);
        CliChoice.Visibility = Offered(offered, SignInMethodKind.AzureCLI);
        PowerShellChoice.Visibility = Offered(offered, SignInMethodKind.AzurePowerShell);
        CustomChoice.Visibility = model.IsCustomMethodAllowed ? Visibility.Visible : Visibility.Collapsed;
        var initial = preselected ?? (ownAppAvailable || model.CanPin ? SignInMethod.OwnApp : SignInMethod.AzureCLI);
        if (initial.PinnedClientId is { } preselectedPin && model.CanPin)
        {
            PinnedClientId.Text = preselectedPin;
            PinnedRegistration.IsChecked = true;
        }

        var choice = Row(initial.Kind);
        if (choice.Visibility != Visibility.Visible)
        {
            // Every fixed method may be withheld, leaving "Other app (browser sign-in)" as all
            // there is — and it may be withheld too, in which case the dialog has nothing to offer.
            choice = new[] { OwnAppChoice, CliChoice, PowerShellChoice, CustomChoice }
                .FirstOrDefault(r => r.Visibility == Visibility.Visible) ?? OwnAppChoice;
        }

        if (choice.Visibility == Visibility.Visible)
        {
            choice.IsChecked = true;
        }
        else
        {
            Error.Message = AppModel.DisallowedMethodNotice;
            Error.IsOpen = true;
        }

        Update();
    }

    private static Visibility Offered(IReadOnlyList<SignInMethod> methods, SignInMethodKind kind) =>
        methods.Any(m => m.Kind == kind) ? Visibility.Visible : Visibility.Collapsed;

    private RadioButton Row(SignInMethodKind kind) => kind switch
    {
        SignInMethodKind.OwnApp => OwnAppChoice,
        SignInMethodKind.AzurePowerShell => PowerShellChoice,
        SignInMethodKind.Custom => CustomChoice,
        _ => CliChoice,
    };

    private SignInMethod Selection
    {
        get
        {
            if (OwnAppChoice.IsChecked == true)
            {
                if (PinnedRegistration.IsChecked != true)
                {
                    return SignInMethod.OwnApp;
                }

                var pinned = PinnedClientId.Text.Trim();
                // A placeholder id keeps the method pinned while the box is empty, so the dialog
                // stays on "Use a different registration" instead of falling back to Settings.
                return SignInMethod.PinnedApp(pinned.Length > 0 ? pinned : "-");
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
        // Nothing to choose between when a registration of the account's own is unavailable, so
        // the whole sub-section goes rather than leaving one row that cannot be unpicked.
        RegistrationRow.Visibility = _model.CanPin && OwnAppChoice.IsChecked == true && OwnAppChoice.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        var pinnedChosen = OwnAppChoice.IsChecked == true && PinnedRegistration.IsChecked == true;
        PinnedRow.Visibility = pinnedChosen ? Visibility.Visible : Visibility.Collapsed;
        var typedPin = PinnedClientId.Text.Trim();
        var pinIsGuid = AppSettings.IsValidClientId(typedPin);
        PinnedHint.Visibility = pinnedChosen && typedPin.Length > 0 && !pinIsGuid ? Visibility.Visible : Visibility.Collapsed;
        PinnedMatchHint.Visibility = pinnedChosen && pinIsGuid && _model.MatchesSettingsClientId(typedPin)
            ? Visibility.Visible
            : Visibility.Collapsed;
        CustomRow.Visibility = CustomChoice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CustomHint.Visibility = CustomChoice.IsChecked == true && CustomClientId.Text.Trim().Length > 0 && !AppSettings.IsValidClientId(CustomClientId.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (selection.LimitationSummary is { } summary)
        {
            Limits.Severity = InfoBarSeverity.Informational;
            Limits.Title = summary;
            Limits.Message = $"Microsoft grants the {selection.DisplayName} no Graph PIM permissions, so Elevate skips Entra directory roles for this account entirely. Azure resource roles are discovered, activated and deactivated normally. Use your own or another app registration for Entra roles.";
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
