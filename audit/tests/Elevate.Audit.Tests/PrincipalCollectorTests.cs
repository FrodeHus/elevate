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
    [Fact]
    public async Task EnrichUsers_ReadsUserTypeInChunksOfFifteen_WithAnIdInFilter()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/users?$select=", request =>
        {
            var url = Uri.UnescapeDataString(request.Url.AbsoluteUri);
            var ids = url[(url.IndexOf("id in (", StringComparison.Ordinal) + 7)..].TrimEnd(')').Split(',').Select(i => i.Trim('\'')).ToList();
            var value = string.Join(",", ids.Select(id =>
                $$"""{"id":"{{id}}","displayName":"Priya Natarajan","userPrincipalName":"priya_fabrikam.com#EXT#@contoso.com","userType":"Guest","accountEnabled":true}"""));
            return new Elevate.Core.Networking.HttpResponseData(200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes($$"""{"value":[{{value}}]}"""));
        });
        var ids = Enumerable.Range(0, 16).Select(i => $"u{i}").ToList();

        var enriched = await new PrincipalCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).EnrichUsersAsync(ids, CancellationToken.None);

        var requests = stub.RequestsMatching("/users?$select=");
        requests.Should().HaveCount(2, "ids are chunked at fifteen so the $filter stays inside Graph's URL limits");
        var first = Uri.UnescapeDataString(requests[0].Url.AbsoluteUri);
        first.Should().Contain("$select=id,displayName,userPrincipalName,userType,accountEnabled");
        first.Should().Contain("$filter=id in ('u0','u1',");
        enriched.Should().HaveCount(16);
        enriched.Should().OnlyContain(p => p.Type == PrincipalType.User && p.IsGuest && p.AccountEnabled == true);
    }

    [Fact]
    public async Task EnrichUsers_WithNoIds_MakesNoRequest()
    {
        var stub = new StubHttpClient();

        var enriched = await new PrincipalCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).EnrichUsersAsync([], CancellationToken.None);

        enriched.Should().BeEmpty();
        stub.Requests.Should().BeEmpty();
    }
}
