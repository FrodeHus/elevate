using System.Text.Json.Serialization;

namespace Elevate.Core.Models;

/// <summary>
/// Where a profile came from. User profiles live in <c>state.json</c>; managed ones are published
/// by the organization and resolved at runtime, so they are never persisted.
/// </summary>
public enum ProfileSource { User, Managed }

/// <summary>A named set of roles and groups activated together, across accounts and tenants.</summary>
public sealed record ActivationProfile
{
    [JsonConstructor]
    public ActivationProfile(Guid id, string name, List<Entry> entries, string? lastJustification = null, bool pinned = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Id = id;
        Name = name;
        Entries = entries;
        LastJustification = lastJustification;
        Pinned = pinned;
    }

    /// <summary>A new profile with a fresh id.</summary>
    public ActivationProfile(string name, IEnumerable<Entry> entries, string? lastJustification = null)
        : this(Guid.NewGuid(), name, [.. entries ?? []], lastJustification)
    {
    }

    /// <summary>One role in a profile; <see cref="Entry.LastDuration"/> is null until the profile has run.</summary>
    public sealed record Entry(RoleKey RoleKey, TimeSpan? LastDuration = null);

    public Guid Id { get; set; }

    public string Name { get; set; }

    public List<Entry> Entries { get; set; }

    /// <summary>Reason entered on the last run; prefilled next time.</summary>
    public string? LastJustification { get; set; }

    /// <summary>
    /// Shown as a chip in the panel; at most <see cref="ProfilePins.Limit"/> profiles are pinned at a
    /// time. Written only when true, so files from before pinning existed round-trip unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Pinned { get; set; }

    /// <summary>
    /// <see cref="ProfileSource.Managed"/> profiles are published by the organization: read-only in
    /// the UI and never saved. Written only when managed, so a user profile's JSON is unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ProfileSource Source { get; init; } = ProfileSource.User;

    /// <summary>
    /// A copy with its own <see cref="Entries"/> list, so mutating one profile's entries does not
    /// touch the other's. <see cref="Entry"/> is an immutable record, so the entries are shared.
    /// (Records may not declare a member named <c>Clone</c>, hence the name.)
    /// </summary>
    public ActivationProfile DeepCopy() => new(Id, Name, [.. Entries], LastJustification, Pinned) { Source = Source };

    public bool Equals(ActivationProfile? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && LastJustification == other.LastJustification
        && Pinned == other.Pinned
        && Source == other.Source
        && Entries.SequenceEqual(other.Entries);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Entries.Count, LastJustification, Pinned, Source);
}

public static class ProfilePins
{
    /// <summary>How many profiles may be pinned to the panel; one row of chips that never wraps.</summary>
    public const int Limit = 4;
}

public static class ProfileSummary
{
    /// <summary>"3 roles · 1 group" style caption for a chip. Entra and Azure count as roles.</summary>
    public static string Caption(IEnumerable<ActivationProfile.Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var all = entries.ToList();
        var groups = all.Count(e => e.RoleKey.Scope.Kind == RoleScopeKind.Group);
        var roles = all.Count - groups;
        var parts = new List<string>();
        if (roles > 0)
        {
            parts.Add($"{roles} role{(roles == 1 ? "" : "s")}");
        }

        if (groups > 0)
        {
            parts.Add($"{groups} group{(groups == 1 ? "" : "s")}");
        }

        return parts.Count == 0 ? "empty" : string.Join(" · ", parts);
    }
}
