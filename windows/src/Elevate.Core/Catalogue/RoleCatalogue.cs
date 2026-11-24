using System.Text.Json;
using Elevate.Core.Models;

namespace Elevate.Core.Catalogue;

/// <summary>One built-in Entra directory role, as bundled with the app.</summary>
public sealed record CatalogueRole(string TemplateId, string DisplayName, string Description, bool IsPrivileged)
{
    /// <summary>Stable identity of the role; the template id, like Swift's <c>Identifiable</c> conformance.</summary>
    public string Id => TemplateId;
}

public static class RoleCatalogue
{
    /// <summary>The name the catalogue is embedded under; see the csproj's <c>LogicalName</c>.</summary>
    internal const string ResourceName = "Elevate.Core.Resources.EntraBuiltInRoles.json";

    /// <summary>Parsed once; the resource never changes for the life of the process.</summary>
    private static readonly Lazy<IReadOnlyList<CatalogueRole>> Cached = new(Parse);

    /// <summary>
    /// Built-in Entra directory roles bundled with the app, sorted by display name.
    /// Parsed on first use and cached. Regenerate with <c>Scripts/update-role-catalogue.pl</c>.
    /// </summary>
    public static IReadOnlyList<CatalogueRole> EntraBuiltInRoles() => Cached.Value;

    private static IReadOnlyList<CatalogueRole> Parse()
    {
        using var stream = typeof(RoleCatalogue).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new PimException(PimErrorKind.Unexpected, "EntraBuiltInRoles.json missing from bundle");

        var roles = JsonSerializer.Deserialize<List<CatalogueRole>>(stream, Json.Options)
            ?? throw new PimException(PimErrorKind.Unexpected, "EntraBuiltInRoles.json is empty");

        roles.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        return roles.AsReadOnly();
    }
}

/// <summary>
/// Which roles the app marks with a crown: Entra roles Microsoft flags privileged, and the four Azure
/// built-in privileged administrator roles. Groups and access packages are never privileged.
/// </summary>
public static class PrivilegedRoles
{
    private static readonly HashSet<string> AzureIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "8e3af657-a8ff-443c-a75c-2fe8c4bcb635", // Owner
        "b24988ac-6180-42a0-ab88-20f7382dd24c", // Contributor
        "18d7d88d-d35e-4fb5-a5c3-7773c20a72d9", // User Access Administrator
        "f58310d9-a9f6-439a-9e8d-f62e7b41a168", // Role Based Access Control Administrator
    };

    private static readonly Lazy<Dictionary<string, bool>> EntraCatalogue = new(() =>
        RoleCatalogue.EntraBuiltInRoles().ToDictionary(r => r.TemplateId, r => r.IsPrivileged, StringComparer.OrdinalIgnoreCase));

    /// <summary>The id is a full ARM id or a bare GUID; only its last segment counts.</summary>
    public static bool IsAzure(string roleDefinitionId) =>
        AzureIds.Contains(roleDefinitionId[(roleDefinitionId.LastIndexOf('/') + 1)..]);

    /// <summary>Privilege by id alone: the bundled catalogue (Entra) or the Azure ids. Used where no loaded role says.</summary>
    public static bool IsKnown(RoleScope scope) => scope switch
    {
        EntraDirectoryScope e => EntraCatalogue.Value.GetValueOrDefault(e.RoleDefinitionId),
        AzureResourceScope a => IsAzure(a.RoleDefinitionId),
        _ => false,
    };

    /// <summary>Whether to mark a role: the decoded flag, or the ids when the service did not yield it. Use this for every marker.</summary>
    public static bool IsPrivileged(EligibleRole role) => role.IsPrivileged || IsKnown(role.Key.Scope);
}
