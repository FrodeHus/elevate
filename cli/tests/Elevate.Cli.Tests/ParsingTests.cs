using Elevate.Cli.Infrastructure;
using Elevate.Cli.Selection;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Cli.Tests;

public class ParsingTests
{
    [Theory]
    [InlineData("2h", 120)]
    [InlineData("30m", 30)]
    [InlineData("1h30m", 90)]
    [InlineData("1h 30min", 90)]
    [InlineData("1.5h", 90)]
    [InlineData("90", 90)]
    [InlineData("2:30", 150)]
    [InlineData("PT2H", 120)]
    [InlineData("pt45m", 45)]
    [InlineData("1d", 1440)]
    public void DurationsParse(string text, int minutes) =>
        DurationParser.Parse(text).Should().Be(TimeSpan.FromMinutes(minutes));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("0m")]
    [InlineData("soon")]
    [InlineData("2x")]
    public void BadDurationsAreNull(string text) => DurationParser.Parse(text).Should().BeNull();

    [Fact]
    public void RequireDurationThrowsUsage()
    {
        var act = () => DurationParser.Require("nope");
        act.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.Usage);
    }

    [Fact]
    public void StartTimeRelative()
    {
        var now = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.FromHours(2));
        StartTimeParser.Parse("+2h", now).Should().Be(now.AddHours(2));
    }

    [Fact]
    public void StartTimeOfDayRollsToTomorrowWhenPassed()
    {
        var now = new DateTimeOffset(2026, 9, 7, 15, 0, 0, TimeSpan.FromHours(2));
        StartTimeParser.Parse("14:30", now).Should().Be(new DateTimeOffset(2026, 9, 8, 14, 30, 0, TimeSpan.FromHours(2)));
        StartTimeParser.Parse("16:00", now).Should().Be(new DateTimeOffset(2026, 9, 7, 16, 0, 0, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void StartTimeAbsolute()
    {
        StartTimeParser.Parse("2030-01-02T09:00:00Z").Should().Be(new DateTimeOffset(2030, 1, 2, 9, 0, 0, TimeSpan.Zero));
        StartTimeParser.Parse("yesterday").Should().BeNull();
    }

    [Fact]
    public void ShortIdsAreStableAndDistinct()
    {
        var a = new RoleKey("id1", "t1", new EntraDirectoryScope("r1", "/"));
        var b = new RoleKey("id1", "t1", new EntraDirectoryScope("r2", "/"));
        ShortId.For(a).Should().HaveLength(8).And.Be(ShortId.For(a with { }));
        ShortId.For(a).Should().NotBe(ShortId.For(b));
        ShortId.LooksLikeId(ShortId.For(a)).Should().BeTrue();
        ShortId.LooksLikeId("Global Reader").Should().BeFalse();
    }

    [Theory]
    [InlineData("entra", RoleScopeKind.EntraDirectory)]
    [InlineData("Azure", RoleScopeKind.AzureResource)]
    [InlineData("groups", RoleScopeKind.Group)]
    public void KindsParse(string text, RoleScopeKind kind) => RoleFilter.ParseKind(text).Should().Be(kind);

    [Fact]
    public void UnknownKindIsUsageError()
    {
        var act = () => RoleFilter.ParseKind("pim");
        act.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.Usage);
    }
}
