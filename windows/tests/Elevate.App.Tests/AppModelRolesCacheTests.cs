using Elevate.App.Tests.Support;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.App.Tests;

/// <summary>Last session's roles are on screen at launch, before the first (slow) refresh.</summary>
public class AppModelRolesCacheTests
{
    [Fact]
    public async Task LaunchShowsLastSessionsRolesForTenantsStillConfigured()
    {
        var other = new TenantKey(Sample.IdentityId, "tenant-2");
        using var first = await TestModel.BootstrappedAsync(new AppState
        {
            Identities = [Sample.Identity()],
            Tenants = [Sample.Tenant(), Sample.Tenant(tenantId: "tenant-2", name: "Fabrikam")],
        });
        first.Model.Roles[Sample.TenantKey] = [Sample.Role(Sample.EntraKey, "Global Reader")];
        first.Model.Roles[other] = [Sample.Role(Sample.Key(new EntraDirectoryScope("x", "/"), tenantId: "tenant-2"), "Gone")];
        first.Model.SaveRolesCache();

        // Next launch: tenant-2 was removed meanwhile, so its cached rows must not come back.
        using var second = new TestModel(new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] });
        File.Copy(Path.Combine(first.Directory, AppModel.RolesCacheFile), Path.Combine(second.Directory, AppModel.RolesCacheFile));
        await second.Model.BootstrapAsync();

        second.Model.RolesFor(Sample.TenantKey).Select(r => r.DisplayName).Should().Equal("Global Reader");
        second.Model.Roles.Should().NotContainKey(other);
    }
}
