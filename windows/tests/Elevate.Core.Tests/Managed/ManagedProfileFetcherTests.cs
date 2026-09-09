using System.Text;
using Elevate.Core.Managed;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests.Managed;

public class ManagedProfileFetcherTests
{
    private static readonly Uri Url = new("https://contoso.example/profiles.json");

    private static string TemporaryCachePath()
        => Path.Combine(Path.GetTempPath(), "elevate-tests-" + Guid.NewGuid().ToString("N"), "managed-profiles.json");

    [Fact]
    public async Task FetchParsesAndCaches()
    {
        var cache = TemporaryCachePath();
        var http = new StubHttpClient();
        http.On("GET", "profiles.json", ManagedProfileSetTests.Example);
        var fetcher = new ManagedProfileFetcher(http, cache);

        var set = await fetcher.FetchAsync(Url);

        set.Profiles.Select(p => p.Id).Should().Equal("prod-incident");
        File.Exists(cache).Should().BeTrue();
        fetcher.Cached().Should().Be(set);

        var request = http.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be("GET");
        request.Headers["Accept"].Should().Be("application/json");
    }

    [Fact]
    public void CachedIsNullWithoutAFileOrWithAnUnreadableOne()
    {
        var cache = TemporaryCachePath();
        var fetcher = new ManagedProfileFetcher(new StubHttpClient(), cache);

        fetcher.Cached().Should().BeNull();

        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "not json");
        fetcher.Cached().Should().BeNull();
    }

    [Fact]
    public async Task FetchThrowsOnAnErrorStatusAndLeavesTheCacheAlone()
    {
        var cache = TemporaryCachePath();
        var http = new StubHttpClient();
        http.On("GET", "profiles.json", "boom", status: 500);
        var fetcher = new ManagedProfileFetcher(http, cache);

        var act = () => fetcher.FetchAsync(Url);

        (await act.Should().ThrowAsync<ManagedProfileException>()).Which.Message
            .Should().Be("managed profiles fetch failed with HTTP 500");
        File.Exists(cache).Should().BeFalse();
    }

    [Fact]
    public async Task FetchThrowsOnAnOversizedBody()
    {
        var cache = TemporaryCachePath();
        var http = new StubHttpClient();
        http.On("GET", "profiles.json", body: Encoding.UTF8.GetBytes(new string(' ', ManagedProfileFetcher.MaxBytes + 1)));
        var fetcher = new ManagedProfileFetcher(http, cache);

        var act = () => fetcher.FetchAsync(Url);

        (await act.Should().ThrowAsync<ManagedProfileException>()).Which.Message
            .Should().Be("managed profiles document is larger than 1 MB");
        File.Exists(cache).Should().BeFalse();
    }

    [Fact]
    public async Task FetchRefusesANonHttpsUrl()
    {
        var fetcher = new ManagedProfileFetcher(new StubHttpClient(), TemporaryCachePath());

        var act = () => fetcher.FetchAsync(new Uri("http://contoso.example/profiles.json"));

        (await act.Should().ThrowAsync<ManagedProfileException>()).Which.Message
            .Should().Be("managed profiles URL must be https");
    }

    [Fact]
    public async Task FetchThrowsOnAnUnparsableBody()
    {
        var cache = TemporaryCachePath();
        var http = new StubHttpClient();
        http.On("GET", "profiles.json", """{"version":2}""");
        var fetcher = new ManagedProfileFetcher(http, cache);

        var act = () => fetcher.FetchAsync(Url);

        (await act.Should().ThrowAsync<ManagedProfileException>()).Which.Message.Should().Be("version 2 is not supported");
        File.Exists(cache).Should().BeFalse();
    }
}
