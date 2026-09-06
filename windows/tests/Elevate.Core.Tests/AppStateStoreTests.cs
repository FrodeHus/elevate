using System.Text;
using System.Text.Json;
using Elevate.Core.Catalogue;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class AppStateStoreTests
{
    private static readonly RoleKey Key = new("i", "t", new EntraDirectoryScope("r", "/"));

    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "elevate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void LoadReturnsEmptyStateWhenNoFile()
    {
        var store = new AppStateStore(TempDir());

        store.Load().Should().Be(new AppState());
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var store = new AppStateStore(TempDir());
        var state = new AppState { Identities = [new Identity("i", "u@x", "U", "t")] };
        state.UpsertTenant(new TenantContext("i", "t", "Home", TenantSource.Home));
        state.Remember(Key, "Ops work", TimeSpan.FromMinutes(30));
        state.ManualRoles = [new ManualRole(new TenantKey("i", "t"), Key.Scope, "R")];

        store.Save(state);
        var back = store.Load();

        back.Should().Be(state);
        back.MemoryFor(Key)!.Justification.Should().Be("Ops work");
        back.MemoryFor(Key)!.LastDuration.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void SaveWritesAtomicallyLeavingNoTempFile()
    {
        var dir = TempDir();
        var store = new AppStateStore(dir);

        store.Save(new AppState { Identities = [new Identity("i", "u@x", "U", "t")] });

        File.Exists(Path.Combine(dir, "state.json")).Should().BeTrue();
        Directory.GetFiles(dir).Should().ContainSingle();
    }

    [Fact]
    public void OlderGenerationDoesNotOverwriteNewerState()
    {
        var store = new AppStateStore(TempDir());
        var newer = new AppState { Identities = [new Identity("new", "new@x", "New", "t")] };
        var older = new AppState { Identities = [new Identity("old", "old@x", "Old", "t")] };

        store.Save(newer, 2);
        store.Save(older, 1);

        store.Load().Identities.Select(i => i.Id).Should().Equal("new");
    }

    [Fact]
    public void QuarantineMovesUnreadableFileAside()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "state.json");
        File.WriteAllText(file, "not json");
        var store = new AppStateStore(dir);

        store.Invoking(s => s.Load()).Should().Throw<JsonException>();

        var backup = store.QuarantineCorruptFile();

        Path.GetFileName(backup).Should().Be("state.json.bak");
        File.Exists(file).Should().BeFalse();
        store.Load().Should().Be(new AppState());
    }

    [Fact]
    public void QuarantineReturnsNullWhenThereIsNoFile()
    {
        new AppStateStore(TempDir()).QuarantineCorruptFile().Should().BeNull();
    }

    [Fact]
    public void RememberOverwritesPerKey()
    {
        var state = new AppState();

        state.Remember(Key, "a", null);
        state.Remember(Key, "b", TimeSpan.FromMinutes(1));

        state.Memory.Should().ContainSingle();
        state.MemoryFor(Key)!.Justification.Should().Be("b");
    }

    [Fact]
    public void RemovingIdentityRemovesTenantsRolesAndMemory()
    {
        var state = new AppState { Identities = [new Identity("i", "u@x", "U", "t")] };
        state.UpsertTenant(new TenantContext("i", "t", "Home", TenantSource.Home));
        state.Remember(Key, "a", null);
        state.ManualRoles = [new ManualRole(Key.TenantKey, Key.Scope, "R")];

        state.RemoveIdentity("i");

        state.Should().Be(new AppState());
    }

    [Fact]
    public void MissingArraysDecodeAsEmpty()
    {
        Json.Deserialize<AppState>("{}").Should().Be(new AppState());
        Json.Deserialize<AppState>("""{"identities":null,"tenants":null,"manualRoles":null,"memory":null,"profiles":null}""")
            .Should().Be(new AppState());
    }

    [Fact]
    public void MissingRequiredFieldThrowsJsonException()
    {
        // Swift's Identity requires upn, displayName and homeTenantId; strict decoding must fail
        // rather than bind nulls into non-nullable strings.
        var decode = () => Json.Deserialize<AppState>("""{"identities":[{"id":"x"}]}""");

        decode.Should().Throw<JsonException>();
    }

    [Fact]
    public void OptionalSwiftFieldsStillDecodeWhenAbsent()
    {
        const string json = """
        {
          "identities": [{"id": "i", "upn": "u@x", "displayName": "U", "homeTenantId": "t"}],
          "tenants": [{"identityId": "i", "tenantId": "t", "displayName": "Home", "source": "home"}],
          "memory": [{"roleKey": {"identityId": "i", "tenantId": "t",
                                  "scope": {"entraDirectory": {"roleDefinitionId": "r", "directoryScopeId": "/"}}},
                      "justification": "j"}],
          "profiles": [{"id": "8E6A1B2C-3D4E-4F50-8112-A3B4C5D6E7F8", "name": "P", "entries": []}]
        }
        """;

        var state = Json.Deserialize<AppState>(json)!;

        state.Identities[0].SignInMethod.Should().Be(SignInMethod.OwnApp);
        state.Tenants[0].DiscoveryMode.Should().Be(DiscoveryMode.Automatic);
        state.Tenants[0].PrincipalObjectId.Should().BeNull();
        state.Tenants[0].EntraActivation.Should().BeNull();
        state.Memory[0].LastDuration.Should().BeNull();
        state.Profiles[0].LastJustification.Should().BeNull();
        state.ManualRoles.Should().BeEmpty();
    }

    [Fact]
    public void LoadThrowsJsonExceptionForAFileMissingRequiredFields()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "state.json"), """{"identities":[{"id":"x"}]}""");
        var store = new AppStateStore(dir);

        store.Invoking(s => s.Load()).Should().Throw<JsonException>();
        store.QuarantineCorruptFile().Should().NotBeNull();
        store.Load().Should().Be(new AppState());
    }

    [Fact]
    public void CloneIsIndependentOfTheOriginal()
    {
        var state = new AppState();
        state.Identities.Add(new Identity("i", "u@x", "U", "t"));
        state.UpsertTenant(new TenantContext("i", "t", "Home", TenantSource.Home));
        var roleKey = new RoleKey("i", "t", new EntraDirectoryScope("r", "/"));
        state.Remember(roleKey, "j", TimeSpan.FromHours(1));
        state.UpsertProfile(new ActivationProfile("P", [new ActivationProfile.Entry(roleKey)]));

        var clone = state.Clone();
        clone.Should().Be(state);

        clone.Identities.Add(new Identity("i2", "u2@x", "U2", "t"));
        clone.Tenants.Clear();
        clone.Memory.Clear();
        clone.Profiles[0].Entries.Clear();

        state.Identities.Should().ContainSingle();
        state.Tenants.Should().ContainSingle();
        state.Memory.Should().ContainSingle();
        state.Profiles[0].Entries.Should().ContainSingle();
        clone.Profiles[0].Entries.Should().BeEmpty();
    }

    [Fact]
    public void SavedFileIsPrettyPrintedWithSortedKeys()
    {
        var dir = TempDir();
        var store = new AppStateStore(dir);
        var state = new AppState();
        state.UpsertTenant(new TenantContext("i", "t", "Home", TenantSource.Home));

        store.Save(state);

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "state.json")));
        text.Should().Contain("\n  \"identities\": [],");
        var tenant = text[text.IndexOf("\"tenants\"", StringComparison.Ordinal)..];
        tenant.IndexOf("\"discoveryMode\"", StringComparison.Ordinal)
            .Should().BeLessThan(tenant.IndexOf("\"displayName\"", StringComparison.Ordinal));
    }
}
