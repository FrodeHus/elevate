using System.Text.Json;
using Elevate.Cli.Infrastructure;
using Elevate.Core.Auth;
using Elevate.Core.Catalogue;
using Elevate.Core.Coordination;
using Elevate.Core.Discovery;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Providers;
using Elevate.Core.Storage;
using Elevate.Core.Support;

namespace Elevate.Cli.Session;

/// <summary>
/// The CLI's working set for one invocation: the persisted state, the roles and assignments read
/// this run, and the Core services that read and change them. A headless port of the desktop
/// apps' <c>AppModel</c>: the same tenant latches (consent refused, no Azure, groups off, Entra
/// view-only) and the same activation bookkeeping, without timers, notifications or a UI thread.
/// </summary>
public sealed partial class ElevateSession
{
    private readonly Lock _sync = new();
    private readonly AppStateStore _store;
    private readonly IHttpClient _http;

    public ElevateSession(
        AppStateStore store,
        CliSettings settings,
        ITokenProvider tokens,
        IHttpClient http,
        IEnumerable<IPimProvider>? providers = null,
        IEnumerable<IApprovalProvider>? approvalProviders = null,
        IAccessPackageProvider? accessPackages = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(http);
        _store = store;
        _http = http;
        Settings = settings;
        Tokens = tokens;
        Coordinator = new ActivationCoordinator(
            providers ?? [new EntraDirectoryProvider(http, tokens), new AzureResourceProvider(http, tokens), new GroupProvider(http, tokens)],
            tokens);
        ApprovalProviders = (approvalProviders
            ?? [new EntraApprovalProvider(http, tokens), new GroupApprovalProvider(http, tokens), new AzureApprovalProvider(http, tokens)])
            .DistinctBy(p => p.Kind).ToDictionary(p => p.Kind);
        Discovery = new TenantDiscovery(http, tokens);
        Packages = accessPackages ?? new AccessPackageProvider(http, tokens);
        LoadInlineProfiles();
    }

    public CliSettings Settings { get; }

    public ITokenProvider Tokens { get; }

    public ActivationCoordinator Coordinator { get; }

    public Dictionary<RoleScopeKind, IApprovalProvider> ApprovalProviders { get; }

    public IAccessPackageProvider Packages { get; }

    public TenantDiscovery Discovery { get; }

    public AppState State { get; private set; } = new();

    public Dictionary<TenantKey, List<EligibleRole>> Roles { get; } = [];

    public Dictionary<RoleKey, ActiveAssignment> Active { get; } = [];

    public Dictionary<TenantKey, string> TenantErrors { get; } = [];

    public Dictionary<TenantKey, Dictionary<RoleScopeKind, List<ApprovalRequest>>> Approvals { get; } = [];

    /// <summary>Tenants read this run, so a profile plan can tell "not eligible" from "not loaded".</summary>
    public HashSet<TenantKey> LoadedTenants { get; } = [];

    public Dictionary<RoleKey, RolePolicy> PolicyCache { get; } = [];

    public ErrorLog ErrorLog { get; } = new();

    /// <summary>A message about the state file worth showing once, set by <see cref="Load"/>.</summary>
    public string? LoadNotice { get; private set; }

    /// <summary>Reads <c>state.json</c>; an unreadable file is moved aside so it is never overwritten.</summary>
    public void Load()
    {
        try
        {
            State = _store.Load();
        }
        catch (JsonException e)
        {
            string? backup = null;
            try
            {
                backup = _store.QuarantineCorruptFile();
            }
            catch (IOException)
            {
                // The load already failed; nothing more to do.
            }

            State = new AppState();
            LoadNotice = $"Saved state could not be read ({e.Message}); it was moved to {backup ?? "state.json.bak"} and the CLI starts empty.";
            ErrorLog.Append(LoadNotice);
        }
    }

    public void Persist()
    {
        lock (_sync)
        {
            _store.Save(State);
        }
    }

    public void LogError(string message) => ErrorLog.Append(message);

    public static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is PimException pim ? pim.UserMessage : error.Message;
    }

    // MARK: Lookups

    public IReadOnlyList<Identity> Identities => State.Identities;

    public Identity? Identity(string id) => State.Identities.FirstOrDefault(i => i.Id == id);

    public TenantContext? Tenant(TenantKey key) => State.Tenants.FirstOrDefault(t => t.Key == key);

    public IReadOnlyList<TenantContext> Tenants => State.Tenants;

    public EligibleRole? Role(RoleKey key) =>
        Roles.TryGetValue(key.TenantKey, out var list) ? list.FirstOrDefault(r => r.Key == key) : null;

    public IEnumerable<EligibleRole> AllRoles => Roles.Values.SelectMany(list => list);

    public RoleMemory? Remembered(RoleKey key) => State.MemoryFor(key);

    public string TenantName(TenantKey key) => Tenant(key)?.DisplayName ?? key.TenantId;

    public string AccountName(string identityId) => Identity(identityId)?.Upn ?? identityId;

    /// <summary>The display name for a role key: the loaded role, else a manual entry, else the key's raw scope.</summary>
    public string RoleName(RoleKey key)
    {
        if (Role(key) is { } role)
        {
            return role.DisplayName;
        }

        if (State.ManualRoles.FirstOrDefault(m => m.TenantKey == key.TenantKey && m.Scope == key.Scope) is { } manual)
        {
            return manual.DisplayName;
        }

        if (key.Scope is EntraDirectoryScope entra
            && RoleCatalogue.EntraBuiltInRoles().FirstOrDefault(r => string.Equals(r.TemplateId, entra.RoleDefinitionId, StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            return known.DisplayName;
        }

        return key.Scope switch
        {
            AzureResourceScope azure => azure.RoleDefinitionId + " @ " + azure.Scope,
            GroupScope group => group.GroupId + " (" + (group.AccessId == GroupAccess.Owner ? "owner" : "member") + ")",
            EntraDirectoryScope e => e.RoleDefinitionId,
            _ => key.Scope.ToString() ?? "role",
        };
    }

    /// <summary>Why Entra roles in this tenant are view-only, or null when they can be activated.</summary>
    public string? EntraViewOnlyReason(TenantKey key)
    {
        if (Identity(key.IdentityId) is not { } identity)
        {
            return null;
        }

        if (Tenant(key)?.EntraActivation is { } support)
        {
            return support.Reason;
        }

        return identity.SignInMethod.EntraViewOnlyReason;
    }

    public bool CanActivate(RoleKey key) =>
        key.Scope.Kind != RoleScopeKind.EntraDirectory || EntraViewOnlyReason(key.TenantKey) is null;

    public IEnumerable<ApprovalRequest> AllApprovals => Approvals.Values.SelectMany(k => k.Values).SelectMany(l => l);

    internal void Mutate(Action action)
    {
        lock (_sync)
        {
            action();
        }
    }
}
