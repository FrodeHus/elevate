using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Storage;

namespace Elevate.App.ViewModels;

/// <summary>Activation profiles. Port of <c>AppModel+Profiles.swift</c>.</summary>
public sealed partial class AppModel
{
    private Guid? _profileToEdit;

    // MARK: Profiles

    /// <summary>
    /// The user's own profiles, then the ones the organization publishes. Managed profiles live
    /// only in memory: they are resolved on every read and never reach <c>state.json</c>.
    /// </summary>
    public IReadOnlyList<ActivationProfile> Profiles => [.. State.Profiles, .. ManagedProfiles];

    public ActivationProfile? Profile(Guid id) => State.Profile(id) ?? ManagedProfiles.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// The profile "Edit…" asked the Profiles window to select. The window reads and clears it, on
    /// open and again when the existing window is fronted with a new request.
    /// </summary>
    public Guid? ProfileToEdit
    {
        get => _profileToEdit;
        set
        {
            if (SetProperty(ref _profileToEdit, value))
            {
                Touch();
            }
        }
    }

    /// <summary>
    /// Bumped each time the user asks to run a profile, so an open Run window re-plans on that
    /// request and only then, never merely because it regained focus.
    /// </summary>
    public Dictionary<Guid, int> RunRequests { get; } = [];

    public void RequestRun(Guid id)
    {
        RunRequests[id] = RunRequests.GetValueOrDefault(id) + 1;
        Touch();
    }

    /// <summary>Stable, readable order: by account, then tenant, then kind, then name.</summary>
    private List<RoleKey> OrderedKeys(IEnumerable<RoleKey> keys) =>
        [.. keys
            .OrderBy(k => Identity(k.IdentityId)?.Upn ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(k => Tenant(k.TenantKey)?.DisplayName ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(k => k.Scope.Kind)
            .ThenBy(k => Role(k)?.DisplayName ?? string.Empty, StringComparer.Ordinal)];

    private ActivationProfile.Entry NewEntry(RoleKey key) => new(key, Remembered(key)?.LastDuration);

    /// <summary>The one refusal for a name a published profile already carries, worded as the CLI words it.</summary>
    public static string ManagedProfileRefusal(string name) =>
        $"'{name}' is published by your organization and cannot be changed.";

    /// <summary>
    /// Refuses a name a published profile already carries, so a user profile cannot shadow one —
    /// the CLI refuses the same name, and a state the app allowed must not error there. Returns
    /// the notice to show, or null when the name is free.
    /// </summary>
    private string? ManagedNameRefusal(string name) =>
        ManagedProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } published
            ? ManagedProfileRefusal(published.Name)
            : null;

    /// <summary>
    /// Saves a new profile, or returns null having set <see cref="Notice"/> when the organization
    /// publishes a profile by that name.
    /// </summary>
    public ActivationProfile? SaveProfile(string name, IEnumerable<RoleKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var entries = OrderedKeys(keys).Select(NewEntry);
        var trimmed = (name ?? string.Empty).Trim();
        var final = trimmed.Length == 0 ? "Untitled profile" : trimmed;
        if (ManagedNameRefusal(final) is { } refusal)
        {
            Notice = refusal;
            return null;
        }

        var profile = new ActivationProfile(final, entries);
        State.UpsertProfile(profile);
        Persist();
        return profile;
    }

    public void UpdateProfile(Guid id, IEnumerable<RoleKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (IsManagedProfile(id) || State.Profile(id) is not { } p)
        {
            return;
        }

        var old = new Dictionary<RoleKey, ActivationProfile.Entry>();
        foreach (var entry in p.Entries)
        {
            old[entry.RoleKey] = entry;
        }

        p.Entries = [.. OrderedKeys(keys).Select(k => old.GetValueOrDefault(k) ?? NewEntry(k))];
        State.UpsertProfile(p);
        Persist();
    }

    public void RenameProfile(Guid id, string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || IsManagedProfile(id) || State.Profile(id) is not { } p)
        {
            return;
        }

        if (ManagedNameRefusal(trimmed) is { } refusal)
        {
            Notice = refusal;
            return;
        }

        p.Name = trimmed;
        State.UpsertProfile(p);
        Persist();
    }

    public void DeleteProfile(Guid id)
    {
        if (IsManagedProfile(id))
        {
            return;
        }

        State.RemoveProfile(id);
        Persist();
        // The global shortcut pointed at a profile that no longer exists; drop the binding with it.
        if (Settings.HotKeyProfileId == id)
        {
            Settings.HotKeyProfileId = null;
            ApplyHotKey();
        }
    }

    /// <summary>
    /// Profiles shown as chips in the flyout, in list order. Pinned managed profiles come first
    /// and do not count against <see cref="ProfilePins.Limit"/>: the organization asked for them,
    /// so they never cost the user a pin of their own.
    /// </summary>
    public IReadOnlyList<ActivationProfile> PinnedProfiles => [.. ManagedProfiles.Where(p => p.Pinned), .. State.PinnedProfiles];

    /// <summary>
    /// Whether the user still has a pin slot free. Only the user's own pins count against
    /// <see cref="ProfilePins.Limit"/>; pinned managed profiles must not cost the user a slot here.
    /// </summary>
    public bool CanPinAnotherProfile => State.PinnedProfiles.Count < ProfilePins.Limit;

    /// <summary>
    /// Pins or unpins a profile. Returns false, changing nothing, when the pinned row is full
    /// (<see cref="ProfilePins.Limit"/>); the caller says so instead of silently ignoring the click.
    /// </summary>
    public bool SetPinned(Guid id, bool pinned)
    {
        if (IsManagedProfile(id) || !State.SetPinned(id, pinned))
        {
            return false;
        }

        Persist();
        return true;
    }

    /// <summary>
    /// Reorders the user's profiles. The managed ones are listed after them and cannot be moved,
    /// so offsets that reach into that tail are ignored rather than applied to the wrong profile.
    /// </summary>
    public void MoveProfiles(IEnumerable<int> fromOffsets, int toOffset)
    {
        ArgumentNullException.ThrowIfNull(fromOffsets);
        var count = State.Profiles.Count;
        var offsets = fromOffsets.ToList();
        if (offsets.Any(o => o >= count) || toOffset > count)
        {
            return;
        }

        State.MoveProfiles(offsets, toOffset);
        Persist();
    }

    // MARK: In-place editing (the Profiles window)

    /// <summary>An empty profile to fill in the Profiles window; the caller selects it there.</summary>
    public ActivationProfile NewProfile()
    {
        var profile = new ActivationProfile("New profile", []);
        State.UpsertProfile(profile);
        Persist();
        Touch();
        return profile;
    }

    /// <summary>
    /// Adds roles to a profile, keeping the entries it already has (and their durations) and the
    /// stable order <see cref="SaveProfile"/> uses. Keys already present are ignored.
    /// </summary>
    public void AddProfileEntries(Guid id, IEnumerable<RoleKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (IsManagedProfile(id) || State.Profile(id) is not { } p)
        {
            return;
        }

        var existing = p.Entries.Select(e => e.RoleKey).ToHashSet();
        var added = keys.Where(k => !existing.Contains(k)).Distinct().ToList();
        if (added.Count == 0)
        {
            return;
        }

        UpdateProfile(id, [.. p.Entries.Select(e => e.RoleKey), .. added]);
        Touch();
    }

    public void RemoveProfileEntry(Guid id, RoleKey key)
    {
        if (IsManagedProfile(id) || State.Profile(id) is not { } p)
        {
            return;
        }

        p.Entries.RemoveAll(e => e.RoleKey == key);
        State.UpsertProfile(p);
        Persist();
        Touch();
    }

    /// <summary>The duration the next run proposes for one entry; null falls back to memory or the policy.</summary>
    public void SetProfileEntryDuration(Guid id, RoleKey key, TimeSpan? duration)
    {
        if (IsManagedProfile(id) || State.Profile(id) is not { } p)
        {
            return;
        }

        var index = p.Entries.FindIndex(e => e.RoleKey == key);
        if (index < 0)
        {
            return;
        }

        p.Entries[index] = p.Entries[index] with { LastDuration = duration };
        State.UpsertProfile(p);
        Persist();
    }

    /// <summary>Which profile the global shortcut runs; null unbinds it. The key itself is recorded in Settings.</summary>
    public void SetHotKeyProfile(Guid? id)
    {
        Settings.HotKeyProfileId = id;
        ApplyHotKey();
        Touch();
    }

    public IReadOnlyList<ProfilePlanItem> Plan(Guid profileId)
    {
        if (Profile(profileId) is not { } p)
        {
            return [];
        }

        var rolesByKey = new Dictionary<RoleKey, EligibleRole>();
        foreach (var list in Roles.Values)
        {
            foreach (var r in list)
            {
                rolesByKey[r.Key] = r;
            }
        }

        var memoryByKey = new Dictionary<RoleKey, RoleMemory>();
        foreach (var m in State.Memory)
        {
            memoryByKey[m.RoleKey] = m;
        }

        // A tenant counts as loaded once it has a roles entry and is not mid-refresh; entries of a
        // tenant that is not loaded yet plan as NotLoaded rather than a wrong "not eligible".
        var loadedTenants = Roles.Keys.Where(k => !Busy.Contains(k)).ToHashSet();
        return ProfilePlanner.Plan(p, rolesByKey, Active, memoryByKey, loadedTenants);
    }

    /// <summary>
    /// Activates the plan's Activate items, then remembers the reason and each duration on the
    /// profile. Returns the outcomes of that activation so callers can report on them.
    /// </summary>
    public async Task<IReadOnlyList<ActivationOutcome>> RunProfileAsync(
        Guid id, IReadOnlyList<ProfilePlanItem> items, string justification, TicketInfo? ticket,
        DateTimeOffset? startDateTime = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var requests = items
            .Where(i => i.Disposition == ProfilePlanDisposition.Activate)
            .Select(i => new ActivationRequest(i.RoleKey, i.Duration, justification, ticket, i.Role?.Policy.AuthenticationContext, startDateTime))
            .ToList();
        var outcomes = requests.Count == 0 ? [] : await ActivateAsync(requests, ct);
        // A managed profile is the organization's document; nothing is remembered onto it. The
        // per-role memory the activation writes is unaffected.
        if (IsManagedProfile(id) || State.Profile(id) is not { } p)
        {
            return outcomes;
        }

        p.LastJustification = justification;
        foreach (var item in items.Where(i => i.Disposition is not (ProfilePlanDisposition.NotEligible or ProfilePlanDisposition.NotLoaded)))
        {
            // Rekey may have moved a manual Azure entry onto the key the provider resolved, so the
            // planned key can be gone. Fall back to the one active key of the same tenant carrying
            // the same display name; ambiguity means we leave the remembered duration alone.
            var index = p.Entries.FindIndex(e => e.RoleKey == item.RoleKey);
            if (index < 0 && !Active.ContainsKey(item.RoleKey) && item.Role?.DisplayName is { } name)
            {
                var candidates = Active.Keys.Where(candidate =>
                    candidate.TenantKey == item.RoleKey.TenantKey && Role(candidate)?.DisplayName == name
                    && p.Entries.Any(e => e.RoleKey == candidate)).ToList();
                if (candidates.Count == 1)
                {
                    index = p.Entries.FindIndex(e => e.RoleKey == candidates[0]);
                }
            }

            if (index >= 0)
            {
                p.Entries[index] = p.Entries[index] with { LastDuration = item.Duration };
            }
        }

        State.UpsertProfile(p);
        Persist();
        return outcomes;
    }
}
