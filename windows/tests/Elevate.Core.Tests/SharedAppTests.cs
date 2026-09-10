using Elevate.Core.Auth;
using Elevate.Core.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>The shared Elevate app constants, its consent link, and the diagnostics line that names it.</summary>
public class SharedAppTests
{
    private const string OtherClientId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void IsSharedClientIdIgnoresCaseAndWhitespace()
    {
        SharedApp.IsSharedClientId($"  {SharedApp.ClientId.ToUpperInvariant()} ").Should().BeTrue();
        SharedApp.IsSharedClientId(OtherClientId).Should().BeFalse();
        SharedApp.IsSharedClientId(null).Should().BeFalse();
    }

    [Fact]
    public void AdminConsentUriPicksRedirectByClientId()
    {
        var shared = SharedApp.AdminConsentUri(SharedApp.ClientId, "organizations").ToString();
        shared.Should().StartWith("https://login.microsoftonline.com/organizations/v2.0/adminconsent?")
            .And.Contain(Uri.EscapeDataString(SharedApp.ConsentRedirectUri));

        var own = SharedApp.AdminConsentUri(OtherClientId, "tenant-1").ToString();
        own.Should().StartWith("https://login.microsoftonline.com/tenant-1/v2.0/adminconsent?")
            .And.Contain(Uri.EscapeDataString(SharedApp.NativeClientRedirectUri))
            .And.NotContain("consent.html");
    }

    private static DiagnosticsInput Input(bool shared, bool configured) =>
        new("1.2.3", "45", "Unsigned", "Windows 11 26200", [], [], [], null, [], null, shared, configured);

    [Fact]
    public void DiagnosticsNamesTheKindOfClientIdNeverTheId()
    {
        DiagnosticsReport.Render(Input(shared: true, configured: true))
            .Should().Contain("Client id: shared Elevate app").And.NotContain(SharedApp.ClientId);
        DiagnosticsReport.Render(Input(shared: false, configured: true)).Should().Contain("Client id: own registration");
        DiagnosticsReport.Render(Input(shared: false, configured: false)).Should().Contain("Client id: not set");
    }
}
