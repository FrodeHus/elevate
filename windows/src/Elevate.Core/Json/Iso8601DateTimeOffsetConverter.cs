using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elevate.Core;

/// <summary>
/// Writes instants exactly as the Swift side does (UTC, whole seconds, "Z"), and reads any
/// ISO-8601 form Graph or an older state file may carry.
/// </summary>
public sealed class Iso8601DateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected an ISO-8601 string for a date.");
        }

        var text = reader.GetString();
        if (text is null || !DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value))
        {
            throw new JsonException($"Bad date {text}");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
