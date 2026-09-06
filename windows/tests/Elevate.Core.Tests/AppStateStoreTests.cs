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
