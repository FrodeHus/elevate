using Elevate.App.Tests.Support;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.App.Tests;

/// <summary>
/// Port of the macOS <c>AppModelSilentRefreshTests</c>. Background refreshes (timer, wake, launch,
/// panel open) never open a browser or a broker dialog: a tenant whose silent token acquisition
/// fails is flagged instead, and only a user-initiated refresh may prompt for it.
/// </summary>
public class AppModelSilentRefreshTests
{
    private static AppState StateWithTenant(bool accessPackages = false) => new()
    {
        Identities = [Sample.Identity()],
        Tenants = [Sample.Tenant() with { AccessPackagesAvailable = accessPackages ? true : null }],
    };

    private static FakeTokenProvider TokensNeedingSignIn(PimErrorKind kind = PimErrorKind.InteractionRequired)
    {
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity());
        tokens.SilentError = new PimException(kind, kind == PimErrorKind.ClaimsChallenge ? """{"access_token":{}}""" : null);
        return tokens;
    }

    [Fact]
    public async Task BackgroundRefreshNeverPromptsWhenSilentAcquisitionFails()
    {
        var tokens = TokensNeedingSignIn();
        // Bootstrapping online performs the launch refresh, which is a background one.
        using var test = await TestModel.BootstrappedAsync(StateWithTenant(), online: true, tokens: tokens);
        var model = test.Model;

        await model.RefreshAllAsync();

        tokens.InteractiveCalls.Should().BeEmpty();
        tokens.SilentCalls.Should().NotBeEmpty();
        model.TenantsAwaitingSignIn.Should().Contain(Sample.TenantKey);
        // Not a failure the user can do anything about from the error glyph; it is a pending sign-in.
        model.TenantErrors.Should().BeEmpty();
        model.Busy.Should().BeEmpty();
    }

    [Fact]
    public async Task BackgroundRefreshTreatsAClaimsChallengeAsAPendingSignIn()
    {
        var tokens = TokensNeedingSignIn(PimErrorKind.ClaimsChallenge);
        using var test = await TestModel.BootstrappedAsync(StateWithTenant(), online: true, tokens: tokens);

        await test.Model.RefreshAllAsync();

        tokens.InteractiveCalls.Should().BeEmpty();
        test.Model.TenantsAwaitingSignIn.Should().Contain(Sample.TenantKey);
        test.Model.TenantErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task PanelOpenIsABackgroundRefresh()
    {
        var tokens = TokensNeedingSignIn();
        using var test = await TestModel.BootstrappedAsync(StateWithTenant(), online: true, tokens: tokens);
        var model = test.Model;
        model.LastRefresh = DateTimeOffset.MinValue;

        model.PanelOpened();
        for (var i = 0; i < 50 && model.Busy.Count > 0; i++)
        {
            await Task.Delay(20);
        }

        tokens.InteractiveCalls.Should().BeEmpty();
        model.TenantsAwaitingSignIn.Should().Contain(Sample.TenantKey);
    }

    [Fact]
    public async Task UserRefreshPromptsAndClearsThePendingSignIn()
    {
        var tokens = TokensNeedingSignIn();
        using var test = await TestModel.BootstrappedAsync(StateWithTenant(), online: true, tokens: tokens);
        var model = test.Model;
        await model.RefreshAllAsync();
        model.TenantsAwaitingSignIn.Should().Contain(Sample.TenantKey);

        await model.RefreshAllAsync(userInitiated: true);

        tokens.InteractiveCalls.Should().NotBeEmpty();
        model.TenantsAwaitingSignIn.Should().NotContain(Sample.TenantKey);
    }

    [Fact]
    public async Task BackgroundRefreshThatSucceedsClearsThePendingSignIn()
    {
        var tokens = TokensNeedingSignIn();
        using var test = await TestModel.BootstrappedAsync(StateWithTenant(), online: true, tokens: tokens);
        var model = test.Model;
        await model.RefreshAllAsync();
        model.TenantsAwaitingSignIn.Should().Contain(Sample.TenantKey);

        // The session recovered on its own (e.g. the refresh token became usable again).
        tokens.SilentError = null;
        await model.RefreshAllAsync();

        tokens.InteractiveCalls.Should().BeEmpty();
        model.TenantsAwaitingSignIn.Should().NotContain(Sample.TenantKey);
    }

    [Fact]
    public async Task RemovingTheIdentityDropsTheFlag()
    {
        var tokens = TokensNeedingSignIn();
        using var test = await TestModel.BootstrappedAsync(StateWithTenant(), online: true, tokens: tokens);
        var model = test.Model;
        await model.RefreshAllAsync();
        model.TenantsAwaitingSignIn.Should().Contain(Sample.TenantKey);

        model.ForgetIdentity(Sample.IdentityId);

        model.TenantsAwaitingSignIn.Should().BeEmpty();
    }

    [Fact]
    public async Task BackgroundAccessPackagePollNeverPrompts()
    {
        var tokens = TokensNeedingSignIn();
        using var test = await TestModel.BootstrappedAsync(StateWithTenant(accessPackages: true), online: true, tokens: tokens);

        await test.Model.PollAccessPackagesIfDueAsync(force: true);

        tokens.InteractiveCalls.Should().BeEmpty();
        tokens.SilentCalls.Should().NotBeEmpty();
        test.Model.AccessPackagesPolling.Should().BeEmpty();
    }

    [Fact]
    public async Task OpeningAccessPackagesMayPrompt()
    {
        var tokens = TokensNeedingSignIn();
        using var test = await TestModel.BootstrappedAsync(StateWithTenant(accessPackages: true), online: true, tokens: tokens);

        await test.Model.PollAccessPackagesAsync(Sample.TenantKey);

        tokens.InteractiveCalls.Should().NotBeEmpty();
    }
}
