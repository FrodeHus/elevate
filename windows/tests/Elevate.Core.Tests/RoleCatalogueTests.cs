using Elevate.Core.Catalogue;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class RoleCatalogueTests
{
    private static readonly TenantKey Tenant = new("i", "t");

    [Fact]
    public void LoadsBuiltInRoles()
    {
        var roles = RoleCatalogue.EntraBuiltInRoles();

        roles.Count.Should().BeGreaterThanOrEqualTo(130);
        var ga = roles.Single(r => r.DisplayName == "Global Administrator");
        ga.TemplateId.Should().Be("62e90394-69f5-4237-9190-012177145e10");
        ga.Id.Should().Be(ga.TemplateId);
        ga.IsPrivileged.Should().BeTrue();
        roles.Should().BeInAscendingOrder(r => r.DisplayName, StringComparer.Ordinal);
    }

    [Fact]
    public void ManualRolesBecomeEligibleRolesWithDefaultPolicy()
    {
        var manual = new[]
        {
            new ManualRole(Tenant, new EntraDirectoryScope("62e90394-69f5-4237-9190-012177145e10", "/"), "Global Administrator"),
            new ManualRole(new TenantKey("i", "other"), new GroupScope("g", GroupAccess.Member), "Ops"),
        };

        var roles = ManualRoleSource.EligibleRoles(manual, Tenant);

        roles.Should().ContainSingle();
        roles[0].Source.Should().Be(RoleSource.Manual);
        roles[0].Policy.Should().Be(RolePolicy.ManualDefault);
        roles[0].Key.Should().Be(new RoleKey("i", "t", new EntraDirectoryScope("62e90394-69f5-4237-9190-012177145e10", "/")));
    }

    [Fact]
    public void ManualGroupRoleCarriesAccessAsDetail()
    {
        var manual = new[]
        {
            new ManualRole(Tenant, new GroupScope("g", GroupAccess.Owner), "Owners"),
            new ManualRole(Tenant, new GroupScope("h", GroupAccess.Member), "Members"),
        };

        ManualRoleSource.EligibleRoles(manual, Tenant).Select(r => r.Detail).Should().Equal("owner", "member");
    }

    [Fact]
    public void MergePrefersDiscovered()
    {
        var key = new RoleKey("i", "t", new EntraDirectoryScope("r", "/"));
        var discovered = new EligibleRole(key, "Disc", RoleSource.Discovered, RolePolicy.ManualDefault);
        var manual = new EligibleRole(key, "Man", RoleSource.Manual, RolePolicy.ManualDefault);
        var other = new EligibleRole(
            new RoleKey("i", "t", new EntraDirectoryScope("x", "/")), "X", RoleSource.Manual, RolePolicy.ManualDefault);

        var merged = ManualRoleSource.Merge([discovered], [manual, other]);

        merged.Select(r => r.DisplayName).Should().Equal("Disc", "X");
    }

    [Fact]
    public void ManualAzureRoleCarriesScopeAsDetail()
    {
        var manual = new[] { new ManualRole(Tenant, new AzureResourceScope("/subscriptions/sub-1", "Contributor"), "Contributor") };

        var roles = ManualRoleSource.EligibleRoles(manual, Tenant);

        roles[0].Detail.Should().Be("/subscriptions/sub-1");
        roles[0].DisplayName.Should().Be("Contributor");
    }

    [Fact]
    public void MergeDropsManualAzureRoleMatchingDiscoveredByScopeAndName()
    {
        var discovered = new EligibleRole(
            new RoleKey("i", "t", new AzureResourceScope(
                "/subscriptions/SUB-1", "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac")),
            "Contributor", RoleSource.Discovered, RolePolicy.ManualDefault, "Pay-As-You-Go · subscription");
        var manualSame = new EligibleRole(
            new RoleKey("i", "t", new AzureResourceScope("/subscriptions/sub-1", "contributor")),
            "contributor", RoleSource.Manual, RolePolicy.ManualDefault, "/subscriptions/sub-1");
        var manualOther = new EligibleRole(
            new RoleKey("i", "t", new AzureResourceScope("/subscriptions/sub-2", "Reader")),
            "Reader", RoleSource.Manual, RolePolicy.ManualDefault, "/subscriptions/sub-2");

        var merged = ManualRoleSource.Merge([discovered], [manualSame, manualOther]);

        merged.Select(r => r.DisplayName).Should().Equal("Contributor", "Reader");
    }

    [Fact]
    public void DetailRoundTripsAndDefaultsToNull()
    {
        var key = new RoleKey("i", "t", new EntraDirectoryScope("r", "/"));

        new EligibleRole(key, "X", RoleSource.Discovered, RolePolicy.ManualDefault).Detail.Should().BeNull();

        var json = Json.Serialize(new EligibleRole(key, "X", RoleSource.Manual, RolePolicy.ManualDefault, "d"));
        Json.Deserialize<EligibleRole>(json)!.Detail.Should().Be("d");
    }

    [Fact]
    public void ManualRoleRoundTripsThroughJson()
    {
        var role = new ManualRole(Tenant, new AzureResourceScope("/subscriptions/s", "Reader"), "Reader");

        var json = Json.Serialize(role);

        json.Should().Be(
            """{"tenantKey":{"identityId":"i","tenantId":"t"},"scope":{"azureResource":{"scope":"/subscriptions/s","roleDefinitionId":"Reader"}},"displayName":"Reader"}""");
        Json.Deserialize<ManualRole>(json).Should().Be(role);
    }
}
