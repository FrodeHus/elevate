using Elevate.Core.Managed;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Core.Tests.Managed;

public class ManagedProfileResolverTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string ContributorId =
        "/subscriptions/S/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c";
    private const string SecurityReaderId = "5d6b6bb7-de71-4623-b4af-96380a352509";

    private static readonly Dictionary<string, string> TenantIds = new() { ["contoso.com"] = TenantId };

    private static readonly List<TenantContext> Tenants =
    [
        new("id-1", TenantId, "Contoso", TenantSource.Home),
        new("id-2", TenantId, "Contoso", TenantSource.Home),
    ];

    private static RoleKey Key(string identity, RoleScope scope) => new(identity, TenantId, scope);

    private static RoleKey AzureKey => Key("id-1", new AzureResourceScope("/subscriptions/S", ContributorId));

    private static RoleKey EntraKey(string identity) => Key(identity, new EntraDirectoryScope(SecurityReaderId, "/"));

    private static RoleKey GroupKey => Key("id-2", new GroupScope("g-1", GroupAccess.Member));

    private static Dictionary<RoleKey, EligibleRole> Roles => new()
    {
        [AzureKey] = Role(AzureKey, "Contributor"),
        [EntraKey("id-1")] = Role(EntraKey("id-1"), "Security Reader"),
        [EntraKey("id-2")] = Role(EntraKey("id-2"), "Security Reader"),
        [GroupKey] = Role(GroupKey, "SRE on-call"),
    };

    private static EligibleRole Role(RoleKey key, string name)
        => new(key, name, RoleSource.Discovered, RolePolicy.ManualDefault);

    /// <summary>The §7.1 example, in a different case than the eligible roles carry.</summary>
    private static ManagedProfileSet Set => ManagedProfileSet.Parse("""
        {"version":1,"profiles":[{"id":"prod-incident","name":"Prod incident",
          "reason":"Incident response","pinned":true,"roles":[
            {"kind":"azureResource","tenant":"contoso.com","scope":"/SUBSCRIPTIONS/s",
             "role":"contributor","duration":"PT2H"},
            {"kind":"entraDirectory","tenant":"contoso.com","role":"SECURITY READER"},
            {"kind":"group","tenant":"contoso.com","group":"sre on-call","access":"member","duration":"PT4H"}]}]}
        """);

    [Fact]
    public void ResolvesRolesForEveryIdentityInTheTenant()
    {
        var result = ManagedProfileResolver.Resolve(Set, TenantIds, Tenants, Roles);

        result.Warnings.Should().BeEmpty();
        var profile = result.Profiles.Should().ContainSingle().Subject;
        profile.Id.Should().Be(ManagedProfileSet.ProfileId("prod-incident"));
        profile.Name.Should().Be("Prod incident");
        profile.LastJustification.Should().Be("Incident response");
        profile.Pinned.Should().BeTrue();
        profile.Source.Should().Be(ProfileSource.Managed);
        profile.Entries.Select(e => e.RoleKey).Should().Equal(AzureKey, EntraKey("id-1"), EntraKey("id-2"), GroupKey);
        profile.Entries.Select(e => e.LastDuration).Should()
            .Equal(TimeSpan.FromHours(2), null, null, TimeSpan.FromHours(4));
    }

    [Fact]
    public void MatchesByRoleDefinitionGuidAndObjectId()
    {
        var set = ManagedProfileSet.Parse($$"""
            {"profiles":[{"id":"ids","name":"By id","roles":[
              {"kind":"azureResource","tenant":"contoso.com","scope":"/subscriptions/S",
               "role":"B24988AC-6180-42A0-AB88-20F7382DD24C"},
              {"kind":"entraDirectory","tenant":"contoso.com","role":"{{SecurityReaderId}}"},
              {"kind":"group","tenant":"contoso.com","group":"G-1"}]}]}
            """);

        var result = ManagedProfileResolver.Resolve(set, TenantIds, Tenants, Roles);

        result.Warnings.Should().BeEmpty();
        result.Profiles[0].Entries.Select(e => e.RoleKey)
            .Should().Equal(AzureKey, EntraKey("id-1"), EntraKey("id-2"), GroupKey);
    }

    [Fact]
    public void AcceptsATenantGuidDirectly()
    {
        var set = ManagedProfileSet.Parse($$"""
            {"profiles":[{"id":"guid","name":"Guid","roles":[
              {"kind":"entraDirectory","tenant":"{{TenantId.ToUpperInvariant()}}","role":"Security Reader"}]}]}
            """);

        var result = ManagedProfileResolver.Resolve(set, new Dictionary<string, string>(), Tenants, Roles);

        result.Warnings.Should().BeEmpty();
        result.Profiles[0].Entries.Select(e => e.RoleKey).Should().Equal(EntraKey("id-1"), EntraKey("id-2"));
    }

    [Fact]
    public void AddsAPlaceholderEntryPerIdentityWhenNothingMatches()
    {
        var set = ManagedProfileSet.Parse("""
            {"profiles":[{"id":"miss","name":"Miss","roles":[
              {"kind":"azureResource","tenant":"contoso.com","scope":"/subscriptions/other","role":"Owner"},
              {"kind":"entraDirectory","tenant":"contoso.com","role":"Global Reader","directoryScope":"/x"},
              {"kind":"group","tenant":"contoso.com","group":"Payments","access":"owner"}]}]}
            """);

        var result = ManagedProfileResolver.Resolve(set, TenantIds, Tenants, Roles);

        result.Warnings.Should().BeEmpty();
        result.Profiles[0].Entries.Select(e => e.RoleKey).Should().Equal(
            Key("id-1", new AzureResourceScope("/subscriptions/other", "Owner")),
            Key("id-1", new EntraDirectoryScope("Global Reader", "/x")),
            Key("id-1", new GroupScope("Payments", GroupAccess.Owner)),
            Key("id-2", new AzureResourceScope("/subscriptions/other", "Owner")),
            Key("id-2", new EntraDirectoryScope("Global Reader", "/x")),
            Key("id-2", new GroupScope("Payments", GroupAccess.Owner)));
    }

    [Fact]
    public void DropsARoleNoAccountTracksTheTenantFor()
    {
        var set = ManagedProfileSet.Parse("""
            {"profiles":[{"id":"prod-incident","name":"Prod incident","roles":[
              {"kind":"entraDirectory","tenant":"fabrikam.com","role":"Security Reader"},
              {"kind":"entraDirectory","tenant":"contoso.com","role":"Security Reader"}]}]}
            """);
        var ids = new Dictionary<string, string>
        {
            ["contoso.com"] = TenantId,
            ["fabrikam.com"] = "22222222-2222-2222-2222-222222222222",
        };

        var result = ManagedProfileResolver.Resolve(set, ids, Tenants, Roles);

        result.Warnings.Should().Equal("Prod incident: no account in tenant fabrikam.com");
        result.Profiles[0].Entries.Select(e => e.RoleKey).Should().Equal(EntraKey("id-1"), EntraKey("id-2"));
    }

    [Fact]
    public void WarnsWhenADomainCouldNotBeResolved()
    {
        var set = ManagedProfileSet.Parse("""
            {"profiles":[{"id":"prod-incident","name":"Prod incident","roles":[
              {"kind":"entraDirectory","tenant":"fabrikam.com","role":"Security Reader"}]}]}
            """);

        var result = ManagedProfileResolver.Resolve(set, TenantIds, Tenants, Roles);

        result.Warnings.Should().Equal("Prod incident: could not resolve tenant fabrikam.com");
        result.Profiles[0].Entries.Should().BeEmpty();
    }

    [Fact]
    public void EmptySetResolvesToNothing()
    {
        var result = ManagedProfileResolver.Resolve(
            ManagedProfileSet.Empty, new Dictionary<string, string>(), [], new Dictionary<RoleKey, EligibleRole>());

        result.Profiles.Should().BeEmpty();
        result.Warnings.Should().BeEmpty();
    }
}
