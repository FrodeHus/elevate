using System.Reflection;
using System.Text.Json;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void EntraBuiltInRolesCatalogue_IsEmbeddedAndParsesToAtLeast130Entries()
    {
        var coreAssembly = Assembly.Load("Elevate.Core");

        using var stream = coreAssembly.GetManifestResourceStream("Elevate.Core.Resources.EntraBuiltInRoles.json");

        stream.Should().NotBeNull();

        using var document = JsonDocument.Parse(stream!);

        document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        document.RootElement.GetArrayLength().Should().BeGreaterThanOrEqualTo(130);
    }
}
