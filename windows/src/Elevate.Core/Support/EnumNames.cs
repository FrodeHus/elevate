namespace Elevate.Core.Support;

internal static class EnumNames
{
    /// <summary>Matches an enum member by name, ignoring case; never by numeric value.</summary>
    internal static T? Parse<T>(string? raw)
        where T : struct, Enum
    {
        if (string.IsNullOrEmpty(raw) || char.IsAsciiDigit(raw[0]) || raw[0] == '-')
        {
            return null;
        }

        return Enum.TryParse<T>(raw, ignoreCase: true, out var value) ? value : null;
    }
}
