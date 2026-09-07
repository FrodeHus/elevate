using System.Globalization;

namespace Elevate.Core.Support;

/// <summary>Countdown helpers: remaining time and its display labels.</summary>
public static class Countdown
{
    /// <summary>Whole seconds until <paramref name="end"/>, or <c>null</c> once it has passed.</summary>
    public static TimeSpan? Remaining(DateTimeOffset end, DateTimeOffset? now = null)
    {
        var reference = now ?? DateTimeOffset.UtcNow;
        var seconds = (end - reference).TotalSeconds;
        if (!(seconds > 0))
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Floor(seconds));
    }

    /// <summary>
    /// A coarse "time until" label: "2 h 15 min", "15 min", or "now" under a minute (or once past).
    /// </summary>
    public static string Until(DateTimeOffset date, DateTimeOffset? now = null)
    {
        var reference = now ?? DateTimeOffset.UtcNow;
        var minutes = (long)((date - reference).TotalSeconds / 60);
        return minutes < 1 ? "now" : Units(minutes);
    }

    /// <summary>
    /// Hours and minutes in units ("2 h 41 min", "46 min", "1 h"), floored to the minute; under a
    /// minute is "&lt; 1 min". Never <c>HH:MM</c>, which reads as a time of day next to a clock.
    /// </summary>
    public static string Label(TimeSpan d)
    {
        var minutes = (long)d.TotalSeconds / 60;
        return minutes < 1 ? "< 1 min" : Units(minutes);
    }

    private static string Units(long minutes)
    {
        var hours = minutes / 60;
        var mins = minutes % 60;
        if (hours == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{mins} min");
        }

        if (mins == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{hours} h");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{hours} h {mins} min");
    }
}
