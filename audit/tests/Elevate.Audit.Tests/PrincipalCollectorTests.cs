using System.Text;
using System.Text.Json;
using Elevate.Audit.Collectors;
using Elevate.Audit.Model;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class PrincipalCollectorTests
{
    [Fact]
    public async Task Resolve_PostsInChunksOfAThousand_AndMapsTypes()
    {
        var stub = new StubHttpClient();
        stub.On("POST", "/directoryObjects/getByIds", request =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            var ids = body.RootElement.GetProperty("ids").EnumerateArray().Select(e => e.GetString()!).ToList();
            var value = string.Join(",", ids.Take(2).Select((id, i) => i == 0
                ? $$"""{"@odata.type":"#microsoft.graph.user","id":"{{id}}","displayName":"Jordan Lee","userPrincipalName":"jordan.lee@contoso.com","userType":"Member"}"""
                : $$"""{"@odata.type":"#microsoft.graph.servicePrincipal","id":"{{id}}","displayName":"Backup Job","servicePrincipalType":"ManagedIdentity"}"""));
            return new Elevate.Core.Networking.HttpResponseData(200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes($$"""{"value":[{{value}}]}"""));
        });
        var ids = Enumerable.Range(0, 1001).Select(i => $"p{i}").ToList();

        var resolved = await new PrincipalCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).ResolveAsync(ids, CancellationToken.None);

        stub.RequestsMatching("getByIds").Should().HaveCount(2);
        using var first = JsonDocument.Parse(stub.Requests[0].Body!);
        first.RootElement.GetProperty("ids").GetArrayLength().Should().Be(1000);
        first.RootElement.GetProperty("types").EnumerateArray().Select(e => e.GetString()).Should().Equal("user", "group", "servicePrincipal", "device");
        resolved.Should().HaveCount(3);
        resolved.Single(p => p.Id == "p0").Type.Should().Be(PrincipalType.User);
        resolved.Single(p => p.Id == "p1").Should().BeEquivalentTo(new { Type = PrincipalType.ServicePrincipal, ServicePrincipalType = "ManagedIdentity" });
    }

    [Fact]
    public async Task Resolve_WithNoIds_MakesNoRequest()
    {
        var stub = new StubHttpClient();

        var resolved = await new PrincipalCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).ResolveAsync([], CancellationToken.None);

        resolved.Should().BeEmpty();
        stub.Requests.Should().BeEmpty();
    }
}
