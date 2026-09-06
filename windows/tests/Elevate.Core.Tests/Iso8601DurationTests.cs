using System.Text.Json;
using Elevate.Core.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class Iso8601DurationTests
{
    [Fact]
    public void ParsesHoursMinutes()
    {
        Iso8601Duration.Parse("PT8H").Should().Be(TimeSpan.FromSeconds(8 * 3600));
        Iso8601Duration.Parse("PT30M").Should().Be(TimeSpan.FromSeconds(1800));
        Iso8601Duration.Parse("PT1H30M").Should().Be(TimeSpan.FromSeconds(5400));
        Iso8601Duration.Parse("P1D").Should().Be(TimeSpan.FromSeconds(86400));
        Iso8601Duration.Parse("garbage").Should().BeNull();
    }

    [Fact]
    public void FormatsAsHoursAndMinutes()
    {
        Iso8601Duration.Format(TimeSpan.FromSeconds(8 * 3600)).Should().Be("PT8H");
        Iso8601Duration.Format(TimeSpan.FromSeconds(1800)).Should().Be("PT30M");
        Iso8601Duration.Format(TimeSpan.FromSeconds(5400)).Should().Be("PT1H30M");
    }

    [Fact]
    public void DecoderAcceptsFractionalAndPlainDates()
    {
        var plain = JsonSerializer.Deserialize<Box>("""{"d":"2026-09-04T08:00:00Z"}""", GraphJson.Options);
        var frac = JsonSerializer.Deserialize<Box>("""{"d":"2026-09-04T08:00:00.1234567Z"}""", GraphJson.Options);

        Math.Abs((plain!.D - frac!.D).TotalSeconds).Should().BeLessThan(1);
    }

    [Fact]
    public void ParseDateIsTolerantOfFractionalDigitsAndOffsets()
    {
        GraphJson.ParseDate("2026-09-04T08:00:00Z").Should().NotBeNull();
        GraphJson.ParseDate("2026-09-04T08:00:00.1Z").Should().NotBeNull();
        GraphJson.ParseDate("2026-09-04T08:00:00.1234567Z").Should().NotBeNull();
        GraphJson.ParseDate("2026-09-04T08:00:00.1234567+02:00").Should().NotBeNull();
        GraphJson.ParseDate("not a date").Should().BeNull();
    }

    private sealed record Box(DateTimeOffset D);
}
