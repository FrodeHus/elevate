using Elevate.App.Services;
using Elevate.App.Tests.Support;
using Elevate.Core.Catalogue;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.App.Tests;

public class AppModelRefreshTests
{
    private static AppState ManualState() => new()
    {
        Identities = [Sample.Identity()],
        Tenants = [Sample.Tenant() with { DiscoveryMode = DiscoveryMode.ManualRoles }],
        ManualRoles =
        [
            new ManualRole(Sample.TenantKey, new EntraDirectoryScope("role-def", "/"), "Global Reader"),
            new ManualRole(Sample.TenantKey, new AzureResourceScope("/subscriptions/s1", "Contributor"), "Contributor"),
        ],
    };

    [Fact]
    public async Task ManualRolesAreListedEvenWhenEveryProviderFails()
    {
        // No own-app provider: every token acquisition fails with "configure a client id".
        using var test = await TestModel.BootstrappedAsync(ManualState(), online: true, tokens: new FailingTokenProvider());
        var model = test.Model;

        model.RolesFor(Sample.TenantKey, PanelTab.Roles).Select(r => r.DisplayName).Should().Equal("Global Reader");
        model.RolesFor(Sample.TenantKey, PanelTab.Azure).Select(r => r.DisplayName).Should().Equal("Contributor");
        model.TenantErrors.Should().ContainKey(Sample.TenantKey);
        model.Busy.Should().BeEmpty();
    }

    [Fact]
    public async Task ManualRolesAreListedOffline()
    {
        using var test = await TestModel.BootstrappedAsync(ManualState());
        await test.Model.RefreshAsync(Sample.TenantKey);
        test.Model.RolesFor(Sample.TenantKey, PanelTab.Roles).Should().ContainSingle(r => r.Source == RoleSource.Manual);
    }

    [Fact]
    public async Task DiscoveredRolesWinOverManualOnesAndGetTheirPolicies()
    {
        var http = new StubHttpClient();
        http.On("GET", "/me", """{"id":"principal-1"}""");
        http.On("GET", "roleEligibilitySchedules/filterByCurrentUser", Fixtures.Text("entra-eligible"));
        http.On("GET", "roleAssignmentScheduleInstances", """{"value":[]}""");
        http.On("GET", "roleAssignmentScheduleRequests", """{"value":[]}""");
        http.On("GET", "roleManagementPolicyAssignments", Fixtures.Text("entra-policy"));
        http.On("GET", "management.azure.com", """{"value":[]}""");
        http.On("GET", "privilegedAccess/group", """{"value":[]}""");
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        var tokens = new FakeTokenProvider();
        tokens.AddIdentity(Sample.Identity());
        using var test = await TestModel.BootstrappedAsync(state, http: http, online: true, tokens: tokens);
        var model = test.Model;

        var roles = model.RolesFor(Sample.TenantKey, PanelTab.Roles);
        roles.Should().NotBeEmpty();
        roles.Should().OnlyContain(r => r.Source == RoleSource.Discovered);
        roles.Should().BeInAscendingOrder(r => r.DisplayName, StringComparer.Ordinal);
        model.TenantErrors.Should().NotContainKey(Sample.TenantKey);
    }

    /// <summary>What the composite does for an own-app identity when no client id is configured.</summary>
    private sealed class FailingTokenProvider : Elevate.Core.Auth.ITokenProvider
    {
        public Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct) => throw new PimException(PimErrorKind.Unexpected, "Configure a client id in Settings");

        public Task SignOutAsync(Identity identity, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Identity>>([]);

        public Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct) =>
            throw new PimException(PimErrorKind.Unexpected, "Configure a client id in Settings");

        public Task<string> AcquireInteractivelyAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct) =>
            throw new PimException(PimErrorKind.Unexpected, "Configure a client id in Settings");
    }
}
