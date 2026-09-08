using Elevate.Core.Models;

namespace Elevate.Core.Coordination;

/// <summary>
/// Remembers which eligible roles a tenant has shown before, so a panel can mark additions as
/// new and an app can notify about them. Pure state: the model calls <see cref="Observe"/> after
/// each discovery and <see cref="PanelOpened"/> each time the panel opens. Port of the Swift
/// <c>NewRoleTracker</c>; the JSON shape (<c>seen</c>, <c>new</c>, <c>shownOpens</c>) is shared.
/// </summary>
public sealed class NewRoleTracker : IEquatable<NewRoleTracker>
{
    private HashSet<RoleKey> _seen = [];
    private HashSet<RoleKey> _new = [];

    /// <summary>Every role key seen in the most recent discovery. Empty means "never baselined".</summary>
    public HashSet<RoleKey> Seen
    {
        get => _seen;
        set => _seen = value ?? [];
    }

    /// <summary>Roles added since the baseline that the panel still marks.</summary>
    public HashSet<RoleKey> New
    {
        get => _new;
        set => _new = value ?? [];
    }

    /// <summary>Panel opens since <see cref="New"/> became non-empty; the marker clears at two.</summary>
    public int ShownOpens { get; set; }

    /// <summary>
    /// Records a discovery. Returns the additions, in no particular order. An empty discovery is
    /// ignored (a failed or consent-blocked read must not baseline away real roles), and the
    /// first non-empty discovery only baselines.
    /// </summary>
    public IReadOnlyList<RoleKey> Observe(IReadOnlySet<RoleKey> discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        if (discovered.Count == 0)
        {
            return [];
        }

        try
        {
            if (Seen.Count == 0)
            {
                return [];
            }

            var added = discovered.Where(k => !Seen.Contains(k)).ToList();
            New.UnionWith(added);
            // A role that disappeared stops being "new"; it becomes new again if it returns.
            New.IntersectWith(discovered);
            return added;
        }
        finally
        {
            Seen = [.. discovered];
        }
    }

    /// <summary>Counts a panel open while something is marked; the second one clears the marker.</summary>
    public void PanelOpened()
    {
        if (New.Count == 0)
        {
            return;
        }

        ShownOpens += 1;
        if (ShownOpens >= 2)
        {
            New.Clear();
            ShownOpens = 0;
        }
    }

    public bool IsNew(RoleKey key) => New.Contains(key);

    public NewRoleTracker Clone() => new() { Seen = [.. Seen], New = [.. New], ShownOpens = ShownOpens };

    public bool Equals(NewRoleTracker? other) =>
        other is not null && ShownOpens == other.ShownOpens && Seen.SetEquals(other.Seen) && New.SetEquals(other.New);

    public override bool Equals(object? obj) => Equals(obj as NewRoleTracker);

    public override int GetHashCode() => HashCode.Combine(Seen.Count, New.Count, ShownOpens);
}
