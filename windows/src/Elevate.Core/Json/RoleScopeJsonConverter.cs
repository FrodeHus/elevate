using System.Text.Json;
using System.Text.Json.Serialization;
using Elevate.Core.Models;

namespace Elevate.Core;

/// <summary>
/// Matches Swift's synthesised coding for <c>enum RoleScope</c>: a single-key object keyed by the
/// case name, whose value is an object of the associated values.
/// <c>{"entraDirectory":{"roleDefinitionId":"r","directoryScopeId":"/"}}</c>,
/// <c>{"azureResource":{"scope":"s","roleDefinitionId":"d"}}</c>,
/// <c>{"group":{"groupId":"g","accessId":"member"}}</c>.
/// </summary>
public sealed class RoleScopeJsonConverter : JsonConverter<RoleScope>
{
    public override bool CanConvert(Type typeToConvert) => typeof(RoleScope).IsAssignableFrom(typeToConvert);

    public override RoleScope? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject || !reader.Read() || reader.TokenType != JsonTokenType.PropertyName)
        {
            throw new JsonException("Expected a single-key object for a role scope.");
        }

        var caseName = reader.GetString();
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected a payload object for \"{caseName}\".");
        }

        var payload = ReadPayload(ref reader);

        if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException("Expected a single-key object for a role scope.");
        }

        RoleScope scope = caseName switch
        {
            "entraDirectory" => new EntraDirectoryScope(
                Require(payload, "roleDefinitionId", caseName), Require(payload, "directoryScopeId", caseName)),
            "azureResource" => new AzureResourceScope(
                Require(payload, "scope", caseName), Require(payload, "roleDefinitionId", caseName)),
            "group" => new GroupScope(
                Require(payload, "groupId", caseName), ParseAccess(Require(payload, "accessId", caseName))),
            _ => throw new JsonException($"Unknown role-scope case {caseName}"),
        };

        return typeToConvert.IsInstanceOfType(scope)
            ? scope
            : throw new JsonException($"Role scope {caseName} is not a {typeToConvert.Name}.");
    }

    public override void Write(Utf8JsonWriter writer, RoleScope value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case EntraDirectoryScope s:
                writer.WriteStartObject("entraDirectory");
                writer.WriteString("roleDefinitionId", s.RoleDefinitionId);
                writer.WriteString("directoryScopeId", s.DirectoryScopeId);
                writer.WriteEndObject();
                break;
            case AzureResourceScope s:
                writer.WriteStartObject("azureResource");
                writer.WriteString("scope", s.Scope);
                writer.WriteString("roleDefinitionId", s.RoleDefinitionId);
                writer.WriteEndObject();
                break;
            case GroupScope s:
                writer.WriteStartObject("group");
                writer.WriteString("groupId", s.GroupId);
                writer.WriteString("accessId", s.AccessId == GroupAccess.Owner ? "owner" : "member");
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException($"Unknown role-scope type {value.GetType().Name}");
        }

        writer.WriteEndObject();
    }

    private static Dictionary<string, string> ReadPayload(ref Utf8JsonReader reader)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString()!;
            reader.Read();
            if (reader.TokenType == JsonTokenType.String)
            {
                payload[name] = reader.GetString()!;
            }
            else
            {
                reader.Skip();
            }
        }

        return payload;
    }

    private static string Require(Dictionary<string, string> payload, string key, string? caseName) =>
        payload.TryGetValue(key, out var value)
            ? value
            : throw new JsonException($"Role scope \"{caseName}\" is missing \"{key}\".");

    private static GroupAccess ParseAccess(string value) => value switch
    {
        "member" => GroupAccess.Member,
        "owner" => GroupAccess.Owner,
        _ => throw new JsonException($"Unknown group access {value}"),
    };
}
