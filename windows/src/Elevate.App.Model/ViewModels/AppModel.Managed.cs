using Elevate.Core.Auth;
using Elevate.Core.Catalogue;
using Elevate.Core.Managed;
using Elevate.Core.Models;

namespace Elevate.App.ViewModels;

/// <summary>
/// What an organization's managed configuration permits, applied to the running app: which
/// sign-in methods may be used, which tenants may be tracked, and which ones must be. Port of the
/// macOS <c>AppModel+Managed</c>.
/// </summary>
public sealed partial class AppModel
{
    private ManagedTenantResolver? _tenantResolver;

    /// <summary>The managed configuration in effect; empty when the organization pushed none.</summary>
    public ManagedConfiguration Managed => Settings.Managed;

    /// <summary>
    /// The tenant ids the organization allows, or null when it restricts nothing — which is also
    /// the answer while any allowed entry is unresolved, since a half-applied list would lock the
    /// user out on a guess. Filled by <see cref="ResolveManagedTenantsAsync"/>.
    /// </summary>
    public IReadOnlySet<string>? AllowedTenantIds { get; private set; }

    /// <summary>The tenant ids the organization pins, in configured order.</summary>
    public IReadOnlyList<string> PinnedTenantIds { get; private set; } = [];

    /// <summary>Each managed tenant entry as configured, mapped to the id it resolved to.</summary>
    public IReadOnlyDictionary<string, string> ManagedTenantIds { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Notes about managed tenant entries that could not be resolved, shown in Settings.</summary>
    public IReadOnlyList<string> ManagedTenantWarnings { get; private set; } = [];

    /// <summary>
    /// Whether the managed tenant entries have been applied. False while the machine is offline:
    /// the network change hook runs the resolution again once the path comes back.
    /// </summary>
    public bool ManagedTenantsResolved { get; private set; }

    // MARK: Sign-in methods

    /// <summary>Whether <paramref name="method"/> may be used at all under the managed configuration.</summary>
    public bool IsMethodAllowed(SignInMethod method) => ManagedPolicy.IsAllowed(method, Managed);

    /// <summary>
    /// Whether the "Custom client ID" row is offered; the client id typed into it does not change
    /// the answer, since the managed allow-list names kinds of method, not registrations.
    /// </summary>
    public bool IsCustomMethodAllowed =>
        Managed.AllowedSignInMethods is not { } allowed || allowed.Contains(SignInMethodKind.Custom);

    /// <summary>The refusal shown wherever a withheld sign-in method would be used.</summary>
    public const string DisallowedMethodNotice = "That sign-in method is not permitted by your organization";

    /// <summary>The caption of an account whose sign-in method the organization has since withheld.</summary>
    public const string DisallowedMethodCaption = "Sign-in method no longer permitted by your organization";

    // MARK: Tenants

    /// <summary>
    /// Whether <paramref name="tenantId"/> may be tracked. Unrestricted while
    /// <see cref="AllowedTenantIds"/> is null. A pinned tenant is always allowed, even outside the
    /// allow-list: the organization pinning it says it wants it tracked, so a manual add must not
    /// refuse it and a bulk track must not drop it.
    /// </summary>
    public bool IsTenantAllowed(string tenantId)
        => PinnedTenantIds.Any(id => string.Equals(id, tenantId, StringComparison.OrdinalIgnoreCase))
            || ManagedPolicy.IsTenantAllowed(tenantId, AllowedTenantIds);

    /// <summary>Whether the organization pins this tenant, in which case the user cannot remove it.</summary>
    public bool IsPinnedTenant(TenantKey key)
        => PinnedTenantIds.Any(id => string.Equals(id, key.TenantId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The notice shown when a pinned tenant is removed.</summary>
    public const string PinnedTenantNotice = "This tenant is pinned by your organization";

    /// <summary>What the discover list's row says about a tenant off the allow-list; there is no
    /// room for the tenant's name in that column, so it stays the short form.</summary>
    public const string DisallowedTenantCaption = "Not permitted by your organization";

    /// <summary>The one refusal for a tenant off the allow-list, worded as the spec (§6.2) and the
    /// other two implementations word it.</summary>
    public static string DisallowedTenantMessage(string name) =>
        $"Tenant {name} is not permitted by your organization";

    /// <summary>
    /// Every managed tenant entry that needs a tenant id, in configured order without duplicates.
    /// One place on purpose: the tenants named by managed profiles join the list here.
    /// </summary>
    private IReadOnlyList<string> ManagedTenantEntries
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return
            [
                .. (Managed.AllowedTenants ?? [])
                    .Concat(Managed.PinnedTenants)
                    .Concat(ManagedProfileTenants)
                    .Where(seen.Add),
            ];
        }
    }

    /// <summary>
    /// True while a managed tenant entry still needs a lookup — a domain named by a profile that
    /// arrived with the fetched document, say. GUID entries are their own id and never count.
    /// </summary>
    private bool HasUnresolvedManagedTenantEntries =>
        ManagedTenantEntries.Any(entry => !Guid.TryParseExact(entry, "D", out _) && !ManagedTenantIds.ContainsKey(entry));

    /// <summary>
    /// Resolves the managed tenant entries to tenant ids, then applies them: tenants the
    /// organization does not permit are dropped (the accounts' home tenants excepted — that is
    /// where the account lives), and pinned tenants are tracked for every account. Never throws:
    /// an entry that could not be resolved becomes a warning and restricts nothing.
    /// Resolving needs the network — the entries are looked up over HTTP — so offline it does
    /// nothing at all and leaves <see cref="ManagedTenantsResolved"/> false; applying half a
    /// configuration would drop tenants on a guess.
    /// </summary>
    public async Task ResolveManagedTenantsAsync(CancellationToken ct = default)
    {
        var entries = ManagedTenantEntries;
        if (entries.Count == 0)
        {
            ManagedTenantsResolved = true;
            return;
        }

        if (!IsOnline)
        {
            ManagedTenantsResolved = false;
            return;
        }

        ManagedTenantResolution resolution;
        try
        {
            _tenantResolver ??= new ManagedTenantResolver(Http);
            resolution = await _tenantResolver.ResolveAsync(entries, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A managed restriction is never applied on a failed lookup; the app carries on unrestricted.
            LogError($"Managed tenants could not be resolved: {Describe(e)}");
            return;
        }

        ManagedTenantIds = resolution.Ids;
        var allowedEntries = Managed.AllowedTenants ?? [];
        var warnings = new List<string>();
        warnings.AddRange(allowedEntries.Where(resolution.Unresolved.Contains).Select(entry => $"AllowedTenants: could not resolve '{entry}'"));
        warnings.AddRange(Managed.PinnedTenants.Where(resolution.Unresolved.Contains).Select(entry => $"PinnedTenants: could not resolve '{entry}'"));
        ManagedTenantWarnings = warnings;

        // A restriction is applied whole or not at all: with one entry unresolved the app cannot
        // tell whether a tenant is on the list, and locking the user out on a guess is worse than
        // not applying it. Pinning is per entry, so the ones that resolved still apply.
        AllowedTenantIds = allowedEntries.Count == 0 || allowedEntries.Any(entry => !resolution.Ids.ContainsKey(entry))
            ? null
            : new HashSet<string>(allowedEntries.Select(entry => resolution.Ids[entry]), StringComparer.OrdinalIgnoreCase);
        PinnedTenantIds = [.. Managed.PinnedTenants.Where(resolution.Ids.ContainsKey).Select(entry => resolution.Ids[entry])];

        ManagedTenantsResolved = true;
        RemoveDisallowedTenants();
        foreach (var identity in Identities.ToList())
        {
            await TrackPinnedTenantsAsync(identity.Id, ct);
        }

        Touch();
    }

    /// <summary>
    /// Drops tracked tenants the organization no longer permits, telling the user which ones went.
    /// A pinned tenant is never dropped even when it is off the allow-list: <c>PinnedTenants</c>
    /// says the organization wants it tracked, and removing it here would only have it re-added
    /// below, wiping its roles and approvals on every launch.
    /// </summary>
    private void RemoveDisallowedTenants()
    {
        if (AllowedTenantIds is null)
        {
            return;
        }

        var removed = State.Tenants.Where(t => t.Source != TenantSource.Home && !IsTenantAllowed(t.TenantId)).ToList();
        if (removed.Count == 0)
        {
            return;
        }

        foreach (var tenant in removed)
        {
            ForgetTenant(tenant.Key);
        }

        LogError("Removed tenants not permitted by your organization: " + string.Join(", ", removed.Select(t => t.DisplayName)));
        Persist();
    }

    /// <summary>
    /// Tracks every pinned tenant not tracked for <paramref name="identityId"/> yet, then reads it.
    /// Called for every account after the managed tenants resolve, and for a new account once its
    /// home tenant is in place.
    /// </summary>
    public async Task TrackPinnedTenantsAsync(string identityId, CancellationToken ct = default)
    {
        if (PinnedTenantIds.Count == 0 || Identity(identityId) is not { } identity)
        {
            return;
        }

        var generation = ConfigGeneration;
        foreach (var tenantId in PinnedTenantIds)
        {
            var key = new TenantKey(identityId, tenantId);
            if (Tenant(key) is not null)
            {
                continue;
            }

            // Without a Graph name the entry as the organization wrote it is the best label there is.
            var entry = Managed.PinnedTenants.FirstOrDefault(e => ManagedTenantIds.GetValueOrDefault(e) == tenantId) ?? tenantId;
            string name;
            try
            {
                name = await InteractionRetry.RunAsync(
                    Tokens, identity, tenantId, [Scopes.GraphUserRead],
                    () => Discovery.TenantDisplayNameAsync(identity, tenantId, ct), ct: ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                name = entry;
            }

            if (generation != ConfigGeneration || Tenant(key) is not null)
            {
                continue;
            }

            State.UpsertTenant(new TenantContext(identityId, tenantId, name, TenantSource.Discovered));
            Persist();
            await RefreshAsync(key);
        }
    }
}
