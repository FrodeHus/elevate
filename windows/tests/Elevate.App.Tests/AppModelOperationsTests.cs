using Elevate.App.Services;
using Elevate.App.Tests.Support;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.App.Tests;

public class AppModelOperationsTests
{
    /// <summary>A draft, a macOS-only hotfix without an MSI, the newest full release, and the older ones.</summary>
    private const string Releases = """
        [
          {"tag_name":"v2.0.0","html_url":"https://github.com/FrodeHus/elevate/releases/tag/v2.0.0","draft":true,"assets":[{"name":"Elevate-2.0.0-x64.msi"}]},
          {"tag_name":"v1.1.1","html_url":"https://github.com/FrodeHus/elevate/releases/tag/v1.1.1","assets":[{"name":"Elevate-1.1.1.dmg"}]},
          {"tag_name":"v1.1.0","html_url":"https://github.com/FrodeHus/elevate/releases/tag/v1.1.0","prerelease":false,"assets":[{"name":"Elevate-1.1.0.dmg"},{"name":"Elevate-1.1.0-x64.msi"},{"name":"Elevate-1.1.0-arm64.msi"}]},
          {"tag_name":"windows-v1.0.0","html_url":"https://github.com/FrodeHus/elevate/releases/tag/windows-v1.0.0","assets":[{"name":"Elevate-1.0.0-x64.msi"}]}
        ]
        """;

    [Fact]
    public async Task UpdateCheckerPicksTheNewestPublishedReleaseWithAnInstaller()
    {
        var http = new StubHttpClient();
        http.On("GET", "releases", Releases);
        var latest = await new UpdateChecker(http).LatestAsync();
        latest!.Tag.Should().Be("v1.1.0");
        latest.Version.Should().Be("1.1.0");
        latest.Url.AbsoluteUri.Should().EndWith("/v1.1.0");
        http.Requests[0].Headers["User-Agent"].Should().Be("Elevate");

        http.On("GET", "releases", "[]");
        (await new UpdateChecker(http).LatestAsync()).Should().BeNull();
        http.On("GET", "releases", "", 404);
        (await new UpdateChecker(http).LatestAsync()).Should().BeNull();
        http.On("GET", "releases", "oops", 500);
        var act = () => new UpdateChecker(http).LatestAsync();
        (await act.Should().ThrowAsync<PimException>()).Which.Status.Should().Be(500);
    }

    [Fact]
    public async Task CheckForUpdatesOffersANewerVersionOnceUntilDismissed()
    {
        var http = new StubHttpClient();
        http.On("GET", "releases", Releases);
        using var test = await TestModel.BootstrappedAsync(http: http, online: true);
        var model = test.Model;
        model.CurrentVersion = "1.0.0";

        await model.CheckForUpdatesAsync(force: true);

        model.UpdateAvailable.Should().NotBeNull();
        model.UpdateAvailable!.Value.Version.Should().Be("1.1.0");
        model.UpdateCheckMessage.Should().Be("Elevate 1.1.0 is available");
        test.Settings.LastUpdateCheck.Should().NotBeNull();

        model.DismissUpdate();
        model.UpdateAvailable.Should().BeNull();
        test.Settings.DismissedUpdateVersion.Should().Be("1.1.0");

        // The daily check is throttled and honours the dismissal; a forced check reports again.
        await model.CheckForUpdatesAsync();
        model.UpdateAvailable.Should().BeNull();
        test.Settings.LastUpdateCheck = DateTimeOffset.UtcNow.AddDays(-2);
        await model.CheckForUpdatesAsync();
        model.UpdateAvailable.Should().BeNull();
        model.UpdateCheckMessage.Should().Be("Elevate 1.1.0 is available");
        await model.CheckForUpdatesAsync(force: true);
        model.UpdateAvailable.Should().NotBeNull();
    }

    [Fact]
    public async Task CheckForUpdatesReportsUpToDateAndFailures()
    {
        var http = new StubHttpClient();
        http.On("GET", "releases", Releases);
        using var test = await TestModel.BootstrappedAsync(http: http, online: true);
        var model = test.Model;
        model.CurrentVersion = "1.1.0";

        await model.CheckForUpdatesAsync(force: true);
        model.UpdateCheckMessage.Should().Be("You have the latest version");
        model.UpdateAvailable.Should().BeNull();

        http.On("GET", "releases", "down", 503);
        await model.CheckForUpdatesAsync(force: true);
        model.UpdateCheckMessage.Should().StartWith("Could not check for updates");
        model.ErrorLog.Entries.Should().Contain(e => e.Message.StartsWith("Update check:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnosticsTextCarriesAccountsTenantsProfilesAndErrorsButNoClientId()
    {
        var state = new AppState
        {
            Identities = [Sample.Identity()],
            Tenants = [Sample.Tenant() with { DiscoveryMode = DiscoveryMode.ManualRoles, AzureUnavailableReason = "no" }],
        };
        const string clientId = "9d3a7e2c-4b1f-4a6e-9c2d-5f8b1c0a7e3d";
        using var test = await TestModel.BootstrappedAsync(state, clientId: clientId);
        var model = test.Model;
        model.SaveProfile("Ops", [Sample.EntraKey]);
        test.Settings.HotKey = new HotKeyBinding(HotKeyBinding.ModControl | HotKeyBinding.ModShift, 0x45, "Ctrl+Shift+E");
        test.Settings.HotKeyProfileId = model.Profiles[0].Id;
        model.LogError("something broke token=abc");

        var text = model.DiagnosticsText();

        text.Should().Contain("id-1@example.com — Own app registration — 1 tenant(s)");
        text.Should().Contain("Contoso (tenant-1) — mode: manualRoles — flags: manual roles, Azure off");
        text.Should().Contain("  Ops");
        text.Should().Contain("Hot key: Ctrl+Shift+E → Ops");
        text.Should().Contain("something broke token=abc");
        text.Should().NotContain(clientId);
    }

    [Fact]
    public async Task ApplyHotKeyRegistersOnlyWithABindingAndAProfileAndReportsFailures()
    {
        using var test = await TestModel.BootstrappedAsync(online: true);
        var model = test.Model;
        var profile = model.SaveProfile("Ops", [Sample.EntraKey]);

        test.Settings.HotKey = new HotKeyBinding(HotKeyBinding.ModControl, 0x45, "Ctrl+E");
        model.ApplyHotKey();
        test.HotKeys.Registered.Should().BeNull();

        test.Settings.HotKeyProfileId = profile.Id;
        model.ApplyHotKey();
        test.HotKeys.Registered.Should().Be(test.Settings.HotKey);
        test.HotKeys.OnFire.Should().NotBeNull();
        model.HotKeyError.Should().BeNull();

        // The shortcut needs input (no remembered reason): firing it asks the shell for the Run window.
        test.HotKeys.OnFire!();
        await Task.Delay(50);
        model.PendingProfileRun.Should().Be(profile.Id);
        model.RunRequests[profile.Id].Should().Be(1);
    }

    [Fact]
    public async Task SettingsRoundTripTheNewFields()
    {
        using var test = await TestModel.BootstrappedAsync();
        var id = Guid.NewGuid();
        test.Settings.HotKey = new HotKeyBinding(HotKeyBinding.ModAlt, 0x41, "Alt+A");
        test.Settings.HotKeyProfileId = id;
        test.Settings.SeenApprovalIds = new HashSet<string> { "b", "a" };
        test.Settings.LastApprovalJustification = "ok";
        test.Settings.DismissedUpdateVersion = "1.1.0";
        test.Settings.LastUpdateCheck = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        var reloaded = new AppSettings(test.Directory);

        reloaded.HotKey.Should().Be(test.Settings.HotKey);
        reloaded.HotKeyProfileId.Should().Be(id);
        reloaded.SeenApprovalIds.Should().BeEquivalentTo(["a", "b"]);
        reloaded.LastApprovalJustification.Should().Be("ok");
        reloaded.DismissedUpdateVersion.Should().Be("1.1.0");
        reloaded.LastUpdateCheck.Should().Be(test.Settings.LastUpdateCheck);
    }

    [Fact]
    public async Task ErrorLogIsClippedAndCapped()
    {
        using var test = await TestModel.BootstrappedAsync();
        test.Model.LogError(new string('x', 400));
        test.Model.LogError("   ");
        test.Model.ErrorLog.Entries.Should().ContainSingle().Which.Message.Should().HaveLength(300);
    }
}
