using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Elevate.Core.Storage;

namespace Elevate.Core;

/// <summary>
/// The single serializer configuration used for <c>state.json</c> and for the model types.
/// Matches the Swift side: camelCase keys, string enums, nulls omitted, ISO-8601 dates and
/// Swift <c>Duration</c>'s [seconds, attoseconds] array form.
/// </summary>
public static class Json
{
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>
    /// The same configuration with the strict-decoding flags off, for Graph and ARM responses.
    /// Those payloads legitimately omit fields the wire DTOs declare, so a missing property must
    /// leave a default rather than fail the whole read; strictness is for <c>state.json</c> only.
    /// </summary>
    public static JsonSerializerOptions LenientOptions { get; } = CreateLenient();

    private static JsonSerializerOptions CreateLenient()
    {
        var options = new JsonSerializerOptions(Options)
        {
            RespectNullableAnnotations = false,
            RespectRequiredConstructorParameters = false,
        };
        options.MakeReadOnly();
        return options;
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Strict decoding: a state.json missing a required field, or holding null where the
            // model says non-null, fails with JsonException instead of binding nulls into
            // non-nullable strings. Optional Swift fields keep their C# default values.
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new DurationJsonConverter());
        options.Converters.Add(new GuidJsonConverter());
        options.Converters.Add(new Iso8601DateTimeOffsetConverter());
        options.Converters.Add(new SignInMethodJsonConverter());
        options.Converters.Add(new EntraActivationSupportJsonConverter());
        options.Converters.Add(new RoleScopeJsonConverter());
        options.Converters.Add(new AssignmentStatusJsonConverter());
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { AllowNullCollections } };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// Swift decodes <see cref="AppState"/>'s arrays with <c>decodeIfPresent ?? []</c>, so an
    /// explicit <c>null</c> means "empty" and the setters normalise it. Without this,
    /// <c>RespectNullableAnnotations</c> would reject the null before the setter ran.
    /// </summary>
    private static void AllowNullCollections(JsonTypeInfo info)
    {
        if (info.Type != typeof(AppState))
        {
            return;
        }

        foreach (var property in info.Properties)
        {
            property.IsSetNullable = true;
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
