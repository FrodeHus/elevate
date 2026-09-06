using System.Text.Json;
using System.Text.Json.Serialization;
using Elevate.Core.Models;

namespace Elevate.Core;

/// <summary>
/// Reads and writes <see cref="SignInMethod"/> as the single storage string the Swift side uses:
/// "ownApp", "azureCLI", "azurePowerShell", or "custom:&lt;client id&gt;". A JSON null decodes to
/// <see cref="SignInMethod.OwnApp"/>, matching Swift's <c>decodeIfPresent ?? .ownApp</c>.
/// </summary>
public sealed class SignInMethodJsonConverter : JsonConverter<SignInMethod>
{
    public override SignInMethod Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return SignInMethod.OwnApp;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected a string for a sign-in method.");
        }

        var key = reader.GetString();
        if (!SignInMethod.TryFromStorageKey(key, out var method))
        {
            throw new JsonException($"Unknown sign-in method {key}");
        }

        return method.Value;
    }

    public override void Write(Utf8JsonWriter writer, SignInMethod value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.StorageKey);
}
