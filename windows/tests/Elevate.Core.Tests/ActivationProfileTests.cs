using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class ActivationProfileTests
{
    private static RoleKey Key(string name, RoleScopeKind kind = RoleScopeKind.EntraDirectory, string tenantId = "t") =>
        new("i", tenantId, kind switch
        {
            RoleScopeKind.EntraDirectory => new EntraDirectoryScope(name, "/"),
            RoleScopeKind.AzureResource => new AzureResourceScope("/subscriptions/s", name),
            _ => new GroupScope(name, GroupAccess.Member),
        });

    [Fact]
    public void StateWithoutProfilesDecodes()
    {
        const string json = """{"identities":[],"tenants":[],"manualRoles":[],"memory":[]}""";

        Json.Deserialize<AppState>(json)!.Profiles.Should().BeEmpty();

        var minimal = Json.Deserialize<AppState>("{}")!;
        minimal.Identities.Should().BeEmpty();
        minimal.Profiles.Should().BeEmpty();
    }

    [Fact]
    public void ProfilesRoundTripAndHelpers()
    {
        var state = new AppState();
        var profile = new ActivationProfile("Ops", [new ActivationProfile.Entry(Key("a"), TimeSpan.FromHours(1))], "INC");
        state.UpsertProfile(profile);
        state.UpsertProfile(new ActivationProfile("Second", []));

        var decoded = Json.Deserialize<AppState>(Json.Serialize(state))!;

        decoded.Profiles.Should().HaveCount(2);
        decoded.Profile(profile.Id)!.Entries[0].LastDuration.Should().Be(TimeSpan.FromHours(1));
        decoded.Profile(profile.Id)!.LastJustification.Should().Be("INC");

        state.UpsertProfile(profile with { Name = "Ops 2" });
        state.Profiles.Should().HaveCount(2);
        state.Profile(profile.Id)!.Name.Should().Be("Ops 2");

        state.MoveProfiles([1], 0);
        state.Profiles[0].Name.Should().Be("Second");

        state.RemoveProfile(profile.Id);
        state.Profiles.Should().ContainSingle();
    }

    [Fact]
    public void ProfileIdIsWrittenAsSwiftsUppercaseUuid()
    {
        var id = Guid.Parse("a1b2c3d4-e5f6-4a5b-8c7d-9e0fabcdef12");
        var profile = new ActivationProfile(id, "Ops", []);

        var json = Json.Serialize(profile);

        json.Should().Be("""{"id":"A1B2C3D4-E5F6-4A5B-8C7D-9E0FABCDEF12","name":"Ops","entries":[]}""");
        Json.Deserialize<ActivationProfile>(json)!.Id.Should().Be(id);
    }

    [Fact]
    public void RemovingTenantDropsProfileEntries()
    {
        var state = new AppState();
        var keep = Key("keep", tenantId: "t1");
        var drop = Key("drop", tenantId: "t2");
        var profile = new ActivationProfile("Mixed", [new ActivationProfile.Entry(keep), new ActivationProfile.Entry(drop)]);
        state.UpsertProfile(profile);

        state.RemoveTenant(new TenantKey("i", "t2"));

        state.Profile(profile.Id)!.Entries.Select(e => e.RoleKey).Should().Equal(keep);
    }

    [Fact]
    public void RemovingIdentityDropsProfileEntries()
    {
        var state = new AppState();
        var keep = new RoleKey("other", "t1", new EntraDirectoryScope("keep", "/"));
        var drop = Key("drop", tenantId: "t1");
        var profile = new ActivationProfile("Mixed", [new ActivationProfile.Entry(keep), new ActivationProfile.Entry(drop)]);
        state.UpsertProfile(profile);

        state.RemoveIdentity("i");

        state.Profile(profile.Id)!.Entries.Select(e => e.RoleKey).Should().Equal(keep);
    }

    [Fact]
    public void SummaryCaption()
    {
        ProfileSummary.Caption([new ActivationProfile.Entry(Key("a"))]).Should().Be("1 role");
        ProfileSummary.Caption([
            new ActivationProfile.Entry(Key("a")),
            new ActivationProfile.Entry(Key("b", RoleScopeKind.AzureResource)),
        ]).Should().Be("2 roles");
        ProfileSummary.Caption([new ActivationProfile.Entry(Key("g", RoleScopeKind.Group))]).Should().Be("1 group");
        ProfileSummary.Caption([
            new ActivationProfile.Entry(Key("a")),
            new ActivationProfile.Entry(Key("g", RoleScopeKind.Group)),
            new ActivationProfile.Entry(Key("h", RoleScopeKind.Group)),
        ]).Should().Be("1 role · 2 groups");
        ProfileSummary.Caption([]).Should().Be("empty");
    }
}
