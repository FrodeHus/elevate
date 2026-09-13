using Elevate.App.Notifications;
using FluentAssertions;

namespace Elevate.App.Tests;

public class NotificationAppIdTests
{
    private const string Exe = @"C:\Program Files\Elevate\Elevate.exe";

    [Fact]
    public void Find_ReturnsTheIdWhoseActivatorLaunchesThisExe()
    {
        var entries = new[]
        {
            new AppUserModelEntry("Microsoft.Windows.Explorer", null),
            new AppUserModelEntry("{AAAA}", "{CLSID-OTHER}"),
            new AppUserModelEntry("{3BFB7B69-45D5-434F-9788-2F58C4F0CBD5}", "{CLSID-ELEVATE}"),
        };
        var servers = new Dictionary<string, string>
        {
            ["{CLSID-OTHER}"] = "\"C:\\Other\\other.exe\" ----AppNotificationActivated:",
            ["{CLSID-ELEVATE}"] = "\"c:\\program files\\elevate\\elevate.exe\" ----AppNotificationActivated:",
        };

        NotificationAppId.Find(Exe, entries, clsid => servers.GetValueOrDefault(clsid)).Should().Be("{3BFB7B69-45D5-434F-9788-2F58C4F0CBD5}");
    }

    [Fact]
    public void Find_ReturnsNullWhenNoActivatorMatches()
    {
        var entries = new[] { new AppUserModelEntry("{AAAA}", "{CLSID-OTHER}"), new AppUserModelEntry("{BBBB}", "{MISSING}") };
        var servers = new Dictionary<string, string> { ["{CLSID-OTHER}"] = "\"C:\\Other\\other.exe\"" };

        NotificationAppId.Find(Exe, entries, clsid => servers.GetValueOrDefault(clsid)).Should().BeNull();
    }
}
