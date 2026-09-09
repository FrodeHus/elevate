using Elevate.Core.Models;

namespace Elevate.Core.Managed;

/// <summary>The profiles a <see cref="ManagedProfileSet"/> resolved to, plus what could not be resolved.</summary>
/// <param name="Profiles">Always <see cref="ProfileSource.Managed"/>, with ids derived from the slugs.</param>
/// <param name="Warnings">Notes such as "Prod incident: no account in tenant fabrikam.com".</param>
public sealed record ManagedProfileResolution(
    IReadOnlyList<ActivationProfile> Profiles,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Turns account-agnostic managed profiles into <see cref="ActivationProfile"/>s against the
/// signed-in accounts (design §7.2). Pure: everything it needs is passed in, so it is recomputed
/// whenever the accounts, tenants or eligible roles change. Port of the Swift
/// <c>ManagedProfileResolver</c>.
/// </summary>
public static class ManagedProfileResolver
{
    /// <param name="set">The merged inline and fetched profile document.</param>
    /// <param name="tenantIds">Managed tenant entry (as configured) → tenant id, from <see cref="ManagedTenantResolver"/>.</param>
    /// <param name="tenants">Every tenant context tracked by every signed-in account.</param>
    /// <param name="roles">The eligible roles known so far, keyed as they are in the app.</param>
    public static ManagedProfileResolution Resolve(
        ManagedProfileSet set,
        IReadOnlyDictionary<string, string> tenantIds,
        IReadOnlyList<TenantContext> tenants,
        IReadOnlyDictionary<RoleKey, EligibleRole> roles)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(tenantIds);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(roles);

        var profiles = new List<ActivationProfile>();
        var warnings = new List<string>();

        foreach (var profile in set.Profiles)
        {
            var entries = new List<ActivationProfile.Entry>();
            var seen = new HashSet<RoleKey>();

            foreach (var spec in profile.Roles)
            {
                var tenantId = TenantId(spec.Tenant, tenantIds);
                if (tenantId is null)
                {
                    warnings.Add($"{profile.Name}: could not resolve tenant {spec.Tenant}");
                    continue;
                }

                var contexts = tenants
                    .Where(t => string.Equals(t.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (contexts.Count == 0)
                {
                    warnings.Add($"{profile.Name}: no account in tenant {spec.Tenant}");
                    continue;
                }

                var matched = contexts
                    .SelectMany(context => roles.Values
                        .Where(r => r.Key.IdentityId == context.IdentityId && r.Key.TenantId == context.TenantId)
                        .Where(r => Matches(spec, r))
                        .Select(r => r.Key))
                    .ToList();

                // Nothing matched anywhere in the tenant: a placeholder per account, so the planner
                // marks it "not eligible" once the tenant has loaded and "not loaded" before that.
                List<RoleKey> keys = matched.Count > 0
                    ? matched
                    : [.. contexts.Select(c => new RoleKey(c.IdentityId, c.TenantId, Scope(spec)))];

                foreach (var key in keys)
                {
                    if (seen.Add(key))
                    {
                        entries.Add(new ActivationProfile.Entry(key, spec.Duration));
                    }
                }
            }

            profiles.Add(new ActivationProfile(
                profile.ProfileId, profile.Name, Ordered(entries, roles), profile.Reason, profile.Pinned)
            {
                Source = ProfileSource.Managed,
            });
        }

        return new ManagedProfileResolution(profiles, warnings);
    }

    /// <summary>
    /// A GUID entry is its own tenant id; anything else is a domain the resolver had to look up.
    /// The lookup ignores case: a profile may name <c>Contoso.com</c> where the policy said
    /// <c>contoso.com</c>, and the two must resolve to the same tenant on every platform.
    /// </summary>
    private static string? TenantId(string entry, IReadOnlyDictionary<string, string> tenantIds)
    {
        if (Guid.TryParseExact(entry, "D", out _))
        {
            return entry.ToLowerInvariant();
        }

        if (tenantIds.TryGetValue(entry, out var id))
        {
            return id.ToLowerInvariant();
        }

        foreach (var pair in tenantIds)
        {
            if (string.Equals(pair.Key, entry, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>Kind, then the scope constraints, then the role's name or the id the spec named it by.</summary>
    private static bool Matches(ManagedProfileSet.RoleSpec spec, EligibleRole role) => role.Key.Scope switch
    {
        EntraDirectoryScope entra =>
            spec.Kind == RoleScopeKind.EntraDirectory
            && Same(entra.DirectoryScopeId, spec.DirectoryScope)
            && (Same(role.DisplayName, spec.Role) || Same(entra.RoleDefinitionId, spec.Role)),
        AzureResourceScope azure =>
            spec.Kind == RoleScopeKind.AzureResource
            && Same(azure.Scope, spec.Scope)
            && (Same(role.DisplayName, spec.Role) || (spec.Role is { } id && Same(Tail(azure.RoleDefinitionId), Tail(id)))),
        GroupScope group =>
            spec.Kind == RoleScopeKind.Group
            && group.AccessId == spec.Access
            && (Same(role.DisplayName, spec.Group) || Same(group.GroupId, spec.Group)),
        _ => false,
    };

    private static RoleScope Scope(ManagedProfileSet.RoleSpec spec) => spec.Kind switch
    {
        RoleScopeKind.EntraDirectory => new EntraDirectoryScope(spec.Role ?? "", spec.DirectoryScope),
        RoleScopeKind.AzureResource => new AzureResourceScope(spec.Scope ?? "", spec.Role ?? ""),
        _ => new GroupScope(spec.Group ?? "", spec.Access),
    };

    /// <summary>The same order <c>SaveProfile</c> uses: account, then tenant, then kind, then name.</summary>
    private static List<ActivationProfile.Entry> Ordered(
        List<ActivationProfile.Entry> entries,
        IReadOnlyDictionary<RoleKey, EligibleRole> roles)
    {
        string Name(RoleKey key) => roles.TryGetValue(key, out var role) ? role.DisplayName : "";

        return
        [
            .. entries
                .OrderBy(e => e.RoleKey.IdentityId, StringComparer.Ordinal)
                .ThenBy(e => e.RoleKey.TenantId, StringComparer.Ordinal)
                .ThenBy(e => KindName(e.RoleKey.Scope.Kind), StringComparer.Ordinal)
                .ThenBy(e => Name(e.RoleKey), StringComparer.Ordinal)
                // A total order, so the result does not depend on the input order.
                .ThenBy(e => Identifier(e.RoleKey.Scope), StringComparer.Ordinal),
        ];
    }

    /// <summary>The Swift raw value, which is what both cores sort by.</summary>
    private static string KindName(RoleScopeKind kind) => kind switch
    {
        RoleScopeKind.EntraDirectory => "entraDirectory",
        RoleScopeKind.AzureResource => "azureResource",
        _ => "group",
    };

    private static string Identifier(RoleScope scope) => scope switch
    {
        EntraDirectoryScope entra => $"{entra.DirectoryScopeId}|{entra.RoleDefinitionId}",
        AzureResourceScope azure => $"{azure.Scope}|{azure.RoleDefinitionId}",
        GroupScope group => $"{group.GroupId}|{(group.AccessId == GroupAccess.Owner ? "owner" : "member")}",
        _ => "",
    };

    /// <summary>The trailing segment of an ARM role definition id, so a bare GUID matches a full id.</summary>
    private static string Tail(string id) =>
        id.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts ? parts[^1] : id;

    private static bool Same(string a, string? b) => b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
