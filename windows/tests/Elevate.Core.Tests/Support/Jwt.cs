using System.Text.Json;
using System.Text.Json.Nodes;

namespace Elevate.Core.Tests.Support;

/// <summary>Builds a JWT-shaped token carrying the given claims, so a test can drive claim reads. Port of the Swift <c>Jwt</c>.</summary>
public static class Jwt
{
    public static string With(Action<JsonObject> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var payload = new JsonObject();
        claims(payload);
        return $"eyJhbGciOiJub25lIn0.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload))}.sig";
    }

    /// <summary>A token whose <c>wids</c> names the given directory role template ids.</summary>
    public static string WithRoles(params string[] roleTemplateIds) =>
        With(c => c["wids"] = new JsonArray([.. (roleTemplateIds ?? []).Select(id => (JsonNode)id!)]));

    /// <summary>A token whose <c>groups</c> names the given group object ids.</summary>
    public static string WithGroups(params string[] groupIds) =>
        With(c => c["groups"] = new JsonArray([.. (groupIds ?? []).Select(id => (JsonNode)id!)]));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
