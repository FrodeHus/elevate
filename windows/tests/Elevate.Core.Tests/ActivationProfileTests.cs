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
    public void PinnedDecodesTolerantlyAndIsWrittenOnlyWhenTrue()
    {
        const string legacy = """{"id":"A1B2C3D4-E5F6-4A5B-8C7D-9E0FABCDEF12","name":"Ops","entries":[]}""";

        var decoded = Json.Deserialize<ActivationProfile>(legacy)!;

        decoded.Pinned.Should().BeFalse();
        Json.Serialize(decoded).Should().NotContain("pinned");

        decoded.Pinned = true;
        var json = Json.Serialize(decoded);

        json.Should().Contain("\"pinned\":true");
        Json.Deserialize<ActivationProfile>(json)!.Pinned.Should().BeTrue();
        decoded.DeepCopy().Pinned.Should().BeTrue();
        decoded.Should().NotBe(Json.Deserialize<ActivationProfile>(legacy));
    }

    [Fact]
    public void PinningIsCappedAtTheLimit()
    {
        var state = new AppState();
        var profiles = Enumerable.Range(0, ProfilePins.Limit + 1).Select(i => new ActivationProfile($"P{i}", [])).ToList();
        profiles.ForEach(state.UpsertProfile);

        foreach (var p in profiles.Take(ProfilePins.Limit))
        {
            state.SetPinned(p.Id, true).Should().BeTrue();
        }

        state.PinnedProfiles.Should().HaveCount(ProfilePins.Limit);
        state.SetPinned(profiles[ProfilePins.Limit].Id, true).Should().BeFalse();
        state.Profile(profiles[ProfilePins.Limit].Id)!.Pinned.Should().BeFalse();

        // Re-pinning an already pinned profile is not a new pin.
        state.SetPinned(profiles[0].Id, true).Should().BeTrue();
        state.SetPinned(profiles[0].Id, false).Should().BeTrue();
        state.PinnedProfiles.Select(p => p.Name).Should().Equal("P1", "P2", "P3");
        state.SetPinned(profiles[ProfilePins.Limit].Id, true).Should().BeTrue();
        state.SetPinned(Guid.NewGuid(), true).Should().BeFalse();
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

    [Fact]
    public void SourceDefaultsToUserAndIsWrittenOnlyWhenManaged()
    {
        var user = new ActivationProfile("Ops", []);
        user.Source.Should().Be(ProfileSource.User);
        Json.Serialize(user).Should().NotContain("source");

        var managed = user.DeepCopy() with { Source = ProfileSource.Managed };
        managed.Should().NotBe(user);   // source takes part in equality
        var json = Json.Serialize(managed);
        json.Should().Contain("\"source\":\"managed\"");
        Json.Deserialize<ActivationProfile>(json)!.Source.Should().Be(ProfileSource.Managed);

        // A profile written before `source` existed decodes as a user profile.
        const string Old = """{"id":"A1B2C3D4-E5F6-4A5B-8C7D-9E0FABCDEF12","name":"Old","entries":[]}""";
        Json.Deserialize<ActivationProfile>(Old)!.Source.Should().Be(ProfileSource.User);
    }
}
