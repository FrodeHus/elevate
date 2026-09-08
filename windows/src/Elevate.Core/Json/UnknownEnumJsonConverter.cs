using System.Text.Json;
using System.Text.Json.Serialization;
using Elevate.Core.Support;

namespace Elevate.Core;

/// <summary>
/// Camel-case enum names, like the shared <see cref="JsonStringEnumConverter"/>, but a name this
/// build does not know decodes to the enum's <c>Unknown</c> member instead of failing the whole
/// document. For state that caches what a service said (access package states): a newer build
/// writing one new value must not make an older build quarantine its entire saved state.
/// </summary>
public sealed class UnknownEnumJsonConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    private static readonly T Unknown = Enum.Parse<T>("Unknown");

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string for {typeof(T).Name}.");
        }

        return EnumNames.Parse<T>(reader.GetString()) ?? Unknown;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}
