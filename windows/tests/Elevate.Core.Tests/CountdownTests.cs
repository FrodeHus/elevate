using Elevate.Core.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class CountdownTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    [Fact]
    public void RemainingIsNilWhenExpired()
    {
        Countdown.Remaining(Now.AddSeconds(-1), Now).Should().BeNull();
    }

    [Fact]
    public void LabelFormatsHoursMinutes()
    {
        Countdown.Label(TimeSpan.FromSeconds(2 * 3600 + 41 * 60 + 10)).Should().Be("02:41");
        Countdown.Label(TimeSpan.FromSeconds(59)).Should().Be("00:00");
        Countdown.Label(TimeSpan.FromSeconds(5 * 60)).Should().Be("00:05");
    }

    [Fact]
    public void RemainingRoundsDownToSeconds()
    {
        var r = Countdown.Remaining(Now.AddSeconds(125.9), Now);
        r.Should().Be(TimeSpan.FromSeconds(125));
    }

    [Fact]
    public void UntilLabels()
    {
        Countdown.Until(Now.AddSeconds(2 * 3600 + 15 * 60), Now).Should().Be("2 h 15 m");
        Countdown.Until(Now.AddSeconds(15 * 60 + 59), Now).Should().Be("15 m");
        Countdown.Until(Now.AddSeconds(3600), Now).Should().Be("1 h");
        Countdown.Until(Now.AddSeconds(30), Now).Should().Be("now");
        Countdown.Until(Now.AddSeconds(-5), Now).Should().Be("now");
    }
}
