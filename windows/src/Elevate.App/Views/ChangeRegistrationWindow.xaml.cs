using Elevate.App.Services;
using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>
/// Moves an account onto an Entra app registration — the Settings one or one of its own. For an
/// account already on an Entra app registration this switches it between the two; for an Azure CLI,
/// Azure PowerShell or other-app account it upgrades it, so Elevate can also read and activate
/// Entra roles and PIM for Groups for it. The switch is saved only after the same account signs in
/// with the new registration. Port of the macOS <c>ChangeRegistrationView</c>.
/// </summary>
public sealed partial class ChangeRegistrationWindow : Window
{
    private readonly AppModel _model;
    private readonly string _identityId;
    private bool _working;

    public ChangeRegistrationWindow(AppModel model, string identityId)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        _identityId = identityId;
        var identity = model.Identity(identityId);
        var isUpgrade = identity is not null && !identity.SignInMethod.IsOwnApp;
        var title = identity is null
            ? "App registration"
            : isUpgrade
                ? $"Upgrade {identity.Upn} to an Entra app registration"
                : $"App registration for {identity.Upn}";
        DialogWindows.Configure(this, "App registration", 480, 520, Root, autoHeight: true);
        DialogWindows.DefaultButton(Root, ApplyButton);
        Heading.Text = title;
        Intro.Text = identity is null
            ? string.Empty
            : isUpgrade
                ? $"{identity.Upn} signs in with the {identity.SignInMethod.DisplayName} today. With an Entra app registration Elevate also reads and activates Entra roles and PIM for Groups. Elevate signs the account in with the registration you choose and keeps its tenants, roles and profiles. Nothing changes if the sign-in is cancelled or a different account signs in."
                : $"Elevate signs {identity.Upn} in with the registration you choose and keeps its tenants, roles and profiles. Nothing changes if the sign-in is cancelled or a different account signs in.";

        SettingsRegistration.Content = $"Follow the registration in Settings ({model.SettingsRegistrationLabel})";
        SettingsRegistration.IsEnabled = model.IsAvailable(SignInMethod.OwnApp);
        PinnedRegistration.Visibility = model.CanPin ? Visibility.Visible : Visibility.Collapsed;
        PinnedClientId.Text = identity?.SignInMethod.PinnedClientId ?? model.RememberedPinnedClientId;
        // An account already on a registration of its own opens on that one; otherwise Settings,
        // unless it is the unreachable choice.
        if (model.CanPin && (identity?.SignInMethod.IsPinned == true || !SettingsRegistration.IsEnabled))
        {
            PinnedRegistration.IsChecked = true;
        }
        else
        {
            SettingsRegistration.IsChecked = true;
        }

        Update();
    }

    private SignInMethod Target
    {
        get
        {
            if (PinnedRegistration.IsChecked != true)
            {
                return SignInMethod.OwnApp;
            }

            var id = PinnedClientId.Text.Trim();
            return SignInMethod.PinnedApp(id.Length > 0 ? id : "-");
        }
    }

    private void Update()
    {
        var pinnedChosen = PinnedRegistration.IsChecked == true;
        PinnedRow.Visibility = pinnedChosen ? Visibility.Visible : Visibility.Collapsed;
        var typed = PinnedClientId.Text.Trim();
        var isGuid = AppSettings.IsValidClientId(typed);
        PinnedHint.Visibility = pinnedChosen && typed.Length > 0 && !isGuid ? Visibility.Visible : Visibility.Collapsed;
        PinnedMatchHint.Visibility = pinnedChosen && isGuid && _model.MatchesSettingsClientId(typed)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var identity = _model.Identity(_identityId);
        var busy = _model.IsAccountBusy(_identityId);
        ApplyButton.IsEnabled = !_working && identity is not null && Target != identity.SignInMethod
            && _model.IsAvailable(Target) && !busy;
        CancelButton.IsEnabled = !_working;
        Working.IsActive = _working;
        Working.Visibility = _working ? Visibility.Visible : Visibility.Collapsed;
        if (busy && !_working)
        {
            Error.Severity = InfoBarSeverity.Warning;
            Error.Message = "Wait for this account's requests to finish.";
            Error.IsOpen = true;
        }
    }

    private void OnChoiceChanged(object sender, RoutedEventArgs e) => Update();

    private void OnClientIdChanged(object sender, TextChangedEventArgs e) => Update();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_working || _model.Identity(_identityId) is not { } identity)
        {
            return;
        }

        _working = true;
        Error.IsOpen = false;
        Update();
        var previousNotice = _model.Notice;
        bool changed;
        try
        {
            changed = await _model.ChangeSignInRegistrationAsync(identity, Target);
        }
        catch (Exception ex)
        {
            changed = false;
            _model.Notice = ex.Message;
        }

        _working = false;
        if (changed)
        {
            Close();
            return;
        }

        // The failure belongs to this window, not to the flyout's notice bar.
        Error.Severity = InfoBarSeverity.Error;
        Error.Message = _model.Notice ?? "Sign-in did not complete";
        Error.IsOpen = true;
        _model.Notice = previousNotice;
        Update();
    }
}
