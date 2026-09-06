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

    /// <summary>
    /// Built-in Entra directory roles bundled with the app, sorted by display name.
    /// Regenerate with <c>Scripts/update-role-catalogue.pl</c>.
    /// </summary>
    public static IReadOnlyList<CatalogueRole> EntraBuiltInRoles()
    {
        using var stream = typeof(RoleCatalogue).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new PimException(PimErrorKind.Unexpected, "EntraBuiltInRoles.json missing from bundle");

        var roles = JsonSerializer.Deserialize<List<CatalogueRole>>(stream, Json.Options)
            ?? throw new PimException(PimErrorKind.Unexpected, "EntraBuiltInRoles.json is empty");

        roles.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        return roles;
    }
}
