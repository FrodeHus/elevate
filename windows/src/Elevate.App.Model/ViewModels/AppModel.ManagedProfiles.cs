using Elevate.Core.Managed;
using Elevate.Core.Models;

namespace Elevate.App.ViewModels;

/// <summary>
/// Profiles an organization publishes (design §7): the inline <c>ManagedProfiles</c> document
/// merged with the one fetched from <c>ManagedProfilesUrl</c>, resolved against the accounts
/// actually signed in. They are recomputed from observable state and never written to
/// <c>state.json</c>. Port of the macOS <c>AppModel+ManagedProfiles</c>.
/// </summary>
public sealed partial class AppModel
{
    /// <summary>The published document is fetched at most once a day; the cached copy stands in between.</summary>
    public static readonly TimeSpan ManagedProfilesInterval = TimeSpan.FromHours(24);

    /// <summary>The cache file, kept next to <c>state.json</c>.</summary>
    public const string ManagedProfilesCacheFile = "managed-profiles.json";

    private ManagedProfileSet _inlineProfileSet = Elevate.Core.Managed.ManagedProfileSet.Empty;
    private ManagedProfileSet? _fetchedProfileSet;
    private ManagedProfileFetcher? _profileFetcher;
    private string? _inlineProfileWarning;
    private string? _fetchWarning;

    // MARK: The set

    /// <summary>
    /// Parses the inline document once, at construction: managed configuration does not change
    /// under a running app, and a rejected document becomes a single warning rather than an error.
    /// </summary>
    private void LoadInlineProfiles()
    {
        if (Managed.ManagedProfilesDocument is not { } document)
        {
            return;
        }

        try
        {
            _inlineProfileSet = Elevate.Core.Managed.ManagedProfileSet.Parse(document);
        }
        catch (ManagedProfileException e)
        {
            _inlineProfileSet = Elevate.Core.Managed.ManagedProfileSet.Empty;
            _inlineProfileWarning = $"ManagedProfiles: {e.Message}";
        }
    }

    /// <summary>The inline document as configured, before the fetched one is merged in.</summary>
    public ManagedProfileSet InlineProfileSet => _inlineProfileSet;

    /// <summary>The document fetched from <c>ManagedProfilesUrl</c>, or null until one lands.</summary>
    public ManagedProfileSet? FetchedProfileSet => _fetchedProfileSet;

    /// <summary>The inline document merged with the fetched one, the fetched profile winning on a shared id.</summary>
    public ManagedProfileSet ManagedProfileSet =>
        _fetchedProfileSet is { } fetched ? _inlineProfileSet.Merged(fetched) : _inlineProfileSet;

    /// <summary>When the published document was last fetched successfully; null until the first one lands.</summary>
    public DateTimeOffset? ManagedProfilesFetchedAt => Settings.ManagedProfilesFetchedAt;

    // MARK: Resolution

    /// <summary>
    /// The published profiles against the accounts, tenants and eligible roles known right now,
    /// plus what could not be resolved. Recomputed on every read, so it always follows the state
    /// the views observe.
    /// </summary>
    public ManagedProfileResolution ManagedProfileResolution
    {
        get
        {
            var set = ManagedProfileSet;
            if (set.Profiles.Count == 0)
            {
                return new Elevate.Core.Managed.ManagedProfileResolution([], []);
            }

            var rolesByKey = new Dictionary<RoleKey, EligibleRole>();
            foreach (var list in Roles.Values)
            {
                foreach (var role in list)
                {
                    rolesByKey[role.Key] = role;
                }
            }

            return ManagedProfileResolver.Resolve(set, ManagedTenantIds, State.Tenants, rolesByKey);
        }
    }

    /// <summary>The organization's profiles, resolved against this machine's accounts.</summary>
    public IReadOnlyList<ActivationProfile> ManagedProfiles => ManagedProfileResolution.Profiles;

    /// <summary>
    /// Everything Settings shows about the published profiles: the document that could not be
    /// parsed, the fetch that failed, and what the resolution could not match.
    /// </summary>
    public IReadOnlyList<string> ManagedProfileWarnings =>
    [
        .. new[] { _inlineProfileWarning, _fetchWarning }.OfType<string>(),
        .. ManagedProfileResolution.Warnings,
    ];

    /// <summary>
    /// Whether this id belongs to a published profile — true even when it resolved to nothing, so
    /// an unresolved managed profile is never mistaken for a user profile and edited.
    /// </summary>
    public bool IsManagedProfile(Guid id) => ManagedProfileSet.Profiles.Any(p => p.ProfileId == id);

    // MARK: Fetching

    /// <summary>Every tenant a published profile names, as configured.</summary>
    private IEnumerable<string> ManagedProfileTenants =>
        ManagedProfileSet.Profiles.SelectMany(p => p.Roles).Select(r => r.Tenant);

    /// <summary>
    /// Loads the cached document, then fetches a fresh one when it is due (or <paramref name="force"/>
    /// says so). A failed fetch keeps whatever was cached and leaves a warning behind: a published
    /// profile that momentarily cannot be downloaded should not disappear from the flyout.
    /// </summary>
    public async Task RefreshManagedProfilesAsync(bool force = false, CancellationToken ct = default)
    {
        if (Managed.ManagedProfilesUrl is not { } url)
        {
            return;
        }

        _profileFetcher ??= new ManagedProfileFetcher(Http, Path.Combine(_store.Directory, ManagedProfilesCacheFile));

        // The cache first, so even a launch whose fetch fails has the last good copy.
        if (_fetchedProfileSet is null && _profileFetcher.Cached() is { } cached)
        {
            _fetchedProfileSet = cached;
            Touch();
        }

        if (!IsOnline)
        {
            return;
        }

        if (!force
            && Settings.ManagedProfilesFetchedAt is { } fetchedAt
            && (DateTimeOffset.UtcNow - fetchedAt).Duration() < ManagedProfilesInterval)
        {
            return;
        }

        try
        {
            _fetchedProfileSet = await _profileFetcher.FetchAsync(url, ct);
            Settings.ManagedProfilesFetchedAt = DateTimeOffset.UtcNow;
            _fetchWarning = null;

            // The document just downloaded may name a tenant by a domain nobody has looked up yet;
            // without this its roles would stay unresolved until the next launch.
            if (HasUnresolvedManagedTenantEntries)
            {
                await ResolveManagedTenantsAsync(ct);
            }

            Touch();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            var message = Describe(e);
            _fetchWarning = $"ManagedProfilesUrl: {message}";
            LogError($"Managed profiles: {message}");
        }
    }
}
