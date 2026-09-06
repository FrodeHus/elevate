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
    /// A coarse "time until" label: "2 h 15 m", "15 m", or "now" under a minute (or once past).
    /// </summary>
    public static string Until(DateTimeOffset date, DateTimeOffset? now = null)
    {
        var reference = now ?? DateTimeOffset.UtcNow;
        var minutes = (long)((date - reference).TotalSeconds / 60);
        if (minutes < 1)
        {
            return "now";
        }

        var hours = minutes / 60;
        var mins = minutes % 60;
        if (hours == 0)
        {
            return $"{mins} m";
        }

        if (mins == 0)
        {
            return $"{hours} h";
        }

        return $"{hours} h {mins} m";
    }

    /// <summary><c>HH:MM</c>, floored to the minute.</summary>
    public static string Label(TimeSpan d)
    {
        var total = (long)d.TotalSeconds;
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600:D2}:{(total % 3600) / 60:D2}");
    }
}
