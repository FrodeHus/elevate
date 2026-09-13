using System.Text.Json.Serialization;
using Elevate.Audit.Model;

namespace Elevate.Audit.Collectors;

/// <summary>Graph shapes shared by more than one collector, and the mapping to snapshot records.</summary>
public static class Wire
{
    public sealed record WirePrincipal(
        [property: JsonPropertyName("@odata.type")] string? OdataType,
        string Id,
        string? DisplayName,
        string? UserPrincipalName,
        string? UserType,
        bool? AccountEnabled,
        string? ServicePrincipalType);

    public sealed record ArmPage<T>(IReadOnlyList<T>? Value, string? NextLink);

    public static PrincipalType PrincipalTypeOf(string? odataType) => odataType?.ToLowerInvariant() switch
    {
        "#microsoft.graph.user" => PrincipalType.User,
        "#microsoft.graph.group" => PrincipalType.Group,
        "#microsoft.graph.serviceprincipal" => PrincipalType.ServicePrincipal,
        "#microsoft.graph.device" => PrincipalType.Device,
        "#microsoft.graph.orgcontact" => PrincipalType.Contact,
        _ => PrincipalType.Unknown,
    };

    public static AssignmentType? AssignmentTypeOf(string? value) => value?.ToLowerInvariant() switch
    {
        "assigned" => AssignmentType.Assigned,
        "activated" => AssignmentType.Activated,
        _ => null,
    };

    public static PrincipalRecord ToRecord(WirePrincipal p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new PrincipalRecord(
            p.Id,
            PrincipalTypeOf(p.OdataType),
            p.DisplayName,
            p.UserPrincipalName,
            string.Equals(p.UserType, "Guest", StringComparison.OrdinalIgnoreCase),
            p.AccountEnabled,
            p.ServicePrincipalType);
    }
}
