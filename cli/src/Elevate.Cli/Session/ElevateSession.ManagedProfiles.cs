using Elevate.Cli.Infrastructure;
using Elevate.Core.Managed;
using Elevate.Core.Models;

namespace Elevate.Cli.Session;

/// <summary>
/// Profiles an organization publishes (design §7): the inline <c>ManagedProfiles</c> document
/// merged with the one fetched from <c>ManagedProfilesUrl</c>, resolved against the accounts
/// actually signed in. They are recomputed on every read and never written to <c>state.json</c>.
/// Port of the macOS app's <c>AppModel+ManagedProfiles</c>.
/// </summary>
public sealed partial class ElevateSession
{
    /// <summary>The published document is fetched at most once a day; the cached copy stands in between.</summary>
    public static readonly TimeSpan ManagedProfilesInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// The published-profile fetch runs inside every command's session setup, so it cannot be
    /// left to the shared HTTP client's 60 s timeout: a slow or hanging endpoint would stall every
    /// command. Bounded here instead; the cached set (if any) stands in on timeout.
    /// </summary>
    internal static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The cache file, kept next to <c>state.json</c>.</summary>
    public const string ManagedProfilesCacheFile = "managed-profiles.json";

    private ManagedProfileSet _inlineProfileSet = ManagedProfileSet.Empty;
    private ManagedProfileSet? _fetchedProfileSet;
    private ManagedProfileFetcher? _profileFetcher;
    private string? _inlineProfileWarning;
    private string? _fetchWarning;

    // MARK: The set

    /// <summary>
    /// Parses the inline document once, at construction: managed configuration does not change
    /// under a running command, and a rejected document becomes a single warning rather than an
    /// error that stops the CLI.
    /// </summary>
    private void LoadInlineProfiles()
    {
        if (Settings.Managed.ManagedProfilesDocument is not { } document)
        {
            return;
        }

        try
        {
            _inlineProfileSet = ManagedProfileSet.Parse(document);
        }
        catch (ManagedProfileException e)
        {
            _inlineProfileSet = ManagedProfileSet.Empty;
            _inlineProfileWarning = $"ManagedProfiles: {e.Message}";
        }
    }

    /// <summary>The inline document merged with the fetched one, the fetched profile winning on a shared id.</summary>
    internal ManagedProfileSet PublishedProfileSet =>
        _fetchedProfileSet is { } fetched ? _inlineProfileSet.Merged(fetched) : _inlineProfileSet;

    // MARK: Resolution

    /// <summary>
    /// The published profiles against the accounts, tenants and eligible roles known right now,
    /// plus what could not be resolved. Recomputed on every read, so it always follows the state
    /// the commands have loaded.
    /// </summary>
    public ManagedProfileResolution ManagedProfileResolution
    {
        get
        {
            var set = PublishedProfileSet;
            if (set.Profiles.Count == 0)
            {
                return new ManagedProfileResolution([], []);
            }

            var rolesByKey = new Dictionary<RoleKey, EligibleRole>();
            foreach (var role in AllRoles)
            {
                rolesByKey[role.Key] = role;
            }

            return ManagedProfileResolver.Resolve(set, ManagedTenantIds, State.Tenants, rolesByKey);
        }
    }

    /// <summary>The organization's profiles, resolved against this machine's accounts.</summary>
    public IReadOnlyList<ActivationProfile> ManagedProfiles => ManagedProfileResolution.Profiles;

    /// <summary>
    /// Everything worth saying about the published profiles: the document that could not be
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
    public bool IsManagedProfile(Guid id) => PublishedProfileSet.Profiles.Any(p => p.ProfileId == id);

    // MARK: Guards

    /// <summary>The one refusal used wherever a published profile would be changed.</summary>
    internal static CliException ManagedProfileRefusal(string name) =>
        new($"'{name}' is published by your organization and cannot be changed.", ExitCodes.Usage);

    /// <summary>Refuses <paramref name="profile"/> when the organization published it.</summary>
    internal void RefuseIfManaged(ActivationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (IsManagedProfile(profile.Id))
        {
            throw ManagedProfileRefusal(profile.Name);
        }
    }

    /// <summary>Refuses a name a published profile already carries, so saving cannot shadow one.</summary>
    internal void RefuseIfManagedName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (PublishedProfileSet.Profiles.FirstOrDefault(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)) is { } published)
        {
            throw ManagedProfileRefusal(published.Name);
        }
    }

    /// <summary>Refuses an id a published profile carries; the message names the profile.</summary>
    internal void RefuseIfManagedId(Guid id)
    {
        if (PublishedProfileSet.Profiles.FirstOrDefault(p => p.ProfileId == id) is { } published)
        {
            throw ManagedProfileRefusal(published.Name);
        }
    }

    // MARK: Fetching

    /// <summary>Every tenant a published profile names, as configured.</summary>
    private IEnumerable<string> ManagedProfileTenants =>
        PublishedProfileSet.Profiles.SelectMany(p => p.Roles).Select(r => r.Tenant);

    /// <summary>
    /// True while a managed tenant entry still needs a lookup — a domain named by a profile that
    /// arrived with the fetched document, say. GUID entries are their own id and never count.
    /// </summary>
    private bool HasUnresolvedManagedTenantEntries =>
        ManagedTenantEntries.Any(entry => !Guid.TryParseExact(entry, "D", out _) && !ManagedTenantIds.ContainsKey(entry));

    /// <summary>
    /// Loads the cached document, then fetches a fresh one when it is due (or <paramref name="force"/>
    /// says so). A failed fetch keeps whatever was cached and leaves a warning behind: a published
    /// profile that momentarily cannot be downloaded should not disappear.
    /// </summary>
    public async Task RefreshManagedProfilesAsync(bool force = false, CancellationToken ct = default)
    {
        if (Settings.Managed.ManagedProfilesUrl is not { } url)
        {
            return;
        }

        _profileFetcher ??= new ManagedProfileFetcher(_http, Path.Combine(_store.Directory, ManagedProfilesCacheFile));

        // The cache first, so even a run whose fetch fails has the last good copy.
        if (_fetchedProfileSet is null && _profileFetcher.Cached() is { } cached)
        {
            _fetchedProfileSet = cached;
        }

        // Freshness only excuses the fetch when there is something cached to fall back on: a
        // fresh timestamp with no cache file (say, the cache was deleted) must still fetch.
        if (!force
            && _fetchedProfileSet is not null
            && Settings.ManagedProfilesFetchedAt is { } fetchedAt
            && (DateTimeOffset.UtcNow - fetchedAt).Duration() < ManagedProfilesInterval)
        {
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(FetchTimeout);
        try
        {
            _fetchedProfileSet = await _profileFetcher.FetchAsync(url, timeoutCts.Token).ConfigureAwait(false);
            Settings.ManagedProfilesFetchedAt = DateTimeOffset.UtcNow;
            _fetchWarning = null;

            // The document just downloaded may name a tenant by a domain nobody has looked up yet;
            // without this its roles would stay unresolved until the next run.
            if (HasUnresolvedManagedTenantEntries)
            {
                await ResolveManagedTenantsAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The timeout fired, not the caller's own token: this is a fetch failure, not a
            // cancelled command, so the cached set stands and a warning is left behind.
            var message = $"timed out after {FetchTimeout.TotalSeconds:0} s";
            _fetchWarning = $"ManagedProfilesUrl: {message}";
            LogError($"Managed profiles: {message}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var message = Describe(e);
            _fetchWarning = $"ManagedProfilesUrl: {message}";
            LogError($"Managed profiles: {message}");
        }
    }
}
