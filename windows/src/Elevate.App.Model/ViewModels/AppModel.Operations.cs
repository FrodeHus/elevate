using Elevate.App.Services;
using Elevate.Core.Models;
using Elevate.Core.Support;

namespace Elevate.App.ViewModels;

/// <summary>The global shortcut, diagnostics and the update check. Port of <c>AppModel+Operations.swift</c>.</summary>
public sealed partial class AppModel
{
    private string? _hotKeyError;
    private Guid? _pendingProfileRun;
    private (string Version, Uri Url)? _updateAvailable;
    private string? _updateCheckMessage;

    /// <summary>Why the global shortcut could not be registered, shown in Settings.</summary>
    public string? HotKeyError
    {
        get => _hotKeyError;
        private set
        {
            if (SetProperty(ref _hotKeyError, value))
            {
                Touch();
            }
        }
    }

    /// <summary>Set when the global hot key's profile needs input; the shell opens the Run window for it.</summary>
    public Guid? PendingProfileRun
    {
        get => _pendingProfileRun;
        set
        {
            if (SetProperty(ref _pendingProfileRun, value))
            {
                Touch();
            }
        }
    }

    /// <summary>
    /// A newer release than the running build, once a check has found one and the user has not
    /// dismissed it. The flyout shows a banner while it is set.
    /// </summary>
    public (string Version, Uri Url)? UpdateAvailable
    {
        get => _updateAvailable;
        private set
        {
            if (SetProperty(ref _updateAvailable, value))
            {
                Touch();
            }
        }
    }

    /// <summary>The one-line result of the last check, for the Settings button.</summary>
    public string? UpdateCheckMessage
    {
        get => _updateCheckMessage;
        private set
        {
            if (SetProperty(ref _updateCheckMessage, value))
            {
                Touch();
            }
        }
    }

    /// <summary>The running version compared against releases; overridable by tests.</summary>
    internal string CurrentVersion { get; set; } = BuildInfo.Version;

    // MARK: Global shortcut

    /// <summary>
    /// Re-registers the global shortcut from settings. Registering unregisters first, so calling
    /// this after every Settings change cannot leave a stale hot key behind.
    /// </summary>
    public void ApplyHotKey()
    {
        HotKeys.Unregister();
        HotKeyError = null;
        if (Settings.HotKey is not { } binding || Settings.HotKeyProfileId is null)
        {
            HotKeys.OnFire = null;
            return;
        }

        HotKeys.OnFire = () => Post(() => _ = HotKeyFiredAsync());
        try
        {
            HotKeys.Register(binding);
        }
        catch (InvalidOperationException e)
        {
            HotKeyError = e.Message;
            LogError($"Global shortcut: {e.Message}");
        }
    }

    private async Task HotKeyFiredAsync()
    {
        if (Settings.HotKeyProfileId is not { } id)
        {
            return;
        }

        try
        {
            if (await QuickRunAsync(id))
            {
                return;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogError($"Global shortcut: {Describe(e)}");
            return;
        }

        // Needs a justification, ticket or duration: open the Run window instead.
        RequestRun(id);
        PendingProfileRun = id;
    }

    // MARK: Diagnostics

    /// <summary>
    /// The plain-text report behind "Copy diagnostics". Everything it carries is already visible in
    /// the app; no client id, token or secret is passed to the renderer at all.
    /// </summary>
    public string DiagnosticsText()
    {
        var accounts = State.Identities
            .Select(i => new DiagnosticsAccount(i.Upn, i.SignInMethod.DisplayName, State.TenantsFor(i.Id).Count))
            .ToList();
        var tenants = State.Tenants.Select(t =>
        {
            var flags = new List<string>();
            if (t.DiscoveryMode == DiscoveryMode.ManualRoles)
            {
                flags.Add("manual roles");
            }

            if (t.AzureUnavailableReason is not null)
            {
                flags.Add("Azure off");
            }

            if (t.GroupsUnavailableReason is not null)
            {
                flags.Add("Groups off");
            }

            if (t.EntraActivation?.Reason is not null)
            {
                flags.Add("Entra view only");
            }

            if (t.LastDiscoveryError is not null)
            {
                flags.Add("discovery error");
            }

            var mode = t.DiscoveryMode == DiscoveryMode.ManualRoles ? "manualRoles" : "automatic";
            return new DiagnosticsTenant(t.DisplayName, t.TenantId, mode, flags);
        }).ToList();
        string? hotKey = null;
        if (Settings.HotKey is { } binding)
        {
            var profile = Settings.HotKeyProfileId is { } id ? State.Profile(id) : null;
            hotKey = $"{binding.Display} → {profile?.Name ?? "no profile"}";
        }

        var input = new DiagnosticsInput(
            CurrentVersion, BuildInfo.Build, BuildInfo.SigningDescription, BuildInfo.OsDescription,
            accounts, tenants, [.. State.Profiles.Select(p => p.Name)], hotKey, ErrorLog.Entries);
        return DiagnosticsReport.Render(input);
    }

    // MARK: Updates

    /// <summary>
    /// Asks GitHub for the latest Windows release and compares it with the running version.
    /// The automatic call at startup is throttled to once a day; the Settings button forces a
    /// check. A release the user dismissed is never offered again, but a forced check still
    /// reports it, so "Check for updates" is never silent.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force)
        {
            if (Settings.LastUpdateCheck is { } last && (DateTimeOffset.UtcNow - last).Duration() < TimeSpan.FromHours(24))
            {
                return;
            }

            if (!IsOnline)
            {
                return;
            }
        }

        try
        {
            var latest = await new UpdateChecker(Http).LatestAsync(ct);
            Settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            if (latest is null)
            {
                UpdateCheckMessage = "No releases yet";
                return;
            }

            var version = latest.Version;
            if (!AppVersion.IsNewer(version, CurrentVersion))
            {
                UpdateCheckMessage = "You have the latest version";
                return;
            }

            UpdateCheckMessage = $"Elevate {version} is available";
            // The message is set before the dismissal guard, so Settings always reports what the
            // check found even when the banner stays suppressed because this version was dismissed.
            if (!force && Settings.DismissedUpdateVersion == version)
            {
                return;
            }

            UpdateAvailable = (version, latest.Url);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            var message = Describe(e);
            UpdateCheckMessage = $"Could not check for updates: {message}";
            LogError($"Update check: {message}");
        }
    }

    /// <summary>Hides the update banner and remembers not to raise it again for this release.</summary>
    public void DismissUpdate()
    {
        if (UpdateAvailable is { } update)
        {
            Settings.DismissedUpdateVersion = update.Version;
        }

        UpdateAvailable = null;
    }
}
