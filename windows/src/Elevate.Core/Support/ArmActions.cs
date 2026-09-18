namespace Elevate.Core.Support;

/// <summary>
/// Matching for Azure RBAC action strings, which are slash-separated operation names where <c>*</c>
/// stands for any run of characters — <c>Microsoft.Compute/*</c>, <c>*/read</c>, or a bare <c>*</c>.
/// Port of the Swift <c>ArmActions</c>.
/// <para>
/// Used to decide whether the permissions ARM reports for the caller already include the ones the
/// activated role definition grants. Both sides are patterns, so a granted <c>*</c> covers the
/// literal <c>*</c> an Owner definition asks for.
/// </para>
/// </summary>
public static class ArmActions
{
    /// <summary>
    /// Whether <paramref name="pattern"/> covers <paramref name="action"/>. Comparison ignores case,
    /// as ARM's own evaluation does.
    /// </summary>
    public static bool Covers(string pattern, string action)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(action);
        return Covers(pattern.AsSpan(), action.AsSpan());
    }

    /// <summary>
    /// Whether <paramref name="action"/> is granted by <paramref name="permissions"/>: some entry's
    /// <c>actions</c> covers it and none of that entry's <c>notActions</c> takes it back. ARM
    /// evaluates each role assignment on its own, so an exclusion in one entry does not remove what
    /// another entry grants.
    /// </summary>
    public static bool Grants(IEnumerable<ArmPermission> permissions, string action)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return permissions.Any(p =>
            p.Actions.Any(a => Covers(a, action)) && !p.NotActions.Any(n => Covers(n, action)));
    }

    /// <summary>
    /// Whether <paramref name="permissions"/> covers everything <paramref name="required"/> grants.
    /// An empty <paramref name="required"/> is not evidence of anything, so it answers false.
    /// </summary>
    public static bool Covers(IEnumerable<ArmPermission> permissions, IEnumerable<ArmPermission> required)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(required);

        var wanted = required.SelectMany(p => p.Actions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return wanted.Count > 0 && wanted.TrueForAll(a => Grants(permissions, a));
    }

    /// <summary>
    /// Glob matching over the whole string: every <c>*</c> in the pattern absorbs any run of
    /// characters, including slashes, which is how ARM reads <c>Microsoft.Insights/*</c>.
    /// Iterative rather than recursive so a pathological pattern cannot blow the stack.
    /// </summary>
    private static bool Covers(ReadOnlySpan<char> pattern, ReadOnlySpan<char> action)
    {
        int p = 0, a = 0, star = -1, resume = 0;
        while (a < action.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = a;
            }
            else if (p < pattern.Length && char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(action[a]))
            {
                p++;
                a++;
            }
            else if (star >= 0)
            {
                // Backtrack: let the last star absorb one more character.
                p = star + 1;
                a = ++resume;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}

/// <summary>One entry of an ARM permission set: what it grants and what it takes back.</summary>
public sealed record ArmPermission(IReadOnlyList<string> Actions, IReadOnlyList<string> NotActions)
{
    public static readonly ArmPermission None = new([], []);
}
