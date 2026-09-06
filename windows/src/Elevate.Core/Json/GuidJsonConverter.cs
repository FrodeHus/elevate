using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elevate.Core;

/// <summary>
/// Writes a <see cref="Guid"/> as Swift's <c>UUID.uuidString</c> does — upper-case, hyphenated —
/// so identifiers in <c>state.json</c> round-trip byte-for-byte with the macOS app. Reading accepts
/// either case.
/// </summary>
public sealed class GuidJsonConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected a string for a UUID.");
        }

        var text = reader.GetString();
        return Guid.TryParse(text, out var value) ? value : throw new JsonException($"Invalid UUID {text}");
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("D", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant());
    }
}
