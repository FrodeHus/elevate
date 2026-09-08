using Elevate.Core.Catalogue;
using Elevate.Core.Coordination;
using Elevate.Core.Models;

namespace Elevate.Core.Storage;

/// <summary>The justification and duration last used for one role, prefilled next time.</summary>
public sealed record RoleMemory(RoleKey RoleKey, string Justification, TimeSpan? LastDuration = null);

/// <summary>The last poll of one tenant's access packages, kept so the next poll can diff against it.</summary>
public sealed record AccessPackageRecord(TenantKey TenantKey, AccessPackageSnapshot Snapshot, DateTimeOffset PolledAt);

/// <summary>
/// Per-tenant new-role bookkeeping. An array of records, not a dictionary keyed by a struct, so
/// the macOS app can read the same file.
/// </summary>
public sealed record RoleTrackingRecord(TenantKey TenantKey, NewRoleTracker Tracker);

/// <summary>
/// Everything the app persists: the signed-in accounts, their tenants, manually named roles,
/// per-role memory, activation profiles, access package snapshots and new-role tracking.
/// Missing or null arrays decode as empty, matching Swift's <c>decodeIfPresent ?? []</c>.
/// </summary>
public sealed class AppState : IEquatable<AppState>
{
    private List<Identity> _identities = [];
    private List<TenantContext> _tenants = [];
    private List<ManualRole> _manualRoles = [];
    private List<RoleMemory> _memory = [];
    private List<ActivationProfile> _profiles = [];
    private List<AccessPackageRecord> _accessPackages = [];
    private List<RoleTrackingRecord> _roleTracking = [];

    public List<Identity> Identities
    {
        get => _identities;
        set => _identities = value ?? [];
    }

    public List<TenantContext> Tenants
    {
        get => _tenants;
        set => _tenants = value ?? [];
    }

    public List<ManualRole> ManualRoles
    {
        get => _manualRoles;
        set => _manualRoles = value ?? [];
    }

    public List<RoleMemory> Memory
    {
        get => _memory;
        set => _memory = value ?? [];
    }

    public List<ActivationProfile> Profiles
    {
        get => _profiles;
        set => _profiles = value ?? [];
    }

    public List<AccessPackageRecord> AccessPackages
    {
        get => _accessPackages;
        set => _accessPackages = value ?? [];
    }

    public List<RoleTrackingRecord> RoleTracking
    {
        get => _roleTracking;
        set => _roleTracking = value ?? [];
    }

    public ActivationProfile? Profile(Guid id) => Profiles.Find(p => p.Id == id);

    public void UpsertProfile(ActivationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var index = Profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            Profiles[index] = profile;
        }
        else
        {
            Profiles.Add(profile);
        }
    }

    public void RemoveProfile(Guid id) => Profiles.RemoveAll(p => p.Id == id);

    /// <summary>Reorders profiles, like SwiftUI's <c>move(fromOffsets:toOffset:)</c>.</summary>
    public void MoveProfiles(IEnumerable<int> fromOffsets, int toOffset)
    {
        ArgumentNullException.ThrowIfNull(fromOffsets);

        var offsets = fromOffsets.Distinct().OrderBy(i => i).ToList();
        var moving = offsets.Select(i => Profiles[i]).ToList();
        var remaining = new List<ActivationProfile>(Profiles);
        foreach (var index in Enumerable.Reverse(offsets))
        {
            remaining.RemoveAt(index);
        }

        var adjusted = toOffset - offsets.Count(i => i < toOffset);
        remaining.InsertRange(Math.Max(0, Math.Min(adjusted, remaining.Count)), moving);
        Profiles = remaining;
    }

    public IReadOnlyList<TenantContext> TenantsFor(string identityId) =>
        [.. Tenants.Where(t => t.IdentityId == identityId)];

    public void UpsertTenant(TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var index = Tenants.FindIndex(t => t.Key == tenant.Key);
        if (index >= 0)
        {
            Tenants[index] = tenant;
        }
        else
        {
            Tenants.Add(tenant);
        }
    }

    /// <summary>Drops a tenant along with the manual roles, memory and profile entries that named it.</summary>
    public void RemoveTenant(TenantKey key)
    {
        Tenants.RemoveAll(t => t.Key == key);
        ManualRoles.RemoveAll(r => r.TenantKey == key);
        Memory.RemoveAll(m => m.RoleKey.TenantKey == key);
        foreach (var profile in Profiles)
        {
            profile.Entries.RemoveAll(e => e.RoleKey.TenantKey == key);
        }

        AccessPackages.RemoveAll(r => r.TenantKey == key);
        RoleTracking.RemoveAll(r => r.TenantKey == key);
    }

    /// <summary>Drops an identity along with every tenant, role, memory and profile entry it owned.</summary>
    public void RemoveIdentity(string identityId)
    {
        Identities.RemoveAll(i => i.Id == identityId);
        foreach (var tenant in Tenants.Where(t => t.IdentityId == identityId).ToList())
        {
            RemoveTenant(tenant.Key);
        }

        foreach (var profile in Profiles)
        {
            profile.Entries.RemoveAll(e => e.RoleKey.IdentityId == identityId);
        }
    }

    public RoleMemory? MemoryFor(RoleKey key) => Memory.Find(m => m.RoleKey == key);

    public void Remember(RoleKey roleKey, string justification, TimeSpan? duration)
    {
        var entry = new RoleMemory(roleKey, justification, duration);
        var index = Memory.FindIndex(m => m.RoleKey == roleKey);
        if (index >= 0)
        {
            Memory[index] = entry;
        }
        else
        {
            Memory.Add(entry);
        }
    }

    public AccessPackageRecord? AccessPackagesFor(TenantKey key) => AccessPackages.Find(r => r.TenantKey == key);

    public void SetAccessPackages(TenantKey key, AccessPackageSnapshot snapshot, DateTimeOffset polledAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var record = new AccessPackageRecord(key, snapshot, polledAt);
        var index = AccessPackages.FindIndex(r => r.TenantKey == key);
        if (index >= 0)
        {
            AccessPackages[index] = record;
        }
        else
        {
            AccessPackages.Add(record);
        }
    }

    /// <summary>The stored tracker for a tenant, or a fresh one that is not yet stored.</summary>
    public NewRoleTracker RoleTrackerFor(TenantKey key) => RoleTracking.Find(r => r.TenantKey == key)?.Tracker ?? new NewRoleTracker();

    public void SetRoleTracker(TenantKey key, NewRoleTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        var record = new RoleTrackingRecord(key, tracker);
        var index = RoleTracking.FindIndex(r => r.TenantKey == key);
        if (index >= 0)
        {
            RoleTracking[index] = record;
        }
        else
        {
            RoleTracking.Add(record);
        }
    }

    /// <summary>
    /// A deep, independent copy: every list is new and every profile gets its own
    /// <see cref="ActivationProfile.Entries"/> list. The elements themselves are immutable
    /// records and are shared.
    /// </summary>
    /// <remarks>
    /// <see cref="AppStateStore.Load"/> and <see cref="Clone"/> are the only ways to obtain an
    /// independent graph: an <see cref="AppState"/> handed around otherwise shares its lists and
    /// its profiles, so mutating one observer's copy mutates them all.
    /// </remarks>
    public AppState Clone() => new()
    {
        Identities = [.. Identities],
        Tenants = [.. Tenants],
        ManualRoles = [.. ManualRoles],
        Memory = [.. Memory],
        Profiles = [.. Profiles.Select(p => p.DeepCopy())],
        AccessPackages = [.. AccessPackages],
        RoleTracking = [.. RoleTracking.Select(r => r with { Tracker = r.Tracker.Clone() })],
    };

    public bool Equals(AppState? other) =>
        other is not null
        && Identities.SequenceEqual(other.Identities)
        && Tenants.SequenceEqual(other.Tenants)
        && ManualRoles.SequenceEqual(other.ManualRoles)
        && Memory.SequenceEqual(other.Memory)
        && Profiles.SequenceEqual(other.Profiles)
        && AccessPackages.SequenceEqual(other.AccessPackages)
        && RoleTracking.SequenceEqual(other.RoleTracking);

    public override bool Equals(object? obj) => Equals(obj as AppState);

    public override int GetHashCode() =>
        HashCode.Combine(Identities.Count, Tenants.Count, ManualRoles.Count, Memory.Count, Profiles.Count, AccessPackages.Count, RoleTracking.Count);
}
