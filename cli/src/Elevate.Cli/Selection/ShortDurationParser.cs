using System.Globalization;
using System.Text.RegularExpressions;

namespace Elevate.Cli.Selection;

/// <summary>
/// Waits as people type them, down to the second: <c>30s</c>, <c>2m</c>, <c>1m30s</c>, <c>1h</c>,
/// or a plain number of seconds. <see cref="DurationParser"/> rounds to whole minutes because PIM
/// does; a settle time or a timeout should not.
/// </summary>
public static partial class ShortDurationParser
{
    public static TimeSpan? Parse(string? text)
    {
        var s = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (s.Length == 0)
        {
            return null;
        }

        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        var m = Units().Match(s);
        if (!m.Success)
        {
            return null;
        }

        var total = TimeSpan.Zero;
        if (m.Groups[1].Success)
        {
            total += TimeSpan.FromHours(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        if (m.Groups[2].Success)
        {
            total += TimeSpan.FromMinutes(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        if (m.Groups[3].Success)
        {
            total += TimeSpan.FromSeconds(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        return total;
    }

    /// <summary>Parses or throws a usage error; zero is allowed only when <paramref name="allowZero"/>.</summary>
    public static TimeSpan Require(string text, string what, bool allowZero)
    {
        var parsed = Parse(text) ?? throw new Infrastructure.CliException(
            $"Could not read the {what} '{text}'. Use forms like 30s, 2m, 1m30s or 1h.", Infrastructure.ExitCodes.Usage);
        if (parsed <= TimeSpan.Zero && !allowZero)
        {
            throw new Infrastructure.CliException($"The {what} must be longer than zero.", Infrastructure.ExitCodes.Usage);
        }

        return parsed;
    }

    /// <summary>"30 s", "2 min", "1 min 30 s", "1 h".</summary>
    public static string Label(TimeSpan d)
    {
        var total = (long)d.TotalSeconds;
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        var parts = new List<string>();
        if (hours > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{hours} h"));
        }

        if (minutes > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{minutes} min"));
        }

        if (seconds > 0 || parts.Count == 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{seconds} s"));
        }

        return string.Join(' ', parts);
    }

    [GeneratedRegex(@"^(?:(\d+)h)?\s*(?:(\d+)m(?:in)?)?\s*(?:(\d+)s(?:ec)?)?$")]
    private static partial Regex Units();
}
