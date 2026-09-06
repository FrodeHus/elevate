using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>
/// A <c>state.json</c> written by the macOS app must survive a decode/encode round trip here without
/// losing or changing a single value, so the two apps can share one file.
/// </summary>
public class AppStateGoldenTests
{
    private static string Canonical(string json) =>
        Sorted(JsonNode.Parse(json))!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static JsonNode? Sorted(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
            {
                var result = new JsonObject();
                foreach (var (key, value) in o.ToList().OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    o.Remove(key);
                    result[key] = Sorted(value);
                }

                return result;
            }

            case JsonArray a:
            {
                var result = new JsonArray();
                foreach (var item in a.ToList())
                {
                    a.Remove(item);
                    result.Add(Sorted(item));
                }

                return result;
            }

            default:
                return node;
        }
    }

    [Fact]
    public void MacOsStateFileRoundTripsLosslessly()
    {
        var original = Fixtures.Text("state-macos");

        var state = Json.Deserialize<AppState>(original)!;
        var written = Encoding.UTF8.GetString(AppStateStore.Encode(state));

        Canonical(written).Should().Be(Canonical(original));
    }

    [Fact]
    public void MacOsStateFileDecodesToTheExpectedValues()
    {
        var state = Json.Deserialize<AppState>(Fixtures.Text("state-macos"))!;

        state.Identities.Should().ContainSingle();
        state.Identities[0].SignInMethod.Should().Be(SignInMethod.AzureCLI);
        state.Tenants.Select(t => t.TenantId).Should().Equal("t-home", "t-cust");
        state.Tenants[0].EntraActivation!.IsSupported.Should().BeFalse();
        state.Tenants[1].DiscoveryMode.Should().Be(DiscoveryMode.ManualRoles);

        state.ManualRoles.Select(r => r.Scope).Should().Equal(
            new AzureResourceScope("/subscriptions/sub-1", "Contributor"),
            new GroupScope("g-1", GroupAccess.Owner));

        var entra = new RoleKey("id1", "t-home", new EntraDirectoryScope("62e90394-69f5-4237-9190-012177145e10", "/"));
        state.MemoryFor(entra)!.LastDuration.Should().Be(TimeSpan.FromMinutes(30));
        state.MemoryFor(entra)!.Justification.Should().Be("INC-1234 · escalation");
        state.Memory[1].LastDuration.Should().BeNull();

        var profile = state.Profile(Guid.Parse("A1B2C3D4-E5F6-4A5B-8C7D-9E0FABCDEF12"))!;
        profile.Name.Should().Be("Incident response");
        profile.LastJustification.Should().Be("INC-1234");
        profile.Entries.Should().HaveCount(2);
        profile.Entries[0].LastDuration.Should().Be(TimeSpan.FromHours(1));
        profile.Entries[1].LastDuration.Should().BeNull();
    }

    [Fact]
    public void SavedStateReloadsThroughTheStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elevate-" + Guid.NewGuid().ToString("N"));
        var store = new AppStateStore(dir);
        var state = Json.Deserialize<AppState>(Fixtures.Text("state-macos"))!;

        store.Save(state);

        store.Load().Should().Be(state);
    }
}
