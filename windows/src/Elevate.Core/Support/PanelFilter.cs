using System.Globalization;
using Elevate.Core.Models;

namespace Elevate.Core.Support;

/// <summary>The panel's search box: a substring match over everything a row shows, ignoring case and accents.</summary>
public static class PanelFilter
{
    public static bool IsActive(string? query) => !string.IsNullOrWhiteSpace(query);

    /// <summary>
    /// One field against the query: an empty query matches everything, otherwise a case- and
    /// diacritic-insensitive substring test.
    /// </summary>
    public static bool Matches(string? query, string? text)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length == 0)
        {
            return true;
        }

        return CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            text ?? string.Empty, q, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    public static bool Matches(string? query, EligibleRole role, string tenantName, string upn)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (!IsActive(query))
        {
            return true;
        }

        // The whole ARM path too: a role row only shows the scope's caption, but "prod" should
        // find an eligibility whose subscription is named only in the path.
        var path = role.Key.Scope is AzureResourceScope azure ? azure.Scope : null;
        string?[] fields = [role.DisplayName, role.Detail, role.ViaGroup, tenantName, upn, path];
        return fields.Any(f => Matches(query, f ?? string.Empty));
    }
}
