using Elevate.Audit.Tests.Support;
using Elevate.Audit.Update;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class ReleaseCheckerTests
{
    [Fact]
    public async Task Latest_PicksTheNewestReleaseWithAnAuditAsset()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/releases", """
            [
              {"tag_name":"v2.0.0","html_url":"https://github.com/FrodeHus/elevate/releases/tag/v2.0.0","draft":false,"prerelease":false,"assets":[{"name":"elevate-cli-2.0.0-linux-x64.tar.gz"}]},
              {"tag_name":"v1.9.0","html_url":"https://github.com/FrodeHus/elevate/releases/tag/v1.9.0","draft":false,"prerelease":false,"assets":[{"name":"elevate-audit-1.9.0-linux-x64.tar.gz"}]}
            ]
            """);

        var latest = await new ReleaseChecker(stub).LatestAsync(CancellationToken.None);

        latest!.Version.Should().Be("1.9.0");
        stub.Requests[0].Headers["User-Agent"].Should().Be("elevate-audit");
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.2", true)]
    [InlineData("v1.2.3", "1.2.3", false)]
    [InlineData("v1.10.0", "1.9.9", true)]
    [InlineData("v1.2.3", "0.0.0", true)]
    [InlineData("v1.2.3", "1.2.3+abc", false)]
    public void IsNewer_ComparesNumerically(string tag, string current, bool expected) =>
        ReleaseChecker.IsNewer(tag, current).Should().Be(expected);
}
