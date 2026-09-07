using System.Globalization;
using System.Text.RegularExpressions;
using Elevate.Core.Support;

namespace Elevate.Cli.Selection;

/// <summary>
/// Durations as people type them: <c>2h</c>, <c>30m</c>, <c>1h30m</c>, <c>1.5h</c>, <c>90</c>
/// (minutes), <c>2:30</c> (hours:minutes) and ISO 8601 (<c>PT2H</c>).
/// </summary>
public static partial class DurationParser
{
    public static TimeSpan? Parse(string? text)
    {
        var s = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (s.Length == 0)
        {
            return null;
        }

        if (s.StartsWith('p'))
        {
            return Iso8601Duration.Parse(s.ToUpperInvariant());
        }

        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            return minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
        }

        var clock = Clock().Match(s);
        if (clock.Success)
        {
            var h = int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture);
            var m = int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
            var span = new TimeSpan(h, m, 0);
            return span > TimeSpan.Zero ? span : null;
        }

        var units = Units().Match(s);
        if (!units.Success)
        {
            return null;
        }

        var total = TimeSpan.Zero;
        if (units.Groups[1].Success)
        {
            total += TimeSpan.FromDays(double.Parse(units.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        if (units.Groups[2].Success)
        {
            total += TimeSpan.FromHours(double.Parse(units.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        if (units.Groups[3].Success)
        {
            total += TimeSpan.FromMinutes(double.Parse(units.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        // Whole minutes only: PIM takes PTnHnM.
        total = TimeSpan.FromMinutes(Math.Floor(total.TotalMinutes));
        return total > TimeSpan.Zero ? total : null;
    }

    /// <summary>Parses or throws a usage error naming the accepted forms.</summary>
    public static TimeSpan Require(string text) =>
        Parse(text) ?? throw new Infrastructure.CliException(
            $"Could not read the duration '{text}'. Use forms like 2h, 30m, 1h30m, 90 (minutes) or PT2H.",
            Infrastructure.ExitCodes.Usage);

    [GeneratedRegex(@"^(\d{1,2}):(\d{2})$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"^(?:(\d+(?:\.\d+)?)d)?\s*(?:(\d+(?:\.\d+)?)h)?\s*(?:(\d+(?:\.\d+)?)m(?:in)?)?$")]
    private static partial Regex Units();
}
