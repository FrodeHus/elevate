using Elevate.Core.Models;

namespace Elevate.Core.Catalogue;

/// <summary>A role the user asserts they hold in a tenant where discovery is unavailable.</summary>
public sealed record ManualRole(TenantKey TenantKey, RoleScope Scope, string DisplayName);

public static class ManualRoleSource
{
    /// <summary>The manual roles of one tenant, as eligible roles carrying the default policy.</summary>
    public static IReadOnlyList<EligibleRole> EligibleRoles(IEnumerable<ManualRole> manual, TenantKey tenantKey)
    {
        ArgumentNullException.ThrowIfNull(manual);

        return
        [
            .. manual.Where(r => r.TenantKey == tenantKey).Select(r => new EligibleRole(
                new RoleKey(tenantKey.IdentityId, tenantKey.TenantId, r.Scope),
                r.DisplayName,
                RoleSource.Manual,
                RolePolicy.ManualDefault,
                Detail(r.Scope))),
        ];
    }

    private static string? Detail(RoleScope scope) => scope switch
    {
        AzureResourceScope azure => azure.Scope,
        GroupScope group => group.AccessId == GroupAccess.Owner ? "owner" : "member",
        _ => null,
    };

    /// <summary>
    /// Discovered roles win over manual entries with the same key; a manual Azure entry is also dropped when a
    /// discovered Azure role has the same scope and display name (the manual entry names the role, ARM ids it).
    /// </summary>
    public static IReadOnlyList<EligibleRole> Merge(IEnumerable<EligibleRole> discovered, IEnumerable<EligibleRole> manual)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(manual);

        var discoveredRoles = discovered.ToList();
        var known = new HashSet<RoleKey>(discoveredRoles.Select(r => r.Key));
        var azureNames = new HashSet<string>(discoveredRoles
            .Where(r => r.Key.Scope is AzureResourceScope)
            .Select(r => AzureName(((AzureResourceScope)r.Key.Scope).Scope, r.DisplayName)));

        return
        [
            .. discoveredRoles,
            .. manual.Where(role => !known.Contains(role.Key)
                && (role.Key.Scope is not AzureResourceScope azure
                    || !azureNames.Contains(AzureName(azure.Scope, role.DisplayName)))),
        ];
    }

    private static string AzureName(string scope, string displayName) =>
        scope.ToLowerInvariant() + "|" + displayName.ToLowerInvariant();
}
