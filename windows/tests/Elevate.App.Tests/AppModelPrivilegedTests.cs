using Elevate.App.Tests.Support;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.App.Tests;

/// <summary>Whether a row wears the crown: the loaded role's flag, else the ids.</summary>
public class AppModelPrivilegedTests
{
    private const string GlobalAdministrator = "62e90394-69f5-4237-9190-012177145e10";
    private const string Owner = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";

    private static async Task<TestModel> ModelAsync() =>
        await TestModel.BootstrappedAsync(new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] });

    [Fact]
    public async Task ALoadedRolesFlagMarksItEvenForAnUnknownId()
    {
        using var t = await ModelAsync();
        t.Model.Roles[Sample.TenantKey] = [Sample.Role(Sample.EntraKey, "Custom") with { IsPrivileged = true }, Sample.Role(Sample.GroupKey, "Ops")];

        t.Model.IsPrivileged(Sample.EntraKey).Should().BeTrue();
        t.Model.IsPrivileged(Sample.GroupKey).Should().BeFalse();
    }

    [Fact]
    public async Task TheCatalogueAndTheAzureIdsVouchWhenTheServiceDidNotSay()
    {
        using var t = await ModelAsync();
        var admin = Sample.Key(new EntraDirectoryScope(GlobalAdministrator, "/"));
        var owner = Sample.Key(new AzureResourceScope("/subscriptions/s1", $"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/{Owner.ToUpperInvariant()}"));
        t.Model.Roles[Sample.TenantKey] = [Sample.Role(admin, "Global Administrator")];

        t.Model.IsPrivileged(admin).Should().BeTrue("the loaded role carries no flag, the catalogue does");
        t.Model.IsPrivileged(owner).Should().BeTrue("not loaded: the id decides");
        t.Model.IsPrivileged(Sample.AzureKey).Should().BeFalse();
    }
}
