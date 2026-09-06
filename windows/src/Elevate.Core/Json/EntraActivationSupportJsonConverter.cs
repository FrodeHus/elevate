using System.Text.Json;
using System.Text.Json.Serialization;
using Elevate.Core.Models;

namespace Elevate.Core;

/// <summary>
/// Matches Swift's synthesised encoding of an enum with associated values:
/// <c>{"supported":{}}</c> or <c>{"unsupported":{"reason":"..."}}</c>.
/// </summary>
public sealed class EntraActivationSupportJsonConverter : JsonConverter<EntraActivationSupport>
{
    public override EntraActivationSupport? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject || !reader.Read() || reader.TokenType != JsonTokenType.PropertyName)
        {
            throw new JsonException("Expected a single-key object for an activation-support value.");
        }

        var caseName = reader.GetString();
        EntraActivationSupport result;
        switch (caseName)
        {
            case "supported":
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new JsonException("Expected a payload object for \"supported\".");
                }

                reader.Skip();
                result = EntraActivationSupport.Supported;
                break;
            case "unsupported":
                result = EntraActivationSupport.Unsupported(ReadReason(ref reader));
                break;
            default:
                throw new JsonException($"Unknown activation-support case {caseName}");
        }

        if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException("Expected a single-key object for an activation-support value.");
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, EntraActivationSupport value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Reason is null)
        {
            writer.WriteStartObject("supported");
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteStartObject("unsupported");
            writer.WriteString("reason", value.Reason);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static string ReadReason(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a payload object for \"unsupported\".");
        }

        string? reason = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            if (name == "reason")
            {
                reason = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        return reason ?? throw new JsonException("\"unsupported\" is missing its reason.");
    }
}
