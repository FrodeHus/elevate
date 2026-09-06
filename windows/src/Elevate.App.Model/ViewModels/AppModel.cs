using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Elevate.App.Auth;
using Elevate.App.Notifications;
using Elevate.App.Services;
using Elevate.Core.Auth;
using Elevate.Core.Catalogue;
using Elevate.Core.Coordination;
using Elevate.Core.Discovery;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Providers;
using Elevate.Core.Storage;
using Elevate.Core.Support;

namespace Elevate.App.ViewModels;

/// <summary>
/// The application view model: accounts, tenants, roles, assignments and the session state the
/// flyout shows. Port of the macOS <c>AppModel</c> and its <c>AppModel+*.swift</c> extensions.
/// </summary>
/// <remarks>
/// Every member must be called on the thread that constructed the model (the UI thread). The
/// model captures that thread's <see cref="SynchronizationContext"/> so callbacks arriving from
/// the coordinator or the network monitor are marshalled back before touching state.
/// </remarks>
public sealed partial class AppModel : ObservableObject, IDisposable
{
    // MARK: Roles and assignments — AppModel.Refresh, AppModel.Activation

    /// <summary>Persisted state. Mutated by the Accounts, Refresh and Activation parts.</summary>
    public AppState State { get; internal set; } = new();

    public Dictionary<TenantKey, List<EligibleRole>> Roles { get; } = [];

    public Dictionary<RoleKey, ActiveAssignment> Active { get; } = [];

    public HashSet<TenantKey> Busy { get; } = [];

    public Dictionary<TenantKey, string> TenantErrors { get; } = [];

    public Dictionary<RoleKey, ActivationResult> Progress { get; } = [];

    /// <summary>Roles with an activation or deactivation request currently in flight; rows show a busy indicator.</summary>
    public HashSet<RoleKey> InFlight { get; } = [];

    // MARK: Panel — AppModel.Panel

    private bool _selectMode;
    private string _searchQuery = string.Empty;
    private string? _startupError;
    private string? _notice;
    private RoleKey? _pendingExtend;

    public bool SelectMode
    {
        get => _selectMode;
        set
        {
            if (SetProperty(ref _selectMode, value) && !value)
            {
                Selection.Clear();
                EditingProfileId = null;
            }

            Touch();
        }
    }

    /// <summary>Panel search. Not persisted; changing it drops the bulk selection since rows may disappear.</summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value ?? string.Empty))
            {
                Selection.Clear();
                Touch();
            }
        }
    }

    /// <summary>Collapsed state lives here, not in the views: list rows are recreated as they scroll.</summary>
    public HashSet<TenantKey> CollapsedTenants { get; } = [];

    public HashSet<string> CollapsedIdentities { get; } = [];

    public HashSet<RoleKey> Selection { get; } = [];

    public string? StartupError
    {
        get => _startupError;
        set
        {
            if (SetProperty(ref _startupError, value))
            {
                Touch();
            }
        }
    }

    /// <summary>Transient, dismissible message (failed sign-in, unreadable state file). Never blocks the panel.</summary>
    public string? Notice
    {
        get => _notice;
        set
        {
            if (SetProperty(ref _notice, value))
            {
                Touch();
            }
        }
    }

    /// <summary>Set by the expiry toast's Extend action; the shell opens the activation window for it.</summary>
    public RoleKey? PendingExtend
    {
        get => _pendingExtend;
        set
        {
            if (SetProperty(ref _pendingExtend, value))
            {
                Touch();
            }
        }
    }

    // MARK: Refresh — AppModel.Refresh

    /// <summary>Tenants whose interactive sign-in the user dismissed this session; refreshes stay silent for them until Refresh or Retry discovery.</summary>
    internal HashSet<TenantKey> DeclinedTenants { get; } = [];

    internal bool Bootstrapped { get; private set; }

    internal DateTimeOffset LastRefresh { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Policies are stable per role; fetching them again on every refresh is wasted quota.</summary>
    internal Dictionary<RoleKey, RolePolicy> PolicyCache { get; } = [];

    /// <summary>Coarse "now" for the tray icon, which must not drive its own timer; ticks every 30 s.</summary>
    public DateTimeOffset Clock { get; private set; } = DateTimeOffset.UtcNow;

    private CancellationTokenSource? _timers;

    /// <summary>
    /// Bumped by <see cref="ApplyClientId"/>; in-flight refreshes started under an older client id
    /// check this before writing to state so they cannot repopulate what was just cleared.
    /// </summary>
    internal int ConfigGeneration { get; private set; }

    /// <summary>
    /// The last errors the user was shown, for "Copy diagnostics". Session only: a support report
    /// describes this launch, and a persisted log would be one more file holding service messages
    /// we cannot vet.
    /// </summary>
    public ErrorLog ErrorLog { get; } = new();

    /// <summary>One global hot key, created with the model and reconfigured by <see cref="ApplyHotKey"/>.</summary>
    internal IHotKeyCenter HotKeys { get; }

    // MARK: Dependencies

    public AppSettings Settings { get; }

    public ITokenProvider Tokens { get; private set; }

    public ActivationCoordinator Coordinator { get; private set; }

    public TenantDiscovery Discovery { get; private set; }

    private readonly AppStateStore _store;

    public IExpiryNotifier Notifier { get; }

    private readonly INetworkMonitor _network;

    public IHttpClient Http { get; }

    /// <summary>The own-app half of <see cref="Tokens"/>, swapped by <see cref="ApplyClientId"/>.</summary>
    private IOwnAppTokenProvider? _ownApp;

    private readonly IFirstPartyProviders _firstParty;

    /// <summary>Builds the own-app provider for a client id; null when the build cannot sign in that way.</summary>
    private readonly Func<string, IOwnAppTokenProvider>? _ownAppFactory;

    private readonly SynchronizationContext? _context;

    /// <summary>Mutation order for saves, so a slow write cannot land after a newer one.</summary>
    private ulong _saveGeneration;

    /// <summary>Raised on the model's thread after any change the views should redraw for.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether the app has a usable client id and a provider for it. The first-party methods work without it.</summary>
    public bool IsConfigured => Settings.IsConfigured && _ownApp is not null;

    public AppModel(
        ITokenProvider tokens,
        IHttpClient http,
        AppStateStore store,
        IExpiryNotifier notifier,
        INetworkMonitor network,
        AppSettings settings,
        IFirstPartyProviders firstParty,
        IOwnAppTokenProvider? ownApp = null,
        Func<string, IOwnAppTokenProvider>? ownAppFactory = null,
        IHotKeyCenter? hotKeys = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(firstParty);

        Tokens = tokens;
        Http = http;
        _store = store;
        Notifier = notifier;
        _network = network;
        Settings = settings;
        _firstParty = firstParty;
        _ownApp = ownApp;
        _ownAppFactory = ownAppFactory;
        _context = SynchronizationContext.Current;
        HotKeys = hotKeys ?? new NoopHotKeyCenter();
        Coordinator = MakeCoordinator(tokens);
        ApprovalProviders = MakeApprovalProviders(http, tokens);
        Discovery = new TenantDiscovery(http, tokens);
        _network.Changed += OnNetworkChanged;
    }

    private ActivationCoordinator MakeCoordinator(ITokenProvider tokens) => new(
        [new EntraDirectoryProvider(Http, tokens), new AzureResourceProvider(Http, tokens), new GroupProvider(Http, tokens)],
        tokens);

    /// <summary>
    /// Saves a new client id. The own-app token cache is per client, so every own-app account is
    /// signed out and cleared; first-party accounts keep their own caches and stay.
    /// </summary>
    /// <exception cref="PimException">The id is not a GUID, or this build cannot sign in with an own app.</exception>
    public void ApplyClientId(string raw)
    {
        var id = (raw ?? string.Empty).Trim();
        if (!AppSettings.IsValidClientId(id))
        {
            throw new PimException(PimErrorKind.Unexpected, "Enter the application (client) ID as a GUID");
        }

        // Construct the new provider before mutating anything, so a throwing factory leaves the
        // current client id, tokens and session state untouched.
        var factory = _ownAppFactory ?? throw new PimException(PimErrorKind.Unexpected, "Sign-in is unavailable in this build");
        var replacement = factory(id);

        var ownApp = State.Identities.Where(i => i.SignInMethod.UsesMsal).ToList();
        // The old client's cache is unusable under the new client id; drop it silently. A browser
        // sign-out here would only interrupt the user.
        var previous = _ownApp;
        if (previous is not null && ownApp.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await previous.RemoveCachedAccountsAsync(ownApp, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best effort: the accounts are gone from state either way.
                }
            });
        }

        ConfigGeneration += 1;
        foreach (var identity in ownApp)
        {
            ForgetIdentity(identity.Id);
        }

        LastRefresh = DateTimeOffset.MinValue;
        Selection.Clear();
        Busy.Clear();
        InFlight.Clear();
        DecisionInFlight.Clear();
        ApprovalErrors.Clear();
        PendingExtend = null;
        SelectMode = false;
        Persist();
        Settings.ClientId = id;
        _ownApp = replacement;
        var composite = new CompositeTokenProvider(replacement, _firstParty);
        Tokens = composite;
        Coordinator = MakeCoordinator(composite);
        ApprovalProviders = MakeApprovalProviders(Http, composite);
        Discovery = new TenantDiscovery(Http, composite);
        Notice = null;
        StartupError = null;
        OnPropertyChanged(nameof(IsConfigured));
        _ = RescheduleNotificationsAsync();
        Touch();
    }

    /// <summary>Drops one identity and everything derived from it, in state and in memory.</summary>
    internal void ForgetIdentity(string identityId)
    {
        State.RemoveIdentity(identityId);
        foreach (var key in Roles.Keys.Where(k => k.IdentityId == identityId).ToList())
        {
            Roles.Remove(key);
        }

        RemoveWhere(Active, k => k.IdentityId == identityId);
        RemoveWhere(Progress, k => k.IdentityId == identityId);
        RemoveWhere(TenantErrors, k => k.IdentityId == identityId);
        DropApprovals(k => k.IdentityId == identityId);
        DropPolicies(k => k.IdentityId == identityId);
    }

    // MARK: Derived

    public IReadOnlyList<Identity> Identities => State.Identities;

    /// <summary>Accounts a client-id change would sign out; the first-party ones are unaffected.</summary>
    public int OwnAppIdentityCount => State.Identities.Count(i => i.SignInMethod.UsesMsal);

    /// <summary>False when the machine has no usable network path; reads and requests are held back.</summary>
    public bool IsOnline => _network.IsOnline;

    public IReadOnlyList<TenantContext> TenantsFor(string identityId) => State.TenantsFor(identityId);

    public IReadOnlyList<EligibleRole> RolesFor(TenantKey tenantKey) =>
        Roles.TryGetValue(tenantKey, out var list) ? list : [];

    /// <summary>Why the Groups pivot is empty by construction for this tenant, or null when groups are read normally.</summary>
    public string? GroupsUnavailableReason(TenantKey key)
    {
        if (Identity(key.IdentityId) is not { } identity)
        {
            return null;
        }

        if (!identity.SignInMethod.IsPreauthorisedForEntraActivation)
        {
            return $"The {identity.SignInMethod.DisplayName} supports Azure resource roles only; PIM for Groups needs your own or a custom app registration.";
        }

        return Tenant(key)?.GroupsUnavailableReason;
    }

    public EligibleRole? Role(RoleKey key) =>
        Roles.TryGetValue(key.TenantKey, out var list) ? list.FirstOrDefault(r => r.Key == key) : null;

    public ActiveAssignment? Assignment(RoleKey key) => Active.GetValueOrDefault(key);

    public RoleMemory? Remembered(RoleKey key) => State.MemoryFor(key);

    public Identity? Identity(string id) => State.Identities.FirstOrDefault(i => i.Id == id);

    public TenantContext? Tenant(TenantKey key) => State.Tenants.FirstOrDefault(t => t.Key == key);

    public IReadOnlyList<ManualRole> ManualRoles(TenantKey key) =>
        [.. State.ManualRoles.Where(r => r.TenantKey == key)];

    /// <summary>
    /// Only own-app accounts can be consented to: the first-party client ids are Microsoft's,
    /// already consented tenant-wide, and are not ours to request consent for.
    /// </summary>
    public Uri? AdminConsentUrl(string identityId, string tenantId)
    {
        if (!IsConfigured || Identity(identityId)?.SignInMethod.UsesMsal != true)
        {
            return null;
        }

        var scopes = string.Join(' ', Scopes.GraphAll.Concat(Scopes.GroupAll));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = Settings.ClientId.Trim(),
            ["scope"] = scopes,
            ["redirect_uri"] = "https://login.microsoftonline.com/common/oauth2/nativeclient",
        };
        var text = "https://login.microsoftonline.com/" + Uri.EscapeDataString(tenantId) + "/v2.0/adminconsent?"
            + string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return new Uri(text);
    }

    // MARK: Lifecycle

    public async Task BootstrapAsync()
    {
        if (Bootstrapped)
        {
            return;
        }

        Bootstrapped = true;
        try
        {
            State = await Task.Run(_store.Load);
        }
        catch (JsonException e)
        {
            // Never write over a file we could not read; move it aside first.
            try
            {
                _store.QuarantineCorruptFile();
            }
            catch (IOException)
            {
                // Nothing more to do: the load already failed.
            }

            State = new AppState();
            Notice = "Saved state could not be read; it was moved to state.json.bak";
            LogError($"Saved state could not be read: {e.Message}");
        }

        // Reconcile with the token caches: every identity, own-app or first-party, is real only
        // while its provider still knows the account. A failed read must not be mistaken for "no
        // account", which would sign real accounts out on a transient error: fail open and keep them.
        foreach (var identity in State.Identities)
        {
            _ = _firstParty.Provider(identity.SignInMethod);
        }

        IReadOnlyList<Identity>? known = null;
        try
        {
            known = await Tokens.IdentitiesAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Notice = "Could not read saved sign-ins; your accounts were kept.";
            LogError($"Could not read saved sign-ins: {e.Message}");
        }

        if (known is not null)
        {
            var ids = known.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            var dropped = new List<string>();
            foreach (var identity in State.Identities.ToList())
            {
                // Own-app accounts are only reconcilable when the own-app provider exists.
                if (identity.SignInMethod.UsesMsal && _ownApp is null)
                {
                    continue;
                }

                if (!ids.Contains(identity.Id))
                {
                    State.RemoveIdentity(identity.Id);
                    dropped.Add(identity.Upn);
                }
            }

            if (dropped.Count > 0)
            {
                Notice = $"{string.Join(", ", dropped)} was signed out because its saved sign-in is gone; add the account again.";
                LogError($"Signed out (no saved sign-in): {string.Join(", ", dropped)}");
            }
        }

        Persist();
        if (IsOnline)
        {
            await RefreshAllAsync();
        }

        StartTimers();
        ApplyHotKey();
        // Fire and forget: an update check must never hold up the first flyout open.
        _ = CheckForUpdatesAsync();
        Touch();
    }

    // MARK: Timers

    private void StartTimers()
    {
        _timers?.Cancel();
        var cts = new CancellationTokenSource();
        _timers = cts;
        _ = RunClockAsync(cts.Token);
        _ = RunRefreshTimerAsync(cts.Token);
    }

    private async Task RunClockAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                Clock = DateTimeOffset.UtcNow;
                Touch();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunRefreshTimerAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // Pending approvals live in Active too, and they need polling to flip to active.
                if (!IsOnline || Active.Count == 0)
                {
                    continue;
                }

                await RefreshAllAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Post(() =>
    {
        OnPropertyChanged(nameof(IsOnline));
        Touch();
        if (IsOnline && Bootstrapped && Identities.Count > 0)
        {
            _ = RefreshAllAsync();
        }
    });

    public void Dispose()
    {
        _timers?.Cancel();
        _timers?.Dispose();
        _timers = null;
        _network.Changed -= OnNetworkChanged;
    }

    // MARK: Housekeeping

    /// <summary>Policies belong to the role they were fetched for; drop them when that role can no longer be trusted.</summary>
    internal void DropPolicies(Func<RoleKey, bool> matches)
    {
        foreach (var key in PolicyCache.Keys.Where(matches).ToList())
        {
            PolicyCache.Remove(key);
        }
    }

    internal void Persist()
    {
        _saveGeneration += 1;
        var snapshot = State.Clone();
        var generation = _saveGeneration;
        _ = Task.Run(() =>
        {
            try
            {
                _store.Save(snapshot, generation);
            }
            catch (Exception)
            {
                // A failed save is not worth interrupting the user for; the next mutation retries.
            }
        });
        Touch();
    }

    /// <summary>
    /// Records one user-visible failure. Called wherever a tenant error or a failure notice is set;
    /// purely informational notices are not errors and are not logged.
    /// </summary>
    public void LogError(string message)
    {
        var trimmed = (message ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        ErrorLog.Append(Text.Prefix(trimmed, 300));
    }

    /// <summary>Tells the views something changed. Always raised on the model's thread.</summary>
    internal void Touch() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Runs <paramref name="action"/> on the model's thread.</summary>
    internal void Post(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }

    private static void RemoveWhere<TKey, TValue>(Dictionary<TKey, TValue> dictionary, Func<TKey, bool> matches)
        where TKey : notnull
    {
        foreach (var key in dictionary.Keys.Where(matches).ToList())
        {
            dictionary.Remove(key);
        }
    }

    public static string Describe(Exception error) =>
        error is PimException pim ? pim.UserMessage : error.Message;
}
