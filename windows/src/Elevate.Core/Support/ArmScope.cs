using System.Globalization;

namespace Elevate.Core.Support;

/// <summary>What one step of an ARM scope path names.</summary>
public enum ArmScopeKind
{
    /// <summary>The tenant root, <c>/</c>, or a path this parser did not recognise.</summary>
    Unknown,
    ManagementGroup,
    Subscription,
    ResourceGroup,
    Resource,
}

/// <summary>
/// One step of an ARM scope path: what it names, the name itself, and the scope string that
/// reaches it. <see cref="Scope"/> is a prefix of the scope the segment came from, so it is a
/// usable scope in its own right and can key a node of the tree.
/// </summary>
public sealed record ArmScopeSegment(ArmScopeKind Kind, string Name, string Scope);

/// <summary>
/// Reading Azure Resource Manager scope strings as the hierarchy they already are:
/// <c>/subscriptions/{id}/resourceGroups/{name}/providers/{ns}/{type}/{name}</c>. The panel builds
/// its tree from this and the CLI's <c>--under</c> and glob <c>--scope</c> filters answer from it,
/// so no extra calls are needed to learn the shape.
/// <para>
/// One thing the string cannot tell us: which management group a subscription sits under. ARM
/// writes a management group scope as a flat <c>/providers/Microsoft.Management/managementGroups/{name}</c>
/// and never repeats it in a subscription's scope, so management groups are roots beside the
/// subscriptions rather than above them.
/// </para>
/// </summary>
public static class ArmScope
{
    private const string ManagementGroupPrefix = "/providers/Microsoft.Management/managementGroups/";

    /// <summary>The steps of <paramref name="scope"/>, outermost first; empty for a null or root scope.</summary>
    public static IReadOnlyList<ArmScopeSegment> Segments(string? scope)
    {
        var trimmed = (scope ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed == "/")
        {
            return [];
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<ArmScopeSegment>();
        var path = new System.Text.StringBuilder();
        var i = 0;

        // "/providers/Microsoft.Management/managementGroups/{name}" is a whole scope, not a resource
        // under something: it only ever appears on its own, so it is recognised before the loop.
        if (trimmed.StartsWith(ManagementGroupPrefix, StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
        {
            path.Append(ManagementGroupPrefix).Append(parts[3]);
            segments.Add(new ArmScopeSegment(ArmScopeKind.ManagementGroup, parts[3], path.ToString()));
            i = 4;
        }

        while (i < parts.Length)
        {
            var token = parts[i];
            if (i + 1 < parts.Length && token.Equals("subscriptions", StringComparison.OrdinalIgnoreCase))
            {
                path.Append("/subscriptions/").Append(parts[i + 1]);
                segments.Add(new ArmScopeSegment(ArmScopeKind.Subscription, parts[i + 1], path.ToString()));
                i += 2;
            }
            else if (i + 1 < parts.Length && token.Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                path.Append("/resourceGroups/").Append(parts[i + 1]);
                segments.Add(new ArmScopeSegment(ArmScopeKind.ResourceGroup, parts[i + 1], path.ToString()));
                i += 2;
            }
            else if (token.Equals("providers", StringComparison.OrdinalIgnoreCase) && i + 3 < parts.Length)
            {
                // providers/{namespace}/{type}/{name}, then (type, name) pairs for child resources.
                path.Append("/providers/").Append(parts[i + 1]).Append('/').Append(parts[i + 2]).Append('/').Append(parts[i + 3]);
                segments.Add(new ArmScopeSegment(ArmScopeKind.Resource, parts[i + 3], path.ToString()));
                i += 4;
                while (i + 1 < parts.Length)
                {
                    path.Append('/').Append(parts[i]).Append('/').Append(parts[i + 1]);
                    segments.Add(new ArmScopeSegment(ArmScopeKind.Resource, parts[i + 1], path.ToString()));
                    i += 2;
                }
            }
            else
            {
                // Something this parser does not know. Keep the rest as one step rather than
                // guessing, so an unfamiliar scope still appears in the tree under a readable name.
                var rest = string.Join('/', parts[i..]);
                path.Append('/').Append(rest);
                segments.Add(new ArmScopeSegment(ArmScopeKind.Unknown, parts[^1], path.ToString()));
                break;
            }
        }

        return segments;
    }

    /// <summary>
    /// Whether <paramref name="scope"/> is <paramref name="ancestor"/> or sits below it. Compared
    /// step by step, so <c>/subscriptions/abc</c> does not contain <c>/subscriptions/abcdef</c>.
    /// </summary>
    public static bool IsAtOrUnder(string? scope, string? ancestor)
    {
        var above = Segments(ancestor);
        if (above.Count == 0)
        {
            // The root contains everything.
            return true;
        }

        var below = Segments(scope);
        if (below.Count < above.Count)
        {
            return false;
        }

        for (var i = 0; i < above.Count; i++)
        {
            if (!below[i].Scope.Equals(above[i].Scope, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether any step of <paramref name="scope"/> is named <paramref name="name"/> — the plain
    /// form of "under this subscription" or "under this resource group", where the user types the
    /// name or id rather than the whole path. A step's own scope string is accepted too.
    /// </summary>
    public static bool HasSegmentNamed(string? scope, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var term = name.Trim().TrimEnd('/');
        if (term.Length == 0)
        {
            return false;
        }

        return Segments(scope).Any(s =>
            s.Name.Equals(term, StringComparison.OrdinalIgnoreCase)
            || s.Scope.Equals(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether <paramref name="text"/> matches the glob <paramref name="pattern"/>, where <c>*</c>
    /// absorbs any run of characters including slashes — so <c>/subscriptions/*</c> reaches
    /// everything in every subscription, not only the subscriptions themselves. The whole string
    /// must match, and case is ignored, as everywhere else in ARM.
    /// </summary>
    public static bool MatchesPattern(string pattern, string? text)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        // Same glob ARM uses for action strings; ArmActions already implements it without recursion.
        return ArmActions.Covers(pattern, text ?? string.Empty);
    }

    /// <summary>Whether <paramref name="term"/> is meant as a glob rather than as plain text.</summary>
    public static bool LooksLikePattern(string? term) => (term ?? string.Empty).Contains('*', StringComparison.Ordinal);

    /// <summary>
    /// The display name an Azure role's <c>Detail</c> caption carries — "Pay-As-You-Go · subscription"
    /// is written by <c>AzureResourceProvider.Caption</c>, and only the name part names the scope.
    /// </summary>
    public static string? DisplayName(string? detail)
    {
        var text = (detail ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var separator = text.LastIndexOf(" · ", StringComparison.Ordinal);
        var name = separator < 0 ? text : text[..separator].Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>What to call a step of the path in the panel, matching the provider's captions.</summary>
    public static string Label(ArmScopeKind kind) => kind switch
    {
        ArmScopeKind.ManagementGroup => "management group",
        ArmScopeKind.Subscription => "subscription",
        ArmScopeKind.ResourceGroup => "resource group",
        ArmScopeKind.Resource => "resource",
        _ => "scope",
    };

    /// <summary>
    /// The scope shortened for a narrow row: the last <paramref name="steps"/> steps, with a
    /// leading ellipsis for what was dropped. Truncating from the left keeps the leaf, which is
    /// the part that tells two long paths apart.
    /// </summary>
    public static string Tail(string? scope, int steps = 2)
    {
        var segments = Segments(scope);
        if (segments.Count == 0)
        {
            return "/";
        }

        var take = Math.Max(1, steps);
        if (segments.Count <= take)
        {
            return scope?.Trim() ?? "/";
        }

        var names = segments.Skip(segments.Count - take).Select(s => s.Name);
        return string.Create(CultureInfo.InvariantCulture, $"…/{string.Join('/', names)}");
    }
}
