using System.Globalization;
using System.Text.RegularExpressions;

namespace Elevate.Core.Support;

/// <summary>
/// Parses and formats the subset of ISO-8601 durations Graph PIM uses: <c>PnDTnHnMnS</c> (days,
/// hours, minutes, seconds). Weeks, months and years are not supported.
/// </summary>
public static partial class Iso8601Duration
{
    /// <summary>Parses <c>PnDTnHnMnS</c>, e.g. <c>PT8H</c>, <c>PT30M</c>, <c>PT1H30M</c>, <c>P1D</c>.</summary>
    public static TimeSpan? Parse(string text)
    {
        var match = Pattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var days = ParseInt(match.Groups[1]);
        var hours = ParseInt(match.Groups[2]);
        var minutes = ParseInt(match.Groups[3]);
        var seconds = ParseDouble(match.Groups[4]);

        var total = (double)(days * 86400 + hours * 3600 + minutes * 60) + seconds;
        if (total <= 0 && text != "PT0S")
        {
            return null;
        }

        return TimeSpan.FromSeconds(total);
    }

    /// <summary>Formats whole hours and minutes, e.g. <c>PT1H30M</c>. Seconds are dropped.</summary>
    public static string Format(TimeSpan duration)
    {
        var totalSeconds = duration.Ticks / TimeSpan.TicksPerSecond;
        var totalMinutes = totalSeconds / 60;
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        var result = "PT";
        if (hours > 0)
        {
            result += $"{hours}H";
        }

        if (minutes > 0 || hours == 0)
        {
            result += $"{minutes}M";
        }

        return result;
    }

    private static int ParseInt(Group group) =>
        group.Success && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static double ParseDouble(Group group) =>
        group.Success && double.TryParse(group.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    [GeneratedRegex(@"\AP(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?)?\z")]
    private static partial Regex Pattern();
}
