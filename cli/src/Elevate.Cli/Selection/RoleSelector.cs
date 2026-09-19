using Elevate.Cli.Infrastructure;
using Elevate.Cli.Session;
using Elevate.Core.Models;
using Elevate.Core.Support;

namespace Elevate.Cli.Selection;

/// <summary>Filters shared by every command that names roles: account, tenant, kind and scope.</summary>
public sealed record RoleFilter(
    string? Account = null,
    string? Tenant = null,
    RoleScopeKind? Kind = null,
    string? Scope = null,
    string? Under = null)
{
    public static RoleScopeKind? ParseKind(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" => null,
        "entra" or "directory" or "entradirectory" => RoleScopeKind.EntraDirectory,
        "azure" or "arm" or "azureresource" => RoleScopeKind.AzureResource,
        "group" or "groups" => RoleScopeKind.Group,
        var other => throw new CliException($"Unknown kind '{other}'. Use entra, azure or groups.", ExitCodes.Usage),
    };
}

/// <summary>
/// Turns what the user typed into role keys. A term is a short id from a listing, or a role name
/// matched exactly first and then as a substring, case-insensitively. The filters narrow the
/// candidates; a term that still matches several roles is an error that lists them.
/// </summary>
public static class RoleSelector
{
    public static bool TenantMatches(ElevateSession session, TenantContext tenant, RoleFilter filter)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Account is { Length: > 0 } account)
        {
            var identity = session.Identity(tenant.IdentityId);
            if (identity is null || !(Contains(identity.Upn, account) || Contains(identity.DisplayName, account) || identity.Id.Equals(account, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (filter.Tenant is { Length: > 0 } t)
        {
            if (!(Contains(tenant.DisplayName, t) || tenant.TenantId.Equals(t, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The tenants a command needs to read for these filters.</summary>
    public static IReadOnlyList<TenantKey> TenantsFor(ElevateSession session, RoleFilter filter)
    {
        ArgumentNullException.ThrowIfNull(session);
        return [.. session.Tenants.Where(t => TenantMatches(session, t, filter)).Select(t => t.Key)];
    }

    public static bool RoleMatches(ElevateSession session, EligibleRole role, RoleFilter filter)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (filter.Kind is { } kind && role.Key.Scope.Kind != kind)
        {
            return false;
        }

        if (filter.Scope is { Length: > 0 } scope && !ScopeMatches(role, scope))
        {
            return false;
        }

        // --under reads the path as the hierarchy it is, so one term reaches a whole subtree.
        if (filter.Under is { Length: > 0 } under && !UnderMatches(role, under))
        {
            return false;
        }

        return session.Tenant(role.Key.TenantKey) is { } tenant && TenantMatches(session, tenant, filter);
    }

    /// <summary>
    /// <c>--scope</c>: plain text is a substring of the path and the caption, as it has always
    /// been; a value carrying a <c>*</c> is a glob matched whole against the path, the caption and
    /// the leaf's name, so <c>--scope "/subscriptions/*"</c> reaches everything in every subscription.
    /// </summary>
    private static bool ScopeMatches(EligibleRole role, string scope)
    {
        var path = role.Key.Scope is AzureResourceScope azure ? azure.Scope : null;
        if (!ArmScope.LooksLikePattern(scope))
        {
            var text = path is null ? role.Detail ?? string.Empty : path + " " + (role.Detail ?? string.Empty);
            return Contains(text, scope);
        }

        var leaf = path is null ? null : ArmScope.Segments(path).LastOrDefault()?.Name;
        return ArmScope.MatchesPattern(scope, path)
            || ArmScope.MatchesPattern(scope, role.Detail)
            || (leaf is not null && ArmScope.MatchesPattern(scope, leaf));
    }

    /// <summary>
    /// <c>--under</c>: only Azure roles, and only those at or below the named scope. The term is a
    /// whole scope path, or the name or id of one step of it.
    /// </summary>
    private static bool UnderMatches(EligibleRole role, string under)
    {
        if (role.Key.Scope is not AzureResourceScope azure)
        {
            return false;
        }

        return under.StartsWith('/')
            ? ArmScope.IsAtOrUnder(azure.Scope, under)
            : ArmScope.HasSegmentNamed(azure.Scope, under);
    }

    public static IReadOnlyList<EligibleRole> Filtered(ElevateSession session, RoleFilter filter)
    {
        ArgumentNullException.ThrowIfNull(session);
        return [.. session.AllRoles.Where(r => RoleMatches(session, r, filter))];
    }

    /// <summary>Resolves each term to exactly one role, or throws listing the candidates.</summary>
    public static IReadOnlyList<EligibleRole> Resolve(ElevateSession session, IEnumerable<string> terms, RoleFilter filter)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(terms);
        var candidates = Filtered(session, filter);
        var chosen = new List<EligibleRole>();
        foreach (var raw in terms)
        {
            var term = raw.Trim();
            if (term.Length == 0)
            {
                continue;
            }

            var matches = Matches(candidates, term);
            if (matches.Count == 0)
            {
                throw new CliException($"No eligible role matches '{term}'. Run 'elevate roles' to see what is available.", ExitCodes.NotFound);
            }

            if (matches.Count > 1)
            {
                var lines = matches.Select(m => $"  {ShortId.For(m.Key)}  {m.DisplayName}  ({Describe(session, m)})");
                throw new CliException(
                    $"'{term}' matches {matches.Count} roles; use the id, narrow with --tenant, --account, --kind, --scope or --under, or take them all with --all:\n" + string.Join('\n', lines),
                    ExitCodes.NotFound);
            }

            if (!chosen.Contains(matches[0]))
            {
                chosen.Add(matches[0]);
            }
        }

        return chosen;
    }

    /// <summary>
    /// Every role each term matches, rather than insisting on one. What <c>--all</c> asks for: with
    /// no terms it is the whole filtered list, so <c>--under</c> or a glob <c>--scope</c> selects a
    /// subtree the way the panel's checkbox does.
    /// </summary>
    public static IReadOnlyList<EligibleRole> ResolveAll(ElevateSession session, IEnumerable<string> terms, RoleFilter filter)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(terms);
        var candidates = Filtered(session, filter);
        var wanted = terms.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (wanted.Count == 0)
        {
            return candidates;
        }

        var chosen = new List<EligibleRole>();
        foreach (var term in wanted)
        {
            var matches = Matches(candidates, term);
            if (matches.Count == 0)
            {
                throw new CliException($"No eligible role matches '{term}'. Run 'elevate roles' to see what is available.", ExitCodes.NotFound);
            }

            chosen.AddRange(matches.Where(m => !chosen.Contains(m)));
        }

        return chosen;
    }

    private static List<EligibleRole> Matches(IReadOnlyList<EligibleRole> candidates, string term)
    {
        if (ShortId.LooksLikeId(term))
        {
            var byId = candidates.Where(r => ShortId.For(r.Key) == term).ToList();
            if (byId.Count > 0)
            {
                return byId;
            }
        }

        var exact = candidates.Where(r => string.Equals(r.DisplayName, term, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        return candidates.Where(r => Contains(r.DisplayName, term)).ToList();
    }

    public static string Describe(ElevateSession session, EligibleRole role)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(role);
        var parts = new List<string> { KindLabel(role.Key.Scope.Kind) };
        if (role.Detail is { Length: > 0 } detail)
        {
            parts.Add(detail);
        }

        parts.Add(session.TenantName(role.Key.TenantKey));
        parts.Add(session.AccountName(role.Key.IdentityId));
        return string.Join(" · ", parts);
    }

    public static string KindLabel(RoleScopeKind kind) => kind switch
    {
        RoleScopeKind.EntraDirectory => "Entra",
        RoleScopeKind.AzureResource => "Azure",
        _ => "Group",
    };

    private static bool Contains(string? text, string term) =>
        text is not null && text.Contains(term, StringComparison.OrdinalIgnoreCase);
}
