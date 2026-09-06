using System.Reflection;
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
    private readonly AppModel _model;
    private bool _syncingToggle;

    public SettingsWindow(AppModel model)
    {
        InitializeComponent();
        _model = model;
        DialogWindows.Configure(this, "Settings", 500, 620, Root, autoHeight: true);
        DialogWindows.DefaultButton(Root, SaveButton);
        ClientId.Text = model.Settings.ClientId;
        LoopbackUri.Text = AppSettings.LoopbackRedirectUri;
        Version.Text = VersionText();
        SyncStartup();
        UpdateUris();
        UpdateSave();
    }

    private static string VersionText()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        return $"Elevate {version} · Windows App SDK 1.8";
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

    private bool IsSaveable
    {
        get
        {
            if (!AppSettings.IsValidClientId(ClientId.Text))
            {
                return false;
            }

            var trimmed = ClientId.Text.Trim();
            return !string.Equals(trimmed, _model.Settings.ClientId, StringComparison.OrdinalIgnoreCase) || !_model.IsConfigured;
        }
    }

    private void UpdateSave() => SaveButton.IsEnabled = IsSaveable;

    private void OnClientIdChanged(object sender, TextChangedEventArgs e)
    {
        Saved.Visibility = Visibility.Collapsed;
        SaveError.IsOpen = false;
        UpdateUris();
        UpdateSave();
    }

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

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (!IsSaveable)
        {
            return;
        }

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
                return;
            }
        }

        try
        {
            _model.ApplyClientId(ClientId.Text);
            SaveError.IsOpen = false;
            Saved.Visibility = Visibility.Visible;
            UpdateSave();
        }
        catch (PimException ex)
        {
            SaveError.Message = ex.UserMessage;
            SaveError.IsOpen = true;
        }
    }
}
