using System.Globalization;
using System.Text.Json;

namespace Elevate.Core.Support;

/// <summary>
/// Date parsing and the shared serializer options for Microsoft Graph responses.
/// </summary>
public static class GraphJson
{
    /// <summary>
    /// Parses a Graph timestamp, tolerant of 1-7 fractional-second digits and a <c>Z</c> or
    /// explicit offset.
    /// </summary>
    public static DateTimeOffset? ParseDate(string s) =>
        DateTimeOffset.TryParse(
            s,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var value)
            ? value
            : null;

    /// <summary>The wire form the Swift side sends: UTC, whole seconds, "Z".</summary>
    public static string EncoderDateString(DateTimeOffset date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The serializer options used to decode/encode Graph and ARM payloads. Backed by the shared
    /// <see cref="Elevate.Core.Json.LenientOptions"/> rather than a duplicate configuration: same
    /// converters and naming as <c>state.json</c>, but without the strict missing-field and
    /// non-nullable checks, which a partial server response would trip on every call.
    /// </summary>
    public static JsonSerializerOptions Options => Elevate.Core.Json.LenientOptions;
}
