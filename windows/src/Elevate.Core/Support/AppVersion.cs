using System.Globalization;

namespace Elevate.Core.Support;

/// <summary>
/// A semantic version parsed from a tag or assembly version string.
/// Accepts an optional leading <c>v</c>, a required <c>major.minor</c> (patch defaults to 0 when
/// omitted), and ignores build metadata introduced by a <c>+</c> (e.g. <c>1.2.3+45</c>).
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    public static AppVersion? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.AsSpan();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
        {
            s = s[1..];
        }

        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            s = s[..plus];
        }

        if (s.IsEmpty)
        {
            return null;
        }

        var parts = s.ToString().Split('.');
        if (parts.Length is not (2 or 3))
        {
            return null;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return null;
        }

        var patch = 0;
        if (parts.Length == 3 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
        {
            return null;
        }

        return new AppVersion(major, minor, patch);
    }

    public int CompareTo(AppVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// True when <paramref name="latestTag"/> parses to a version greater than <paramref name="current"/>.
    /// False when either fails to parse, or when they are equal or <paramref name="latestTag"/> is older.
    /// </summary>
    public static bool IsNewer(string latestTag, string current) =>
        Parse(latestTag) is { } latest && Parse(current) is { } now && latest > now;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
