using System.Text;
using Elevate.Audit.Networking;
using Elevate.Audit.Tests.Support;
using Elevate.Core.Networking;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class RetryingHttpClientTests
{
    private static HttpRequestData Get(string url) => new("GET", new Uri(url));

    [Fact]
    public async Task Retries429_HonouringRetryAfter()
    {
        var stub = new StubHttpClient();
        var calls = 0;
        stub.On("GET", "/users", _ => ++calls == 1
            ? new HttpResponseData(429, new Dictionary<string, string> { ["Retry-After"] = "3" }, Encoding.UTF8.GetBytes("slow down"))
            : new HttpResponseData(200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")));
        var waits = new List<TimeSpan>();
        var client = new RetryingHttpClient(stub, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

        var response = await client.SendAsync(Get("https://graph.microsoft.com/v1.0/users"), CancellationToken.None);

        response.Status.Should().Be(200);
        stub.Requests.Should().HaveCount(2);
        waits.Should().Equal(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task GivesUpAfterMaxAttempts_AndReturnsTheLastResponse()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/users", _ => new HttpResponseData(503, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("busy")));
        var client = new RetryingHttpClient(stub, (_, _) => Task.CompletedTask, maxAttempts: 3);

        var response = await client.SendAsync(Get("https://graph.microsoft.com/v1.0/users"), CancellationToken.None);

        response.Status.Should().Be(503);
        stub.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task DoesNotRetryOtherStatuses_AndCapsTheWait()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/forbidden", "nope", 403);
        stub.On("GET", "/slow", _ => new HttpResponseData(429, new Dictionary<string, string> { ["Retry-After"] = "600" }, []));
        var waits = new List<TimeSpan>();
        var client = new RetryingHttpClient(stub, (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, maxAttempts: 2);

        (await client.SendAsync(Get("https://graph.microsoft.com/v1.0/forbidden"), CancellationToken.None)).Status.Should().Be(403);
        await client.SendAsync(Get("https://graph.microsoft.com/v1.0/slow"), CancellationToken.None);

        stub.RequestsMatching("/forbidden").Should().HaveCount(1);
        waits.Should().Equal(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task MissingRetryAfter_BacksOffExponentially()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/users", _ => new HttpResponseData(429, new Dictionary<string, string>(), []));
        var waits = new List<TimeSpan>();
        var client = new RetryingHttpClient(stub, (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, maxAttempts: 4);

        await client.SendAsync(Get("https://graph.microsoft.com/v1.0/users"), CancellationToken.None);

        waits.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8));
    }
}
