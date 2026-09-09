using Elevate.Core.Managed;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Core.Tests.Managed;

public class ManagedProfileSetTests
{
    /// <summary>The document from the design's §7.1.</summary>
    internal const string Example = """
    {
      "version": 1,
      "profiles": [
        {
          "id": "prod-incident",
          "name": "Prod incident",
          "reason": "Incident response",
          "pinned": true,
          "roles": [
            { "kind": "azureResource", "tenant": "contoso.com",
              "scope": "/subscriptions/00000000-0000-0000-0000-000000000001",
              "role": "Contributor", "duration": "PT2H" },
            { "kind": "entraDirectory", "tenant": "contoso.com",
              "role": "Security Reader" },
            { "kind": "group", "tenant": "contoso.com",
              "group": "SRE on-call", "access": "member", "duration": "PT4H" }
          ]
        }
      ]
    }
    """;

    private static string Invalid(Action parse)
    {
        var act = () => parse();
        return act.Should().Throw<ManagedProfileException>().Which.Message;
    }

    [Fact]
    public void ParsesTheSpecExample()
    {
        var set = ManagedProfileSet.Parse(Example);

        var profile = set.Profiles.Should().ContainSingle().Subject;
        profile.Id.Should().Be("prod-incident");
        profile.Name.Should().Be("Prod incident");
        profile.Reason.Should().Be("Incident response");
        profile.Pinned.Should().BeTrue();
        profile.ProfileId.Should().Be(ManagedProfileSet.ProfileId("prod-incident"));
        profile.Roles.Should().HaveCount(3);

        profile.Roles[0].Kind.Should().Be(RoleScopeKind.AzureResource);
        profile.Roles[0].Tenant.Should().Be("contoso.com");
        profile.Roles[0].Scope.Should().Be("/subscriptions/00000000-0000-0000-0000-000000000001");
        profile.Roles[0].Role.Should().Be("Contributor");
        profile.Roles[0].Duration.Should().Be(TimeSpan.FromHours(2));

        profile.Roles[1].Kind.Should().Be(RoleScopeKind.EntraDirectory);
        profile.Roles[1].Role.Should().Be("Security Reader");
        profile.Roles[1].DirectoryScope.Should().Be("/");
        profile.Roles[1].Duration.Should().BeNull();

        profile.Roles[2].Kind.Should().Be(RoleScopeKind.Group);
        profile.Roles[2].Group.Should().Be("SRE on-call");
        profile.Roles[2].Access.Should().Be(GroupAccess.Member);
        profile.Roles[2].Duration.Should().Be(TimeSpan.FromHours(4));
    }

    [Fact]
    public void DefaultsApplyAndUnknownFieldsAreIgnored()
    {
        var set = ManagedProfileSet.Parse("""
        {"profiles":[{"id":"p","name":"P","future":42,"pinned":1,
          "roles":[{"kind":"group","tenant":"contoso.com","group":"g","extra":"x"}]}]}
        """);

        var profile = set.Profiles.Should().ContainSingle().Subject;
        profile.Pinned.Should().BeFalse();   // only a JSON true pins
        profile.Reason.Should().BeNull();
        profile.Roles[0].Access.Should().Be(GroupAccess.Member);
    }

    [Fact]
    public void RejectsAFutureVersion()
        => Invalid(() => ManagedProfileSet.Parse("""{"version":2,"profiles":[]}"""))
            .Should().Be("version 2 is not supported");

    [Fact]
    public void RejectsANonIntegerVersion()
        => Invalid(() => ManagedProfileSet.Parse("""{"version":1.9,"profiles":[]}"""))
            .Should().Be("version 1.9 is not supported");

    [Fact]
    public void RejectsATrailingNewlineInTheSlug()
        => Invalid(() => ManagedProfileSet.Parse("{\"profiles\":[{\"id\":\"prod-incident\\n\",\"name\":\"P\",\"roles\":[]}]}"))
            .Should().Be("profile 'prod-incident\n': id must match [a-z0-9-]{1,64}");

    [Fact]
    public void RequiresAName()
        => Invalid(() => ManagedProfileSet.Parse("""{"version":1,"profiles":[{"id":"prod-incident","roles":[]}]}"""))
            .Should().Be("profile 'prod-incident': name is required");

    [Fact]
    public void RejectsANonSlugId()
        => Invalid(() => ManagedProfileSet.Parse("""{"profiles":[{"id":"Prod Incident","name":"P","roles":[]}]}"""))
            .Should().Be("profile 'Prod Incident': id must match [a-z0-9-]{1,64}");

    [Fact]
    public void RejectsAMissingId()
        => Invalid(() => ManagedProfileSet.Parse("""{"profiles":[{"name":"P","roles":[]}]}"""))
            .Should().Be("profile 1: id is required");

    [Fact]
    public void RejectsDuplicateIds()
        => Invalid(() => ManagedProfileSet.Parse(
            """{"profiles":[{"id":"p","name":"One","roles":[]},{"id":"p","name":"Two","roles":[]}]}"""))
            .Should().Be("profile 'p': duplicate id");

    [Fact]
    public void RejectsAnUnknownKind()
        => Invalid(() => ManagedProfileSet.Parse(
            """{"profiles":[{"id":"x","name":"X","roles":[{"kind":"foo","tenant":"t"}]}]}"""))
            .Should().Be("profile 'x' role 1: unknown kind 'foo'");

    [Fact]
    public void AzureRoleRequiresAScope()
        => Invalid(() => ManagedProfileSet.Parse(
            """{"profiles":[{"id":"x","name":"X","roles":[{"kind":"azureResource","tenant":"t","role":"Contributor"}]}]}"""))
            .Should().Be("profile 'x' role 1: scope is required for azureResource");

    [Fact]
    public void GroupRoleRequiresAGroup()
        => Invalid(() => ManagedProfileSet.Parse(
            """{"profiles":[{"id":"x","name":"X","roles":[{"kind":"group","tenant":"t"}]}]}"""))
            .Should().Be("profile 'x' role 1: group is required");

    [Fact]
    public void RoleRequiresATenantARoleNameAndAValidDurationAndAccess()
    {
        Invalid(() => ManagedProfileSet.Parse(
            """{"profiles":[{"id":"x","name":"X","roles":[{"kind":"group","group":"g"}]}]}"""))
            .Should().Be("profile 'x' role 1: tenant is required");

        Invalid(() => ManagedProfileSet.Parse("""
            {"profiles":[{"id":"x","name":"X","roles":[
              {"kind":"group","tenant":"t","group":"g"},
              {"kind":"entraDirectory","tenant":"t"}]}]}
            """))
            .Should().Be("profile 'x' role 2: role is required");

        Invalid(() => ManagedProfileSet.Parse(
            """{"profiles":[{"id":"x","name":"X","roles":[{"kind":"group","tenant":"t","group":"g","duration":"PT"}]}]}"""))
            .Should().Be("profile 'x' role 1: 'PT' is not a valid duration");

        Invalid(() => ManagedProfileSet.Parse(
            """{"profiles":[{"id":"x","name":"X","roles":[{"kind":"group","tenant":"t","group":"g","access":"admin"}]}]}"""))
            .Should().Be("profile 'x' role 1: unknown access 'admin'");
    }

    [Fact]
    public void RejectsMalformedJson()
        => Invalid(() => ManagedProfileSet.Parse("[]")).Should().Be("not a JSON object");

    [Fact]
    public void ProfileIdIsAStableUuidV5()
    {
        // uuid.uuid5(uuid.NAMESPACE_DNS, "managed-profile:prod-incident"); the Swift core agrees.
        ManagedProfileSet.ProfileId("prod-incident").Should().Be(Guid.Parse("cd5ed157-66f4-5485-ad04-ae05ef70f344"));

        var bytes = ManagedProfileSet.ProfileId("other").ToByteArray(bigEndian: true);
        (bytes[6] & 0xF0).Should().Be(0x50);
        (bytes[8] & 0xC0).Should().Be(0x80);
        ManagedProfileSet.ProfileId("other").Should().NotBe(ManagedProfileSet.ProfileId("prod-incident"));
    }

    [Fact]
    public void MergedReplacesByIdAndAppendsNewOnes()
    {
        var baseSet = ManagedProfileSet.Parse(
            """{"profiles":[{"id":"a","name":"A","roles":[]},{"id":"b","name":"B","roles":[]}]}""");
        var other = ManagedProfileSet.Parse(
            """{"profiles":[{"id":"b","name":"B2","roles":[]},{"id":"c","name":"C","roles":[]}]}""");

        var merged = baseSet.Merged(other);

        merged.Profiles.Select(p => p.Id).Should().Equal("a", "b", "c");
        merged.Profiles.Select(p => p.Name).Should().Equal("A", "B2", "C");
        ManagedProfileSet.Empty.Merged(other).Profiles.Select(p => p.Id).Should().Equal("b", "c");
        baseSet.Merged(ManagedProfileSet.Empty).Should().Be(baseSet);
    }
}
