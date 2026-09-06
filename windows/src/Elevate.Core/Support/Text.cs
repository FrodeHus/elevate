using System.Globalization;

namespace Elevate.Core.Support;

/// <summary>String helpers that match Swift's <c>String</c> semantics where the two differ.</summary>
public static class Text
{
    /// <summary>
    /// The first <paramref name="count"/> user-perceived characters, like Swift's <c>prefix(_:)</c>,
    /// so a cut never lands inside a surrogate pair or a combining sequence.
    /// </summary>
    public static string Prefix(string text, int count)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var info = new StringInfo(text);
        return info.LengthInTextElements <= count ? text : info.SubstringByTextElements(0, count);
    }
}
