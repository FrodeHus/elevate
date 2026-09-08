using Elevate.Core.Models;

namespace Elevate.Core.Coordination;

/// <summary>
/// Remembers which eligible roles a tenant has shown before, so a panel can mark additions as
/// new and an app can notify about them. Immutable: <see cref="Observe"/> and
/// <see cref="PanelOpened"/> return the next tracker, which the model stores. Port of the Swift
/// <c>NewRoleTracker</c> value type; the JSON shape (<c>seen</c>, <c>new</c>, <c>shownOpens</c>)
/// is shared.
/// </summary>
public sealed record NewRoleTracker
{
    private readonly HashSet<RoleKey> _seen = [];
    private readonly HashSet<RoleKey> _new = [];

    /// <summary>Every role key seen in the most recent discovery. Empty means "never baselined".</summary>
    public HashSet<RoleKey> Seen
    {
        get => _seen;
        init => _seen = value ?? [];
    }

    /// <summary>Roles added since the baseline that the panel still marks.</summary>
    public HashSet<RoleKey> New
    {
        get => _new;
        init => _new = value ?? [];
    }

    /// <summary>Panel opens since <see cref="New"/> became non-empty; the marker clears at two.</summary>
    public int ShownOpens { get; init; }

    /// <summary>
    /// Records a discovery. Returns the next tracker and the additions, in no particular order.
    /// An empty discovery is ignored (a failed or consent-blocked read must not baseline away
    /// real roles), and the first non-empty discovery only baselines.
    /// </summary>
    public (NewRoleTracker Next, IReadOnlyList<RoleKey> Added) Observe(IReadOnlySet<RoleKey> discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        if (discovered.Count == 0)
        {
            return (this, []);
        }

        if (Seen.Count == 0)
        {
            return (this with { Seen = [.. discovered] }, []);
        }

        var added = discovered.Where(k => !Seen.Contains(k)).ToList();
        // A role that disappeared stops being "new"; it becomes new again if it returns.
        var stillNew = New.Union(added).Where(discovered.Contains).ToHashSet();
        return (
            this with
            {
                Seen = [.. discovered],
                New = stillNew,
                // Opens counted against a marker that has since emptied must not shorten the next one.
                ShownOpens = stillNew.Count == 0 ? 0 : ShownOpens,
            },
            added);
    }

    /// <summary>Counts a panel open while something is marked; the second one clears the marker.</summary>
    public NewRoleTracker PanelOpened()
    {
        if (New.Count == 0)
        {
            return this;
        }

        return ShownOpens + 1 >= 2
            ? this with { New = [], ShownOpens = 0 }
            : this with { ShownOpens = ShownOpens + 1 };
    }

    public bool IsNew(RoleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return New.Contains(key);
    }

    public bool Equals(NewRoleTracker? other) =>
        other is not null && ShownOpens == other.ShownOpens && Seen.SetEquals(other.Seen) && New.SetEquals(other.New);

    public override int GetHashCode() => HashCode.Combine(Seen.Count, New.Count, ShownOpens);
}
