using System.Text.Json.Serialization;

namespace Elevate.Core.Models;

/// <summary>A named set of roles and groups activated together, across accounts and tenants.</summary>
public sealed record ActivationProfile
{
    [JsonConstructor]
    public ActivationProfile(Guid id, string name, List<Entry> entries, string? lastJustification = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Id = id;
        Name = name;
        Entries = entries;
        LastJustification = lastJustification;
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
    /// A copy with its own <see cref="Entries"/> list, so mutating one profile's entries does not
    /// touch the other's. <see cref="Entry"/> is an immutable record, so the entries are shared.
    /// (Records may not declare a member named <c>Clone</c>, hence the name.)
    /// </summary>
    public ActivationProfile DeepCopy() => new(Id, Name, [.. Entries], LastJustification);

    public bool Equals(ActivationProfile? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && LastJustification == other.LastJustification
        && Entries.SequenceEqual(other.Entries);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Entries.Count, LastJustification);
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
