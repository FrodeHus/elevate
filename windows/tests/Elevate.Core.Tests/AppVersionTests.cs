using Elevate.Core.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>AppVersionTests</c>.</summary>
public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3+45", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    public void Parses(string text, int major, int minor, int patch) =>
        AppVersion.Parse(text).Should().Be(new AppVersion(major, minor, patch));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.x")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    public void RejectsGarbage(string text) => AppVersion.Parse(text).Should().BeNull();

    [Fact]
    public void Comparable()
    {
        (AppVersion.Parse("1.2.3")!.Value < AppVersion.Parse("1.2.4")!.Value).Should().BeTrue();
        (AppVersion.Parse("1.2.3")!.Value < AppVersion.Parse("1.3.0")!.Value).Should().BeTrue();
        (AppVersion.Parse("1.2.3")!.Value < AppVersion.Parse("2.0.0")!.Value).Should().BeTrue();
        AppVersion.Parse("1.2.3").Should().Be(AppVersion.Parse("1.2.3"));
        (AppVersion.Parse("1.2.3")!.Value < AppVersion.Parse("1.2.3")!.Value).Should().BeFalse();
    }

    [Fact]
    public void IsNewerTrueWhenLatestGreater()
    {
        AppVersion.IsNewer("v1.1.0", "1.0.0").Should().BeTrue();
        AppVersion.IsNewer("1.0.1", "1.0.0").Should().BeTrue();
    }

    [Fact]
    public void IsNewerFalseWhenEqual() => AppVersion.IsNewer("v1.0.0", "1.0.0").Should().BeFalse();

    [Fact]
    public void IsNewerFalseWhenOlder() => AppVersion.IsNewer("v0.9.0", "1.0.0").Should().BeFalse();

    [Fact]
    public void IsNewerFalseWhenEitherFailsToParse()
    {
        AppVersion.IsNewer("garbage", "1.0.0").Should().BeFalse();
        AppVersion.IsNewer("v1.0.0", "garbage").Should().BeFalse();
        AppVersion.IsNewer("garbage", "also-garbage").Should().BeFalse();
    }

    [Fact]
    public void WindowsTagPrefixIsNotAVersion() => AppVersion.Parse("windows-v1.2.3").Should().BeNull();
}
