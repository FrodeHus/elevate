using System.Globalization;

namespace Elevate.Cli.Selection;

/// <summary>
/// The <c>--at</c> value: a relative offset (<c>+2h</c>, <c>+30m</c>), a time of day today or
/// tomorrow (<c>14:30</c>), or a full date-time (<c>2026-09-08T09:00</c>, local unless it carries
/// an offset or <c>Z</c>).
/// </summary>
public static class StartTimeParser
{
    public static DateTimeOffset? Parse(string? text, DateTimeOffset? now = null)
    {
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return null;
        }

        var reference = now ?? DateTimeOffset.Now;
        if (s.StartsWith('+'))
        {
            return DurationParser.Parse(s[1..]) is { } offset ? reference + offset : null;
        }

        if (TimeOnly.TryParseExact(s, ["H:mm", "HH:mm", "H:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            var today = new DateTimeOffset(reference.Date + time.ToTimeSpan(), reference.Offset);
            return today > reference ? today : today.AddDays(1);
        }

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var absolute))
        {
            return absolute;
        }

        return null;
    }

    public static DateTimeOffset Require(string text)
    {
        var parsed = Parse(text) ?? throw new Infrastructure.CliException(
            $"Could not read the start time '{text}'. Use +2h, 14:30 or 2026-09-08T09:00.", Infrastructure.ExitCodes.Usage);
        if (parsed <= DateTimeOffset.Now)
        {
            throw new Infrastructure.CliException("The start time must be in the future.", Infrastructure.ExitCodes.Usage);
        }

        return parsed;
    }
}
