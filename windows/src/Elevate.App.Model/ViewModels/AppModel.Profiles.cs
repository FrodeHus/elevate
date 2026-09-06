using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Storage;

namespace Elevate.App.ViewModels;

/// <summary>Activation profiles. Port of <c>AppModel+Profiles.swift</c>.</summary>
public sealed partial class AppModel
{
    private Guid? _editingProfileId;

    // MARK: Profiles

    public IReadOnlyList<ActivationProfile> Profiles => State.Profiles;

    public ActivationProfile? Profile(Guid id) => State.Profile(id);

    /// <summary>The profile whose selection is open in the panel; the bulk bar offers "Update profile" while set.</summary>
    public Guid? EditingProfileId
    {
        get => _editingProfileId;
        private set
        {
            if (SetProperty(ref _editingProfileId, value))
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

    public ActivationProfile SaveProfile(string name, IEnumerable<RoleKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var entries = OrderedKeys(keys).Select(NewEntry);
        var trimmed = (name ?? string.Empty).Trim();
        var profile = new ActivationProfile(trimmed.Length == 0 ? "Untitled profile" : trimmed, entries);
        State.UpsertProfile(profile);
        Persist();
        return profile;
    }

    public void UpdateProfile(Guid id, IEnumerable<RoleKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (State.Profile(id) is not { } p)
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
        if (trimmed.Length == 0 || State.Profile(id) is not { } p)
        {
            return;
        }

        p.Name = trimmed;
        State.UpsertProfile(p);
        Persist();
    }

    public void DeleteProfile(Guid id)
    {
        State.RemoveProfile(id);
        Persist();
        // The global shortcut pointed at a profile that no longer exists; drop the binding with it.
        if (Settings.HotKeyProfileId == id)
        {
            Settings.HotKeyProfileId = null;
            ApplyHotKey();
        }
    }

    public void MoveProfiles(IEnumerable<int> fromOffsets, int toOffset)
    {
        State.MoveProfiles(fromOffsets, toOffset);
        Persist();
    }

    /// <summary>Edit = reopen the selection. The bulk bar offers "Update profile" while <see cref="EditingProfileId"/> is set.</summary>
    public void BeginEditing(Guid profileId)
    {
        if (State.Profile(profileId) is not { } p)
        {
            return;
        }

        SelectMode = true;
        Selection.Clear();
        foreach (var entry in p.Entries)
        {
            Selection.Add(entry.RoleKey);
        }

        EditingProfileId = profileId;
        Touch();
    }

    public IReadOnlyList<ProfilePlanItem> Plan(Guid profileId)
    {
        if (State.Profile(profileId) is not { } p)
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
        if (State.Profile(id) is not { } p)
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
