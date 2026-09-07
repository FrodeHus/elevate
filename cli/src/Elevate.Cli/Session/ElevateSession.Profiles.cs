using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Storage;

namespace Elevate.Cli.Session;

/// <summary>Activation profiles. Port of <c>AppModel.Profiles</c>.</summary>
public sealed partial class ElevateSession
{
    public IReadOnlyList<ActivationProfile> Profiles => State.Profiles;

    /// <summary>A profile by exact name (case-insensitive), then by unique prefix, then by id prefix.</summary>
    public ActivationProfile? FindProfile(string nameOrId)
    {
        var s = (nameOrId ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return null;
        }

        var exact = State.Profiles.Where(p => string.Equals(p.Name, s, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        var prefix = State.Profiles.Where(p => p.Name.StartsWith(s, StringComparison.OrdinalIgnoreCase)).ToList();
        if (prefix.Count == 1)
        {
            return prefix[0];
        }

        var byId = State.Profiles.Where(p => p.Id.ToString("D").StartsWith(s, StringComparison.OrdinalIgnoreCase)).ToList();
        return byId.Count == 1 ? byId[0] : null;
    }

    private List<RoleKey> OrderedKeys(IEnumerable<RoleKey> keys) =>
        [.. keys
            .OrderBy(k => AccountName(k.IdentityId), StringComparer.Ordinal)
            .ThenBy(k => TenantName(k.TenantKey), StringComparer.Ordinal)
            .ThenBy(k => k.Scope.Kind)
            .ThenBy(k => RoleName(k), StringComparer.Ordinal)];

    private ActivationProfile.Entry NewEntry(RoleKey key) => new(key, Remembered(key)?.LastDuration);

    public ActivationProfile SaveProfile(string name, IEnumerable<RoleKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var trimmed = (name ?? string.Empty).Trim();
        var profile = new ActivationProfile(trimmed.Length == 0 ? "Untitled profile" : trimmed, OrderedKeys(keys).Select(NewEntry));
        Mutate(() => State.UpsertProfile(profile));
        Persist();
        return profile;
    }

    public void UpdateProfile(ActivationProfile profile, IEnumerable<RoleKey> keys)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(keys);
        var old = profile.Entries.ToDictionary(e => e.RoleKey);
        profile.Entries = [.. OrderedKeys(keys).Select(k => old.GetValueOrDefault(k) ?? NewEntry(k))];
        Mutate(() => State.UpsertProfile(profile));
        Persist();
    }

    public void RenameProfile(ActivationProfile profile, string name)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        profile.Name = trimmed;
        Mutate(() => State.UpsertProfile(profile));
        Persist();
    }

    public void DeleteProfile(Guid id)
    {
        Mutate(() => State.RemoveProfile(id));
        Persist();
    }

    /// <summary>Copies profiles from another state file; an existing profile with the same id is replaced, same name is kept.</summary>
    public IReadOnlyList<ActivationProfile> ImportProfiles(AppState other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var imported = new List<ActivationProfile>();
        lock (_sync)
        {
            foreach (var profile in other.Profiles)
            {
                if (State.Profiles.Any(p => p.Id != profile.Id && string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                State.UpsertProfile(profile.DeepCopy());
                imported.Add(profile);
            }
        }

        Persist();
        return imported;
    }

    public IReadOnlyList<ProfilePlanItem> Plan(ActivationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_sync)
        {
            var rolesByKey = new Dictionary<RoleKey, EligibleRole>();
            foreach (var r in AllRoles)
            {
                rolesByKey[r.Key] = r;
            }

            var memoryByKey = new Dictionary<RoleKey, RoleMemory>();
            foreach (var m in State.Memory)
            {
                memoryByKey[m.RoleKey] = m;
            }

            return ProfilePlanner.Plan(profile, rolesByKey, Active, memoryByKey, LoadedTenants);
        }
    }

    /// <summary>Activates the plan's Activate items, then remembers the reason and each duration on the profile.</summary>
    public async Task<IReadOnlyList<ActivationOutcome>> RunProfileAsync(
        ActivationProfile profile, IReadOnlyList<ProfilePlanItem> items, string justification, TicketInfo? ticket,
        DateTimeOffset? startDateTime, Action<ActivationOutcome>? onProgress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(items);
        var requests = items
            .Where(i => i.Disposition == ProfilePlanDisposition.Activate)
            .Select(i => new ActivationRequest(i.RoleKey, i.Duration, justification, ticket, i.Role?.Policy.AuthenticationContext, startDateTime))
            .ToList();
        var outcomes = requests.Count == 0 ? [] : await ActivateAsync(requests, deactivateFirst: false, onProgress, ct).ConfigureAwait(false);
        lock (_sync)
        {
            if (State.Profile(profile.Id) is not { } p)
            {
                return outcomes;
            }

            p.LastJustification = justification;
            foreach (var item in items.Where(i => i.Disposition is not (ProfilePlanDisposition.NotEligible or ProfilePlanDisposition.NotLoaded)))
            {
                var index = p.Entries.FindIndex(e => e.RoleKey == item.RoleKey);
                if (index >= 0)
                {
                    p.Entries[index] = p.Entries[index] with { LastDuration = item.Duration };
                }
            }

            State.UpsertProfile(p);
            _store.Save(State);
        }

        return outcomes;
    }
}
