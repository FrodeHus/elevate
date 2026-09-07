using Elevate.App.Services;
using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Elevate.App.Views;

/// <summary>The client id with the redirect URIs the registration needs, run at sign-in, and the version. Port of the macOS <c>SettingsView</c>.</summary>
public sealed partial class SettingsWindow : Window
{
    private sealed record ProfileOption(Guid? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly AppModel _model;
    private bool _syncingToggle;
    private bool _syncingHotKey;
    private bool _applyingClientId;

    public SettingsWindow(AppModel model)
    {
        InitializeComponent();
        _model = model;
        DialogWindows.Configure(this, "Settings", 520, 760, Root, autoHeight: true);
        DialogWindows.DefaultButton(Root, CloseButton);
        ClientId.Text = model.Settings.ClientId;
        LoopbackUri.Text = AppSettings.LoopbackRedirectUri;
        Version.Text = VersionText();
        SyncStartup();
        SyncHotKey();
        UpdateUris();
        UpdateHint();
        UpdateOperations();
        _model.Changed += OnModelChanged;
        Closed += (_, _) => _model.Changed -= OnModelChanged;
    }

    private static string VersionText()
    {
        var build = BuildInfo.Build.Length > 7 ? BuildInfo.Build[..7] : BuildInfo.Build;
        return $"Elevate {BuildInfo.Version} ({build}) · {BuildInfo.SigningDescription}";
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        UpdateOperations();
        // The profile list changes under the picker when profiles are added or deleted elsewhere.
        var ids = HotKeyProfile.Items.OfType<ProfileOption>().Select(o => o.Id).ToList();
        if (!ids.SequenceEqual(new Guid?[] { null }.Concat(_model.Profiles.Select(p => (Guid?)p.Id))))
        {
            SyncHotKey();
        }
    }

    // MARK: Global shortcut

    private void SyncHotKey()
    {
        _syncingHotKey = true;
        try
        {
            Recorder.Binding = _model.Settings.HotKey;
            ClearHotKey.Visibility = _model.Settings.HotKey is null ? Visibility.Collapsed : Visibility.Visible;
            HotKeyProfile.Items.Clear();
            HotKeyProfile.Items.Add(new ProfileOption(null, "None"));
            foreach (var profile in _model.Profiles)
            {
                HotKeyProfile.Items.Add(new ProfileOption(profile.Id, profile.Name));
            }

            var wanted = _model.Settings.HotKeyProfileId;
            HotKeyProfile.SelectedItem = HotKeyProfile.Items.OfType<ProfileOption>().FirstOrDefault(o => o.Id == wanted) ?? HotKeyProfile.Items[0];
        }
        finally
        {
            _syncingHotKey = false;
        }
    }

    private void OnHotKeyChanged(object? sender, EventArgs e)
    {
        if (_syncingHotKey)
        {
            return;
        }

        _model.Settings.HotKey = Recorder.Binding;
        ClearHotKey.Visibility = Recorder.Binding is null ? Visibility.Collapsed : Visibility.Visible;
        _model.ApplyHotKey();
    }

    private void OnClearHotKey(object sender, RoutedEventArgs e)
    {
        Recorder.Binding = null;
        OnHotKeyChanged(sender, EventArgs.Empty);
    }

    private void OnHotKeyProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingHotKey)
        {
            return;
        }

        _model.Settings.HotKeyProfileId = (HotKeyProfile.SelectedItem as ProfileOption)?.Id;
        _model.ApplyHotKey();
    }

    // MARK: Updates and diagnostics

    private void UpdateOperations()
    {
        HotKeyError.Text = _model.HotKeyError ?? string.Empty;
        HotKeyError.Visibility = _model.HotKeyError is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateMessage.Text = _model.UpdateCheckMessage ?? string.Empty;
        UpdateMessage.Visibility = _model.UpdateCheckMessage is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdates.IsEnabled = false;
        Checking.IsActive = true;
        Checking.Visibility = Visibility.Visible;
        try
        {
            await _model.CheckForUpdatesAsync(force: true);
        }
        finally
        {
            Checking.IsActive = false;
            Checking.Visibility = Visibility.Collapsed;
            CheckUpdates.IsEnabled = true;
            UpdateOperations();
        }
    }

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(_model.DiagnosticsText());
        Clipboard.SetContent(package);
        CopyDiagnostics.Content = "Copied";
        _ = ResetDiagnosticsLabelAsync();
    }

    private async Task ResetDiagnosticsLabelAsync()
    {
        await Task.Delay(2000);
        CopyDiagnostics.Content = "Copy diagnostics";
    }

    private void SyncStartup()
    {
        _syncingToggle = true;
        RunAtLogin.IsOn = StartupRegistration.IsEnabled;
        _syncingToggle = false;
    }

    private void UpdateUris()
    {
        var id = ClientId.Text.Trim();
        BrokerUri.Text = AppSettings.BrokerRedirectUri(AppSettings.IsValidClientId(id) ? id : "{client id}");
    }

    /// <summary>The field differs from the stored id, or nothing usable is stored yet.</summary>
    private bool IsDirty =>
        !string.Equals(ClientId.Text.Trim(), _model.Settings.ClientId, StringComparison.OrdinalIgnoreCase) || !_model.IsConfigured;

    /// <summary>"Press Enter to apply." while the field differs from the stored value.</summary>
    private void UpdateHint() => ApplyHint.Visibility = IsDirty && ClientId.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnClientIdChanged(object sender, TextChangedEventArgs e)
    {
        Saved.Visibility = Visibility.Collapsed;
        SaveError.IsOpen = false;
        UpdateUris();
        UpdateHint();
    }

    private void OnClientIdKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = ApplyClientIdAsync();
        }
    }

    private void OnClientIdLostFocus(object sender, RoutedEventArgs e) => _ = ApplyClientIdAsync();

    private void OnCopyUris(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(BrokerUri.Text + Environment.NewLine + LoopbackUri.Text);
        Clipboard.SetContent(package);
        CopyUris.Content = "Copied";
        _ = ResetCopyLabelAsync();
    }

    private async Task ResetCopyLabelAsync()
    {
        await Task.Delay(2000);
        CopyUris.Content = "Copy";
    }

    private void OnRunAtLoginToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggle)
        {
            return;
        }

        StartupError.Visibility = Visibility.Collapsed;
        try
        {
            StartupRegistration.Set(RunAtLogin.IsOn);
        }
        catch (Exception ex)
        {
            StartupError.Text = ex.Message;
            StartupError.Visibility = Visibility.Visible;
            _model.LogError("Start with sign-in: " + ex.Message);
            SyncStartup();
        }
    }

    /// <summary>Close waits while the client id confirmation is up: the click's focus loss is what raised it.</summary>
    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (!_applyingClientId)
        {
            Close();
        }
    }

    /// <summary>
    /// Applies the field on commit, like the other settings on this page. An empty field puts the
    /// stored value back; an invalid one shows the error and keeps the text for correction; a
    /// change that signs accounts out asks first, and Cancel restores the stored value.
    /// </summary>
    private async Task ApplyClientIdAsync()
    {
        // The confirmation dialog moves focus, which raises LostFocus again while it is open.
        if (_applyingClientId || !IsDirty)
        {
            return;
        }

        var trimmed = ClientId.Text.Trim();
        if (trimmed.Length == 0)
        {
            ClientId.Text = _model.Settings.ClientId;
            return;
        }

        if (!AppSettings.IsValidClientId(trimmed))
        {
            SaveError.Message = "Enter the application (client) ID as a GUID";
            SaveError.IsOpen = true;
            return;
        }

        _applyingClientId = true;
        try
        {
            await ApplyClientIdCoreAsync();
        }
        finally
        {
            _applyingClientId = false;
        }
    }

    private async Task ApplyClientIdCoreAsync()
    {
        var count = _model.OwnAppIdentityCount;
        if (count > 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Change client ID?",
                Content = $"Saving a different client ID signs out {count} account{(count == 1 ? "" : "s")} that use{(count == 1 ? "s" : "")} it; you will add {(count == 1 ? "it" : "them")} again. Azure CLI, Azure PowerShell and custom accounts are unaffected.",
                PrimaryButtonText = "Sign out and change",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                ClientId.Text = _model.Settings.ClientId;
                return;
            }
        }

        try
        {
            _model.ApplyClientId(ClientId.Text);
            SaveError.IsOpen = false;
            Saved.Visibility = Visibility.Visible;
            UpdateHint();
        }
        catch (PimException ex)
        {
            SaveError.Message = ex.UserMessage;
            SaveError.IsOpen = true;
        }
    }
}
