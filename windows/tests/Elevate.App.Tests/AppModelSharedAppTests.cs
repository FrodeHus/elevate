using System.Web;
using Elevate.App.Services;
using Elevate.App.Tests.Support;
using Elevate.App.ViewModels;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.App.Tests;

/// <summary>
/// Port of the macOS <c>AppModelSharedAppTests</c>: <see cref="AppSettings.UsesSharedClientId"/>,
/// <see cref="AppModel.UsesSharedApp"/> and <see cref="AppModel.SharedAppAdminConsentUrl"/>, and the
/// diagnostics line that names the shared app without ever printing the id itself.
/// </summary>
public class AppModelSharedAppTests
{
    private const string OtherClientId = "11111111-2222-3333-4444-555555555555";

    private static string Query(Uri url, string name) => HttpUtility.ParseQueryString(url.Query)[name]!;

    [Fact]
    public void UsesSharedClientIdMatchesAnyCaseWithWhitespace()
    {
        using var test = new TestModel(clientId: $"  {AppSettings.SharedClientId.ToUpperInvariant()}  ");
        test.Settings.UsesSharedClientId.Should().BeTrue();
    }

    [Fact]
    public void UsesSharedClientIdFalseForAnotherGuidOrEmpty()
    {
        using var other = new TestModel(clientId: OtherClientId);
        other.Settings.UsesSharedClientId.Should().BeFalse();
        using var empty = new TestModel(clientId: "");
        empty.Settings.UsesSharedClientId.Should().BeFalse();
    }

    [Fact]
    public async Task SharedAppAdminConsentUrlIsNullWhenUnconfigured()
    {
        using var test = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider(), clientId: "");
        test.Model.IsConfigured.Should().BeFalse();
        test.Model.SharedAppAdminConsentUrl().Should().BeNull();
    }

    [Fact]
    public async Task SharedAppAdminConsentUrlWhenConfiguredWithSharedId()
    {
        using var test = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider(), clientId: AppSettings.SharedClientId);
        test.Model.IsConfigured.Should().BeTrue();
        test.Model.UsesSharedApp.Should().BeTrue();

        var url = test.Model.SharedAppAdminConsentUrl();

        url.Should().NotBeNull();
        url!.Host.Should().Be("login.microsoftonline.com");
        url.AbsolutePath.Should().Be("/organizations/v2.0/adminconsent");
        Query(url, "client_id").Should().Be(AppSettings.SharedClientId);
        Query(url, "redirect_uri").Should().Be(AppSettings.SharedConsentRedirectUri);
        Query(url, "scope").Should().Contain("RoleAssignmentSchedule.ReadWrite.Directory");
    }

    [Fact]
    public async Task SharedAppAdminConsentUrlIsNullForAnotherGuid()
    {
        using var test = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider(), clientId: OtherClientId);
        test.Model.IsConfigured.Should().BeTrue();
        test.Model.UsesSharedApp.Should().BeFalse();
        test.Model.SharedAppAdminConsentUrl().Should().BeNull();
    }

    [Fact]
    public async Task PerTenantAdminConsentUrlUsesTenantIdAndConsentPageRedirectForSharedApp()
    {
        using var test = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider(), clientId: AppSettings.SharedClientId);
        test.Model.State.Identities.Add(Sample.Identity(method: SignInMethod.OwnApp));

        var url = test.Model.AdminConsentUrl(Sample.IdentityId, Sample.TenantId);

        url.Should().NotBeNull();
        url!.AbsolutePath.Should().Be($"/{Sample.TenantId}/v2.0/adminconsent");
        Query(url, "redirect_uri").Should().Be(AppSettings.SharedConsentRedirectUri);
    }

    [Fact]
    public async Task PerTenantAdminConsentUrlKeepsNativeClientRedirectForOwnRegistration()
    {
        using var test = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider(), clientId: OtherClientId);
        test.Model.State.Identities.Add(Sample.Identity(method: SignInMethod.OwnApp));

        var url = test.Model.AdminConsentUrl(Sample.IdentityId, Sample.TenantId);

        url.Should().NotBeNull();
        Query(url!, "redirect_uri").Should().Be(SharedApp.NativeClientRedirectUri);
    }

    [Fact]
    public async Task ApplyingTheSharedIdMakesItTheOneInEffect()
    {
        using var test = await TestModel.BootstrappedAsync(
            ownApp: new FakeOwnAppProvider(), ownAppFactory: _ => new FakeOwnAppProvider(), clientId: OtherClientId);

        test.Model.ApplyClientId(AppSettings.SharedClientId);

        test.Model.UsesSharedApp.Should().BeTrue();
        test.Model.SharedAppAdminConsentUrl().Should().NotBeNull();
    }

    [Fact]
    public async Task DiagnosticsNamesSharedAppWithoutTheIdItself()
    {
        using var test = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider(), clientId: AppSettings.SharedClientId);
        var text = test.Model.DiagnosticsText();
        text.Should().Contain("Client id: shared Elevate app").And.NotContain(AppSettings.SharedClientId);
    }

    [Fact]
    public async Task DiagnosticsShowsOwnRegistrationForOtherClientId()
    {
        using var test = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider(), clientId: OtherClientId);
        var text = test.Model.DiagnosticsText();
        text.Should().Contain("Client id: own registration")
            .And.NotContain("shared Elevate app").And.NotContain("not set").And.NotContain(OtherClientId);
    }

    [Fact]
    public async Task DiagnosticsShowsNotSetWhenUnconfigured()
    {
        using var test = await TestModel.BootstrappedAsync(ownApp: new FakeOwnAppProvider(), clientId: "");
        var text = test.Model.DiagnosticsText();
        text.Should().Contain("Client id: not set").And.NotContain("own registration").And.NotContain("shared Elevate app");
    }
}
