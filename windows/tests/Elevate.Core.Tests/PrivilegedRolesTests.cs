using System.Text.Json.Nodes;
using Elevate.Core.Catalogue;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Support;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class PrivilegedRolesTests
{
    private const string Owner = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
    private const string Contributor = "b24988ac-6180-42a0-ab88-20f7382dd24c";
    private const string UserAccessAdministrator = "18d7d88d-d35e-4fb5-a5c3-7773c20a72d9";
    private const string RbacAdministrator = "f58310d9-a9f6-439a-9e8d-f62e7b41a168";
    private const string Reader = "acdd72a7-3385-48ef-bd42-f606fba81ae7";
    private const string GlobalAdministrator = "62e90394-69f5-4237-9190-012177145e10";
    private const string GroupsAdministrator = "fdd7a751-b60b-444a-984c-02652fe8fa1c";

    private static readonly Identity TestIdentity = new("id1", "alex.rivera@contoso.com", "Alex", "t1");
    private static readonly TenantContext Tenant = new("id1", "t1", "Contoso", TenantSource.Home);
    private static readonly TenantKey TKey = new("id1", "t1");

    private static async Task<IReadOnlyList<EligibleRole>> EntraRoles(string roleDefinitionJson)
    {
        var http = new StubHttpClient();
        http.On("GET", "roleEligibilitySchedules/filterByCurrentUser", $$"""
            {"value":[{"id":"e1","roleDefinitionId":"r1","directoryScopeId":"/","roleDefinition":{{roleDefinitionJson}}}]}
            """);
        return await new EntraDirectoryProvider(http, new FakeTokenProvider()).EligibleRolesAsync(TestIdentity, Tenant);
    }

    [Theory]
    [InlineData("""{"id":"r1","displayName":"A","isPrivileged":true}""", true)]
    [InlineData("""{"id":"r1","displayName":"A","isPrivileged":false}""", false)]
    [InlineData("""{"id":"r1","displayName":"A"}""", false)]
    [InlineData("""{"id":"r1","displayName":"A","isPrivileged":null}""", false)]
    public async Task EntraDecodesTheDefinitionsFlag(string definition, bool expected)
    {
        var roles = await EntraRoles(definition);

        roles.Single().IsPrivileged.Should().Be(expected);
    }

    [Theory]
    [InlineData(Owner)]
    [InlineData(Contributor)]
    [InlineData(UserAccessAdministrator)]
    [InlineData(RbacAdministrator)]
    public void AzureRecognisesTheFourPrivilegedIdsInAnyShape(string guid)
    {
        PrivilegedRoles.IsAzure(guid).Should().BeTrue();
        PrivilegedRoles.IsAzure(guid.ToUpperInvariant()).Should().BeTrue();
        PrivilegedRoles.IsAzure($"/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/{guid.ToUpperInvariant()}").Should().BeTrue();
    }

    [Fact]
    public void AzureDoesNotMarkOtherRoles()
    {
        PrivilegedRoles.IsAzure(Reader).Should().BeFalse();
        PrivilegedRoles.IsAzure($"/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/{Reader}").Should().BeFalse();
        PrivilegedRoles.IsAzure("").Should().BeFalse();
    }

    [Fact]
    public async Task AzureProviderMarksContributorButNotReader()
    {
        var http = new StubHttpClient();
        http.On("GET", "roleEligibilityScheduleInstances?", body: Fixtures.Data("arm-eligible"));
        http.On("GET", "skiptoken=page2", body: Fixtures.Data("arm-eligible-page2"));

        var roles = await new AzureResourceProvider(http, new FakeTokenProvider()).EligibleRolesAsync(TestIdentity, Tenant);

        roles.Single(r => r.DisplayName == "Contributor").IsPrivileged.Should().BeTrue();
        roles.Single(r => r.DisplayName == "Reader").IsPrivileged.Should().BeFalse();
    }

    [Fact]
    public void CatalogueSaysWhichEntraBuiltInsArePrivileged()
    {
        PrivilegedRoles.IsKnown(new EntraDirectoryScope(GlobalAdministrator, "/")).Should().BeTrue();
        PrivilegedRoles.IsKnown(new EntraDirectoryScope(GlobalAdministrator.ToUpperInvariant(), "/")).Should().BeTrue();
        PrivilegedRoles.IsKnown(new EntraDirectoryScope(GroupsAdministrator, "/")).Should().BeFalse();
        PrivilegedRoles.IsKnown(new EntraDirectoryScope("not-a-built-in", "/")).Should().BeFalse();
        PrivilegedRoles.IsKnown(new AzureResourceScope("/subscriptions/s", Owner)).Should().BeTrue();
    }

    [Fact]
    public void GroupsAreNeverPrivileged()
    {
        PrivilegedRoles.IsKnown(new GroupScope(GlobalAdministrator, GroupAccess.Owner)).Should().BeFalse();
    }

    [Fact]
    public void ManualRolesCarryTheFlag()
    {
        var manual = new[]
        {
            new ManualRole(TKey, new EntraDirectoryScope(GlobalAdministrator, "/"), "Global Administrator"),
            new ManualRole(TKey, new EntraDirectoryScope(GroupsAdministrator, "/"), "Groups Administrator"),
            new ManualRole(TKey, new AzureResourceScope("/subscriptions/s", Owner), "Owner"),
            new ManualRole(TKey, new AzureResourceScope("/subscriptions/s", Reader), "Reader"),
            new ManualRole(TKey, new GroupScope("g", GroupAccess.Member), "Ops"),
        };

        ManualRoleSource.EligibleRoles(manual, TKey).Select(r => r.IsPrivileged)
            .Should().Equal(true, false, true, false, false);
    }

    [Fact]
    public void ManualRoleRekeyedToTheDiscoveredScopeStaysPrivileged()
    {
        var manual = new ManualRole(TKey, new AzureResourceScope("/subscriptions/s", "Owner"), "Owner");
        var role = ManualRoleSource.EligibleRoles([manual], TKey).Single();
        role.IsPrivileged.Should().BeFalse("a name-only id is not recognised until it is resolved");

        // Rekey replaces the manual scope with the resolved one; the list is rebuilt from it.
        var rekeyed = manual with { Scope = new AzureResourceScope("/subscriptions/s", $"/subscriptions/s/providers/Microsoft.Authorization/roleDefinitions/{Owner}") };

        ManualRoleSource.EligibleRoles([rekeyed], TKey).Single().IsPrivileged.Should().BeTrue();
        (role with { Key = role.Key with { Scope = rekeyed.Scope }, IsPrivileged = true }).IsPrivileged.Should().BeTrue();
    }

    [Fact]
    public void DisplayCombinesTheFlagWithTheKnownIds()
    {
        var entra = new EligibleRole(new RoleKey("i", "t", new EntraDirectoryScope(GlobalAdministrator, "/")), "GA", RoleSource.Discovered, RolePolicy.ManualDefault);
        var flagged = new EligibleRole(new RoleKey("i", "t", new EntraDirectoryScope("custom", "/")), "C", RoleSource.Discovered, RolePolicy.ManualDefault, IsPrivileged: true);
        var plain = new EligibleRole(new RoleKey("i", "t", new EntraDirectoryScope(GroupsAdministrator, "/")), "G", RoleSource.Discovered, RolePolicy.ManualDefault);

        PrivilegedRoles.IsPrivileged(entra).Should().BeTrue("the catalogue vouches even when the service did not say");
        PrivilegedRoles.IsPrivileged(flagged).Should().BeTrue();
        PrivilegedRoles.IsPrivileged(plain).Should().BeFalse();
    }

    [Fact]
    public void AnOldCacheWithoutTheFieldLoadsAsNotPrivileged()
    {
        var role = new EligibleRole(new RoleKey("i", "t", new EntraDirectoryScope("r", "/")), "R", RoleSource.Discovered, RolePolicy.ManualDefault);
        var node = JsonNode.Parse(Json.Serialize(role))!.AsObject();
        node.Remove("isPrivileged");

        Json.Deserialize<EligibleRole>(node.ToJsonString())!.IsPrivileged.Should().BeFalse();
        Json.Deserialize<EligibleRole>(Json.Serialize(role with { IsPrivileged = true }))!.IsPrivileged.Should().BeTrue();
    }
}
