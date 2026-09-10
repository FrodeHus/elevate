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
        QuickStartShared.Content = SharedAppConsent.QuickStartLabel;
        Version.Text = VersionText();
        SyncStartup();
        SyncHotKey();
        UpdateUris();
        UpdateHint();
        UpdateSharedApp();
        UpdateOperations();
        ApplyManaged();
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

    // MARK: Managed configuration

    /// <summary>
    /// Applies what the organization pushed: the client id is shown but not editable, the update
    /// button is replaced by a line saying why, and the managed group lists the keys in effect.
    /// </summary>
    private void ApplyManaged()
    {
        var settings = _model.Settings;
        if (settings.IsClientIdManaged)
        {
            ClientId.Text = settings.ClientId;
            ClientId.IsEnabled = false;
            ClientIdManaged.Visibility = Visibility.Visible;
            ApplyHint.Visibility = Visibility.Collapsed;
            QuickStartShared.Visibility = Visibility.Collapsed;
        }

        if (settings.UpdateCheckDisabled)
        {
            CheckUpdates.Visibility = Visibility.Collapsed;
            UpdatesManaged.Visibility = Visibility.Visible;
        }

        var managed = _model.Managed;
        if (managed.IsEmpty)
        {
            return;
        }

        ManagedGroup.Visibility = Visibility.Visible;
        if (managed.ClientId is { } id)
        {
            AddManagedRow("Client ID", id, monospace: true);
        }

        if (managed.DisableUpdateCheck)
        {
            AddManagedRow("Update check", "Disabled");
        }

        if (managed.AllowedSignInMethods is { Count: > 0 } methods)
        {
            AddManagedRow("Allowed sign-in methods", MethodNames(methods));
        }

        if (managed.AllowedTenants is { Count: > 0 } allowed)
        {
            AddManagedRow("Allowed tenants", TenantList(allowed), monospace: true);
        }

        if (managed.PinnedTenants.Count > 0)
        {
            AddManagedRow("Pinned tenants", TenantList(managed.PinnedTenants), monospace: true);
        }

        if (managed.ManagedProfilesDocument is not null)
        {
            AddManagedRow("Managed profiles", $"{_model.InlineProfileSet.Profiles.Count} (inline)");
        }

        if (managed.ManagedProfilesUrl is { } url)
        {
            AddManagedRow("Managed profiles URL", $"{url.AbsoluteUri} · fetched {Fetched(_model.ManagedProfilesFetchedAt)}", monospace: true);
        }

        foreach (var warning in managed.Warnings.Concat(_model.ManagedTenantWarnings).Concat(_model.ManagedProfileWarnings))
        {
            ManagedRows.Children.Add(new TextBlock
            {
                Text = warning,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            });
        }

        if (managed.Origin is { } origin)
        {
            ManagedRows.Children.Add(new TextBlock
            {
                Text = $"Source: {origin}",
                FontSize = 11,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            });
        }
    }

    private void AddManagedRow(string label, string value, bool monospace = false)
    {
        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = new TextBlock { Text = label, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var text = new TextBlock
        {
            Text = value,
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        if (monospace)
        {
            text.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas");
        }

        Grid.SetColumn(text, 1);
        row.Children.Add(name);
        row.Children.Add(text);
        ManagedRows.Children.Add(row);
    }

    /// <summary>The entries as configured, one per line; a domain also shows the id it resolved to.</summary>
    private string TenantList(IEnumerable<string> entries) => string.Join(
        Environment.NewLine,
        entries.Select(entry => _model.ManagedTenantIds.GetValueOrDefault(entry) is { } resolved
            && !string.Equals(resolved, entry, StringComparison.OrdinalIgnoreCase)
            ? $"{entry} → {resolved}"
            : entry));

    /// <summary>Display names in <see cref="SignInMethodKind"/> order, so the row reads the same every launch.</summary>
    private static string MethodNames(IReadOnlySet<SignInMethodKind> kinds) => string.Join(
        ", ",
        Enum.GetValues<SignInMethodKind>().Where(kinds.Contains).Select(kind => kind switch
        {
            SignInMethodKind.OwnApp => SignInMethod.OwnApp.DisplayName,
            SignInMethodKind.AzureCLI => SignInMethod.AzureCLI.DisplayName,
            SignInMethodKind.AzurePowerShell => SignInMethod.AzurePowerShell.DisplayName,
            _ => SignInMethod.Custom("-").DisplayName,
        }));

    /// <summary>"5 minutes ago", or "never" until the first fetch succeeds.</summary>
    private static string Fetched(DateTimeOffset? date)
    {
        if (date is not { } when)
        {
            return "never";
        }

        var ago = DateTimeOffset.UtcNow - when;
        return ago switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)ago.TotalMinutes} minute(s) ago",
            { TotalDays: < 1 } => $"{(int)ago.TotalHours} hour(s) ago",
            _ => $"{(int)ago.TotalDays} day(s) ago",
        };
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

    /// <summary>The field differs from the stored id, or nothing usable is stored yet. Never while it is managed.</summary>
    private bool IsDirty =>
        !_model.Settings.IsClientIdManaged
        && (!string.Equals(ClientId.Text.Trim(), _model.Settings.ClientId, StringComparison.OrdinalIgnoreCase) || !_model.IsConfigured);

    /// <summary>"Press Enter to apply." while the field differs from the stored value.</summary>
    private void UpdateHint() => ApplyHint.Visibility = IsDirty && ClientId.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Names the shared registration and offers its consent link while the shared id is in effect;
    /// the redirect caption then says the URIs are already registered.
    /// </summary>
    private void UpdateSharedApp()
    {
        var shared = _model.UsesSharedApp && _model.IsConfigured;
        SharedAppRow.Visibility = shared ? Visibility.Visible : Visibility.Collapsed;
        RedirectCaption.Text = shared
            ? "The shared registration already lists these redirect URIs."
            : "Register both under the Mobile and desktop applications platform, and add the Graph PIM permissions listed in the app registration guide.";
    }

    /// <summary>
    /// Fills the field with the shared registration and applies it through the same path as a typed
    /// id, so the sign-out confirmation still applies; a no-op when the shared id is already in effect.
    /// </summary>
    private async void OnQuickStartShared(object sender, RoutedEventArgs e)
    {
        if (_applyingClientId || !await SharedAppConsent.ConfirmAsync(Root.XamlRoot))
        {
            return;
        }

        ClientId.Text = AppSettings.SharedClientId;
        if (_model.UsesSharedApp && _model.IsConfigured)
        {
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

    private void OnGrantSharedConsent(object sender, RoutedEventArgs e)
    {
        if (_model.SharedAppAdminConsentUrl() is { } url)
        {
            _ = Windows.System.Launcher.LaunchUriAsync(url);
        }
    }

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
            UpdateSharedApp();
        }
        catch (PimException ex)
        {
            SaveError.Message = ex.UserMessage;
            SaveError.IsOpen = true;
        }
    }
}
