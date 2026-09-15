using Elevate.Audit.Tests.Support;
using Elevate.Audit.Update;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class ReleaseCheckerTests
{
    // The audit tool has its own release line under audit-v tags. An app release (a v tag) is
    // skipped even when it carries an elevate-audit archive, as the releases up to 1.6.7 did:
    // otherwise a checker at audit 1.0.0 would offer the old app release 1.6.7 as an upgrade.
    [Fact]
    public async Task Latest_PicksTheNewestAuditReleaseWithAnAuditAsset()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/releases", """
            [
              {"tag_name":"v2.0.0","html_url":"https://github.com/FrodeHus/elevate/releases/tag/v2.0.0","draft":false,"prerelease":false,"assets":[{"name":"elevate-audit-2.0.0-linux-x64.tar.gz"}]},
              {"tag_name":"audit-v1.9.1","html_url":"https://github.com/FrodeHus/elevate/releases/tag/audit-v1.9.1","draft":true,"prerelease":false,"assets":[{"name":"elevate-audit-1.9.1-linux-x64.tar.gz"}]},
              {"tag_name":"audit-v1.9.0","html_url":"https://github.com/FrodeHus/elevate/releases/tag/audit-v1.9.0","draft":false,"prerelease":false,"assets":[{"name":"elevate-audit-1.9.0-linux-x64.tar.gz"}]}
            ]
            """);

        var latest = await new ReleaseChecker(stub).LatestAsync(CancellationToken.None);

        latest!.Tag.Should().Be("audit-v1.9.0");
        latest.Version.Should().Be("1.9.0");
        stub.Requests[0].Headers["User-Agent"].Should().Be("elevate-audit");
    }

    [Fact]
    public async Task Latest_IsNullWhenOnlyAppReleasesExist()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/releases", """
            [
              {"tag_name":"v1.6.7","html_url":"https://github.com/FrodeHus/elevate/releases/tag/v1.6.7","draft":false,"prerelease":false,"assets":[{"name":"elevate-audit-1.6.7-linux-x64.tar.gz"}]}
            ]
            """);

        var latest = await new ReleaseChecker(stub).LatestAsync(CancellationToken.None);

        latest.Should().BeNull();
    }

    [Theory]
    [InlineData("audit-v1.2.3", "1.2.2", true)]
    [InlineData("audit-v1.2.3", "1.2.3", false)]
    [InlineData("audit-v1.10.0", "1.9.9", true)]
    [InlineData("audit-v1.2.3", "0.0.0", true)]
    [InlineData("audit-v1.2.3", "1.2.3+abc", false)]
    [InlineData("v1.6.7", "1.0.0", true)]
    public void IsNewer_ComparesNumerically(string tag, string current, bool expected) =>
        ReleaseChecker.IsNewer(tag, current).Should().Be(expected);
}
