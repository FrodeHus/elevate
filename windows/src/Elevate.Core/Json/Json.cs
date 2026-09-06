using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elevate.Core;

/// <summary>
/// The single serializer configuration used for <c>state.json</c> and for the model types.
/// Matches the Swift side: camelCase keys, string enums, nulls omitted, ISO-8601 dates and
/// Swift <c>Duration</c>'s [seconds, attoseconds] array form.
/// </summary>
public static class Json
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new DurationJsonConverter());
        options.Converters.Add(new Iso8601DateTimeOffsetConverter());
        options.Converters.Add(new SignInMethodJsonConverter());
        options.Converters.Add(new EntraActivationSupportJsonConverter());
        options.Converters.Add(new RoleScopeJsonConverter());
        options.Converters.Add(new AssignmentStatusJsonConverter());
        options.TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        options.MakeReadOnly();
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
