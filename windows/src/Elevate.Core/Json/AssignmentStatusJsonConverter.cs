using System.Text.Json;
using System.Text.Json.Serialization;
using Elevate.Core.Models;

namespace Elevate.Core;

/// <summary>
/// Matches Swift's synthesised coding for <c>ActiveAssignment.Status</c>: a single-key object keyed
/// by the case name. Plain cases carry an empty payload (<c>{"active":{}}</c>); the associated-value
/// case uses the positional label Swift generates (<c>{"failed":{"_0":"reason"}}</c>).
/// </summary>
public sealed class AssignmentStatusJsonConverter : JsonConverter<AssignmentStatus>
{
    public override AssignmentStatus? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject || !reader.Read() || reader.TokenType != JsonTokenType.PropertyName)
        {
            throw new JsonException("Expected a single-key object for an assignment status.");
        }

        var caseName = reader.GetString();
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected a payload object for \"{caseName}\".");
        }

        string? reason = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            if (name == "_0" && reader.TokenType == JsonTokenType.String)
            {
                reason = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException("Expected a single-key object for an assignment status.");
        }

        return caseName switch
        {
            "active" => AssignmentStatus.Active,
            "pendingApproval" => AssignmentStatus.PendingApproval,
            "pendingProvisioning" => AssignmentStatus.PendingProvisioning,
            "scheduled" => AssignmentStatus.Scheduled,
            "failed" => AssignmentStatus.Failed(reason ?? throw new JsonException("\"failed\" is missing its reason.")),
            _ => throw new JsonException($"Unknown assignment status {caseName}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, AssignmentStatus value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteStartObject(CaseName(value.Kind));
        if (value.Kind == AssignmentStatusKind.Failed)
        {
            writer.WriteString("_0", value.FailureReason);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string CaseName(AssignmentStatusKind kind) => kind switch
    {
        AssignmentStatusKind.Active => "active",
        AssignmentStatusKind.PendingApproval => "pendingApproval",
        AssignmentStatusKind.PendingProvisioning => "pendingProvisioning",
        AssignmentStatusKind.Scheduled => "scheduled",
        AssignmentStatusKind.Failed => "failed",
        _ => throw new JsonException($"Unknown assignment status kind {kind}"),
    };
}
