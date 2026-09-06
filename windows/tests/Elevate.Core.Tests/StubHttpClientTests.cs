using System.Text;
using System.Text.Json;
using Elevate.Core.Networking;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Self-test for the shared doubles, so the provider tests can trust them.</summary>
public class StubHttpClientTests
{
    private static HttpRequestData Get(string url) => new("GET", new Uri(url));

    [Fact]
    public async Task SendAsync_UsesTheLastMatchingRoute()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/me", "first");
        stub.On("GET", "/me", "second");

        var response = await stub.SendAsync(Get("https://graph.microsoft.com/v1.0/me"), CancellationToken.None);

        response.Status.Should().Be(200);
        response.BodyText.Should().Be("second");
    }

    [Fact]
    public async Task SendAsync_MatchesOnMethodAndUrlSubstring()
    {
        var stub = new StubHttpClient();
        stub.On("POST", "/me", "posted");

        var get = await stub.SendAsync(Get("https://graph.microsoft.com/v1.0/me"), CancellationToken.None);
        var other = await stub.SendAsync(new HttpRequestData("POST", new Uri("https://graph.microsoft.com/v1.0/users")), CancellationToken.None);
        var post = await stub.SendAsync(new HttpRequestData("POST", new Uri("https://graph.microsoft.com/v1.0/me")), CancellationToken.None);

        get.Status.Should().Be(599);
        get.BodyText.Should().Contain("no stub for GET");
        other.Status.Should().Be(599);
        post.BodyText.Should().Be("posted");
    }

    [Fact]
    public async Task SendAsync_RecordsEveryRequestAndFiltersBySubstring()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "graph.microsoft.com", "{}");

        await stub.SendAsync(Get("https://graph.microsoft.com/v1.0/me"), CancellationToken.None);
        await stub.SendAsync(Get("https://graph.microsoft.com/v1.0/users"), CancellationToken.None);
        await stub.SendAsync(Get("https://management.azure.com/tenants"), CancellationToken.None);

        stub.Requests.Should().HaveCount(3);
        stub.RequestsMatching("graph.microsoft.com").Should().HaveCount(2);
        stub.RequestsMatching("/tenants").Should().ContainSingle()
            .Which.Url.AbsoluteUri.Should().Be("https://management.azure.com/tenants");
    }

    [Fact]
    public async Task SendAsync_SupportsADelegateRouteAndHeaderLookup()
    {
        var stub = new StubHttpClient();
        stub.On("POST", "/activate", request => new HttpResponseData(
            401,
            new Dictionary<string, string> { ["WWW-Authenticate"] = $"Bearer method={request.Method}" },
            Encoding.UTF8.GetBytes("denied")));

        var response = await stub.SendAsync(
            new HttpRequestData("POST", new Uri("https://graph.microsoft.com/activate"), new Dictionary<string, string>(), [1, 2]),
            CancellationToken.None);

        response.Status.Should().Be(401);
        response.Header("www-authenticate").Should().Be("Bearer method=POST");
        response.Header("missing").Should().BeNull();
        response.BodyText.Should().Be("denied");
        stub.Requests.Should().ContainSingle().Which.Body.Should().Equal([1, 2]);
    }

    [Fact]
    public void Fixtures_LoadTheCopiedJson()
    {
        var me = Fixtures.Data("me.json");

        me.Should().NotBeEmpty();
        using var document = JsonDocument.Parse(me);
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        Fixtures.Text("me").Should().Contain("user-obj-1");
    }
}
