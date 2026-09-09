using Elevate.Cli.Infrastructure;
using Elevate.Core.Auth;
using Elevate.Core.Catalogue;
using Elevate.Core.Managed;
using Elevate.Core.Models;

namespace Elevate.Cli.Session;

/// <summary>
/// What the organization's managed configuration permits, applied to one CLI run: which sign-in
/// methods may be used, which tenants may be tracked, and which ones must be. Port of the macOS
/// app's <c>AppModel+Managed</c>.
/// </summary>
public sealed partial class ElevateSession
{
    /// <summary>The CLI's name for each sign-in method kind, in the order <c>--method</c> lists them.</summary>
    private static readonly (string Name, SignInMethodKind Kind)[] MethodNames =
    [
        ("own", SignInMethodKind.OwnApp),
        ("cli", SignInMethodKind.AzureCLI),
        ("pwsh", SignInMethodKind.AzurePowerShell),
        ("custom", SignInMethodKind.Custom),
    ];

    private ManagedTenantResolver? _tenantResolver;

    /// <summary>
    /// The tenant ids the organization allows, or null when it restricts nothing — which is also
    /// the answer while any allowed entry is unresolved, since a half-applied list would lock the
    /// user out on a guess.
    /// </summary>
    public IReadOnlySet<string>? AllowedTenantIds { get; private set; }

    /// <summary>The tenant ids the organization pins, in configured order.</summary>
    public IReadOnlyList<string> PinnedTenantIds { get; private set; } = [];

    /// <summary>Each managed tenant entry as configured, mapped to the id it resolved to.</summary>
    public IReadOnlyDictionary<string, string> ManagedTenantIds { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Notes about managed tenant entries that could not be resolved.</summary>
    public IReadOnlyList<string> ManagedTenantWarnings { get; private set; } = [];

    // MARK: Sign-in methods

    /// <summary>Whether <paramref name="method"/> may be used at all under the managed configuration.</summary>
    public bool IsMethodAllowed(SignInMethod method) => ManagedPolicy.IsAllowed(method, Settings.Managed);

    /// <summary>The <c>--method</c> names the configuration permits, in the order they are listed.</summary>
    internal static IReadOnlyList<string> AllowedMethodNames(ManagedConfiguration managed)
    {
        ArgumentNullException.ThrowIfNull(managed);
        return [.. MethodNames.Where(m => managed.AllowedSignInMethods is not { } allowed || allowed.Contains(m.Kind)).Select(m => m.Name)];
    }

    /// <summary>What <c>login</c> signs in with when no <c>--method</c> is given: the first allowed of own, cli, pwsh.</summary>
    internal static string DefaultMethodName(ManagedConfiguration managed)
        => AllowedMethodNames(managed).FirstOrDefault(name => name != "custom") ?? "own";

    /// <summary>The refusal for a method the organization does not permit, naming the ones it does.</summary>
    internal static CliException DisallowedMethod(string name, ManagedConfiguration managed)
        => new($"The sign-in method '{name}' is not permitted by your organization. "
            + $"Allowed: {string.Join(", ", AllowedMethodNames(managed))}.", ExitCodes.Usage);

    /// <summary>The <c>--method</c> name of a stored method, for messages about an account already added.</summary>
    internal static string MethodName(SignInMethod method)
        => MethodNames.First(m => m.Kind == method.Kind).Name;

    // MARK: Tenants

    /// <summary>Whether <paramref name="tenantId"/> may be tracked. Unrestricted while <see cref="AllowedTenantIds"/> is null.</summary>
    public bool IsTenantAllowed(string tenantId) => ManagedPolicy.IsTenantAllowed(tenantId, AllowedTenantIds);

    /// <summary>Whether the organization pins this tenant, in which case the user cannot remove it.</summary>
    public bool IsPinnedTenant(TenantKey key)
        => PinnedTenantIds.Any(id => string.Equals(id, key.TenantId, StringComparison.OrdinalIgnoreCase));

    internal const string PinnedTenantMessage = "This tenant is pinned by your organization.";

    /// <summary>
    /// Every managed tenant entry that needs a tenant id, in configured order without duplicates.
    /// One place on purpose: the tenants named by managed profiles join the list here.
    /// </summary>
    private IReadOnlyList<string> ManagedTenantEntries
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return [.. (Settings.Managed.AllowedTenants ?? []).Concat(Settings.Managed.PinnedTenants).Where(seen.Add)];
        }
    }

    /// <summary>
    /// Resolves the managed tenant entries to tenant ids, then applies them: tenants the
    /// organization does not permit are dropped (the accounts' home tenants excepted — that is
    /// where the account lives), and pinned tenants are tracked for every account. Never throws:
    /// an entry that could not be resolved becomes a warning and restricts nothing.
    /// </summary>
    public async Task ResolveManagedTenantsAsync(CancellationToken ct = default)
    {
        var entries = ManagedTenantEntries;
        if (entries.Count == 0)
        {
            return;
        }

        ManagedTenantResolution resolution;
        try
        {
            _tenantResolver ??= new ManagedTenantResolver(_http);
            resolution = await _tenantResolver.ResolveAsync(entries, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A managed restriction is never applied on a failed lookup; the run carries on unrestricted.
            LogError($"Managed tenants could not be resolved: {Describe(e)}");
            return;
        }

        ManagedTenantIds = resolution.Ids;
        var allowedEntries = Settings.Managed.AllowedTenants ?? [];
        var warnings = new List<string>();
        warnings.AddRange(allowedEntries.Where(resolution.Unresolved.Contains).Select(entry => $"AllowedTenants: could not resolve '{entry}'"));
        warnings.AddRange(Settings.Managed.PinnedTenants.Where(resolution.Unresolved.Contains).Select(entry => $"PinnedTenants: could not resolve '{entry}'"));
        ManagedTenantWarnings = warnings;

        // A restriction is applied whole or not at all: with one entry unresolved the CLI cannot
        // tell whether a tenant is on the list, and locking the user out on a guess is worse than
        // not applying it. Pinning is per entry, so the ones that resolved still apply.
        AllowedTenantIds = allowedEntries.Count == 0 || allowedEntries.Any(entry => !resolution.Ids.ContainsKey(entry))
            ? null
            : new HashSet<string>(allowedEntries.Select(entry => resolution.Ids[entry]), StringComparer.OrdinalIgnoreCase);
        PinnedTenantIds = [.. Settings.Managed.PinnedTenants.Where(resolution.Ids.ContainsKey).Select(entry => resolution.Ids[entry])];

        RemoveDisallowedTenants();
        foreach (var identity in Identities.ToList())
        {
            await TrackPinnedTenantsAsync(identity, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Drops tracked tenants the organization no longer permits, telling the user which ones went.</summary>
    private void RemoveDisallowedTenants()
    {
        if (AllowedTenantIds is null)
        {
            return;
        }

        var removed = Tenants.Where(t => t.Source != TenantSource.Home && !IsTenantAllowed(t.TenantId)).ToList();
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
    /// Tracks every pinned tenant not tracked for <paramref name="identity"/> yet. Called for every
    /// account after the managed tenants resolve, and for a new account once its home tenant is in
    /// place. The tenant is only stored; the commands read it when they need its roles.
    /// </summary>
    public async Task TrackPinnedTenantsAsync(Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        foreach (var tenantId in PinnedTenantIds)
        {
            var key = new TenantKey(identity.Id, tenantId);
            if (Tenant(key) is not null)
            {
                continue;
            }

            // Without a Graph name the entry as the organization wrote it is the best label there is.
            var entry = Settings.Managed.PinnedTenants.FirstOrDefault(e => ManagedTenantIds.GetValueOrDefault(e) == tenantId) ?? tenantId;
            string name;
            try
            {
                name = await InteractionRetry.RunAsync(
                    Tokens, identity, tenantId, [Scopes.GraphUserRead],
                    () => Discovery.TenantDisplayNameAsync(identity, tenantId, ct), ct: ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                name = entry;
            }

            Mutate(() => State.UpsertTenant(new TenantContext(identity.Id, tenantId, name, TenantSource.Discovered)));
            Persist();
        }
    }
}
