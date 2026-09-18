using Elevate.App.Services;
using Elevate.Core.Auth;
using Elevate.Core.Catalogue;
using Elevate.Core.Discovery;
using Elevate.Core.Models;

namespace Elevate.App.ViewModels;

/// <summary>Sign-in methods, accounts and tenants. Port of <c>AppModel+Accounts.swift</c>.</summary>
public sealed partial class AppModel
{
    // MARK: Sign-in methods

    /// <summary>
    /// Fixed sign-in methods offered by "Add account" (a custom client id is typed there). The
    /// own-app row is listed even when unconfigured; the view disables it and explains why. A
    /// method the organization does not permit is not listed at all.
    /// </summary>
    public IReadOnlyList<SignInMethod> AvailableMethods => [.. SignInMethod.BuiltIn.Where(IsMethodAllowed)];

    /// <summary>The custom client id used last time, for prefilling the add-account dialog.</summary>
    public string RememberedCustomClientId => Settings.CustomClientId;

    /// <summary>The last client id typed into "Use a different registration", for prefilling the dialogs.</summary>
    public string RememberedPinnedClientId => Settings.PinnedClientId;

    /// <summary>
    /// Whether an account can use an Entra app registration of its own: the organization allows
    /// the method and has not fixed the client id, and this build has a way to sign in with one.
    /// </summary>
    public bool CanPin => IsMethodAllowed(SignInMethod.OwnApp) && !Settings.IsClientIdManaged && _pinned is not null;

    /// <summary>
    /// Whether a method can be used right now. A custom method needs a well-formed client id; a
    /// pinned one needs pinning to be possible and its id to be a GUID.
    /// </summary>
    public bool IsAvailable(SignInMethod method) => IsMethodAllowed(method) && method.Kind switch
    {
        SignInMethodKind.OwnApp => method.PinnedClientId is { } pinned
            ? CanPin && AppSettings.IsValidClientId(pinned)
            : IsConfigured,
        SignInMethodKind.Custom => AppSettings.IsValidClientId(method.CustomClientId),
        _ => method.ClientId is not null,
    };

    /// <summary>
    /// Whether "Change app registration…" (or, for an account not on an Entra app registration
    /// yet, "Upgrade to Entra app registration…") should be offered: some target is actually
    /// reachable — the Settings registration, when it differs from the account's current method,
    /// or, unless the client id is managed, one of its own.
    /// </summary>
    public bool CanChangeRegistration(Identity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var toSettings = IsAvailable(SignInMethod.OwnApp) && identity.SignInMethod != SignInMethod.OwnApp;
        return toSettings || CanPin;
    }

    /// <summary>How Add account and the Change app registration dialog name the Settings registration.</summary>
    public string SettingsRegistrationLabel
    {
        get
        {
            if (Settings.IsClientIdManaged)
            {
                return "managed by your organization";
            }

            if (UsesSharedApp)
            {
                return "shared Elevate app";
            }

            var id = Settings.ClientId;
            return AppSettings.IsValidClientId(id) ? $"{id.Trim()[..8]}…" : "not configured";
        }
    }

    /// <summary>Whether a typed client id is the one in Settings, so the dialogs can say so.</summary>
    public bool MatchesSettingsClientId(string raw) =>
        AppSettings.IsValidClientId(Settings.ClientId)
        && SignInMethod.NormalizeClientId(Settings.ClientId) == SignInMethod.NormalizeClientId(raw ?? string.Empty);

    /// <summary>Whether any request for the account is running, so its registration must not change now.</summary>
    public bool IsAccountBusy(string identityId) =>
        InFlight.Any(k => k.IdentityId == identityId)
        || Busy.Any(k => k.IdentityId == identityId)
        || SignInInFlight.Contains(identityId);

    /// <summary>
    /// Names the MSAL cache slot a method's tokens live in: its client id, normalised. Two methods
    /// that return the same key share one account entry and one refresh token, so signing one out
    /// signs the other out too. Null when the method has no usable client id at all (the Settings
    /// form before a client id is configured).
    /// </summary>
    private string? TokenStoreKey(SignInMethod method)
    {
        var id = method.IsOwnApp && !method.IsPinned ? Settings.ClientId : method.ClientId;
        return AppSettings.IsValidClientId(id) ? SignInMethod.NormalizeClientId(id!) : null;
    }

    /// <summary>
    /// Whether a sign-in that returned <paramref name="signedIn"/> (instead of the account being
    /// waited for) is a listed account's own session: same user, same cache slot as that account
    /// uses. Discarding it would then delete the listed account's fresh token.
    /// </summary>
    private bool IsListedSession(Identity signedIn)
    {
        if (State.Identities.FirstOrDefault(i => i.Id == signedIn.Id) is not { } listed)
        {
            return false;
        }

        var key = TokenStoreKey(signedIn.SignInMethod);
        return key is not null && key == TokenStoreKey(listed.SignInMethod);
    }

    /// <summary>Signs a stray session out, ignoring failures: leaving it cached is harmless.</summary>
    private async Task DiscardSignInAsync(Identity identity, CancellationToken ct)
    {
        try
        {
            await Tokens.SignOutAsync(identity, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The stray sign-in stays in the cache; harmless.
        }
    }

    /// <summary>Shown when a pinned account is asked to sign in while the organization fixes the client id.</summary>
    public const string ManagedPinnedNotice =
        "Your organization manages the app registration. Change this account to it with Change app registration, or sign it out.";

    // MARK: Accounts

    /// <summary>
    /// Signs in with <paramref name="method"/> and adds the resulting account, its home tenant and
    /// its roles. Sets <see cref="Notice"/> and leaves the state untouched when the sign-in fails.
    /// Returns whether an account was actually added.
    /// </summary>
    public async Task<bool> AddAccountAsync(SignInMethod method, CancellationToken ct = default)
    {
        if (!IsMethodAllowed(method))
        {
            Notice = DisallowedMethodNotice;
            LogError($"Add account ({method.DisplayName}): {DisallowedMethodNotice}");
            return false;
        }

        if (!IsAvailable(method))
        {
            Notice = method.Kind switch
            {
                SignInMethodKind.OwnApp when method.IsPinned =>
                    CanPin ? "Enter the registration's application (client) ID as a GUID" : "Your own app registration is unavailable here",
                SignInMethodKind.OwnApp => "Complete initial setup first",
                SignInMethodKind.Custom => "Enter the other app's application (client) ID as a GUID",
                _ => "That sign-in method is unavailable",
            };
            LogError($"Add account ({method.DisplayName}): {Notice}");
            return false;
        }

        if (method.IsCustom)
        {
            Settings.CustomClientId = method.CustomClientId!;
        }

        if (method.PinnedClientId is { } rememberedPin)
        {
            Settings.PinnedClientId = rememberedPin;
        }

        try
        {
            var identity = await Tokens.SignInAsync(method, ct);
            // The same account under a different method would fight over the same rows and tenants.
            if (State.Identities.FirstOrDefault(i => i.Id == identity.Id) is { } existing && existing.SignInMethod != method)
            {
                Notice = $"This account is already added with {existing.SignInMethod.DetailedName}";
                LogError($"Add account: already added with {existing.SignInMethod.DisplayName}");
                // Discard the sign-in we just made, but only when it does not share a cache slot
                // with the account that is already there: MSAL keys accounts by client id, so a
                // pinned id equal to the Settings one — or a custom method over it — is the same
                // entry, and signing out would delete the existing account's token.
                var added = TokenStoreKey(method);
                if (added is null || added != TokenStoreKey(existing.SignInMethod))
                {
                    await DiscardSignInAsync(identity, ct);
                }

                return false;
            }

            if (!State.Identities.Any(i => i.Id == identity.Id))
            {
                State.Identities.Add(identity);
            }

            var homeKey = new TenantKey(identity.Id, identity.HomeTenantId);
            if (Tenant(homeKey) is null)
            {
                string name;
                try
                {
                    name = await Discovery.TenantDisplayNameAsync(identity, identity.HomeTenantId, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    name = identity.HomeTenantId;
                }

                State.UpsertTenant(new TenantContext(identity.Id, identity.HomeTenantId, name, TenantSource.Home));
            }

            Persist();
            await RefreshAsync(homeKey);
            await TrackPinnedTenantsAsync(identity.Id, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            var message = Describe(e);
            Notice = message;
            LogError($"Add account ({method.DisplayName}): {message}");
            return false;
        }
    }

    /// <summary>Whether <paramref name="identityId"/> is kept in the list without a usable saved sign-in.</summary>
    public bool NeedsSignIn(string identityId) => SignInNeeded.Contains(identityId);

    /// <summary>
    /// Signs an account marked <see cref="SignInNeeded"/> in again with the method it was added
    /// with, keeping its tenants and configured roles. Sets <see cref="Notice"/> and keeps the flag
    /// when the sign-in fails or the browser comes back with a different account. Returns whether
    /// the account is usable again.
    /// </summary>
    public async Task<bool> RetrySignInAsync(Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var method = identity.SignInMethod;
        if (!IsMethodAllowed(method))
        {
            Notice = DisallowedMethodNotice;
            LogError($"Sign in again ({method.DisplayName}): {DisallowedMethodNotice}");
            return false;
        }

        if (method.IsPinned && Settings.IsClientIdManaged)
        {
            Notice = ManagedPinnedNotice;
            LogError($"Sign in again ({method.DisplayName}): the app registration is managed by your organization");
            return false;
        }

        if (!IsAvailable(method))
        {
            Notice = method.Kind == SignInMethodKind.OwnApp && !method.IsPinned
                ? "Complete initial setup first"
                : "That sign-in method is unavailable";
            LogError($"Sign in again ({method.DisplayName}): {Notice}");
            return false;
        }

        if (!SignInInFlight.Add(identity.Id))
        {
            return false;
        }

        Touch();
        try
        {
            var signedIn = await Tokens.SignInAsync(method, ct);
            if (signedIn.Id != identity.Id)
            {
                // A different account came back. Its token is keyed by its own id, so discarding it
                // cannot touch the one we were waiting for — but it may be another listed account
                // that has just signed in again (after a Settings client id change many need to),
                // and its fresh session must stay.
                if (!IsListedSession(signedIn))
                {
                    await DiscardSignInAsync(signedIn, ct);
                }

                Notice = $"Signed in as {signedIn.Upn}, but {identity.Upn} was expected. Sign out {identity.Upn} if you no longer need it.";
                LogError($"Sign in again: got {signedIn.Upn}, expected {identity.Upn}");
                return false;
            }

            SignInNeeded.Remove(identity.Id);
            Notice = null;
            await RefreshTenantsOfAsync(identity.Id);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            var message = Describe(e);
            Notice = message;
            LogError($"Sign in again ({method.DisplayName}): {message}");
            return false;
        }
        finally
        {
            SignInInFlight.Remove(identity.Id);
            Touch();
        }
    }

    /// <summary>
    /// Moves an account to an Entra app registration — a client id of its own or the Settings one
    /// — keeping its tenants, configured roles, profiles and role memory. The current method can be
    /// anything (Azure CLI, Azure PowerShell, another app, or already an Entra app registration);
    /// only the target is restricted. The change is saved only after the same user has signed in
    /// with the new registration; a cancelled sign-in or a different account changes nothing. Sets
    /// <see cref="Notice"/> on failure. Port of the macOS <c>changeSignInRegistration</c>.
    /// </summary>
    public async Task<bool> ChangeSignInRegistrationAsync(Identity identity, SignInMethod method, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var current = identity.SignInMethod;
        if (!method.IsOwnApp)
        {
            Notice = "Only an Entra app registration can be chosen here";
            return false;
        }

        if (method == current)
        {
            return true;
        }

        // Under a managed client id an account may still move *to* the managed registration.
        if (method.IsPinned && Settings.IsClientIdManaged)
        {
            Notice = "The app registration is managed by your organization";
            LogError($"Change registration: {Notice}");
            return false;
        }

        if (!IsAvailable(method))
        {
            Notice = method.IsPinned ? "Enter the application (client) ID as a GUID" : "Configure a client ID in Settings first";
            LogError($"Change registration: {Notice}");
            return false;
        }

        if (IsAccountBusy(identity.Id))
        {
            Notice = "Wait for this account's requests to finish";
            return false;
        }

        if (!SignInInFlight.Add(identity.Id))
        {
            return false;
        }

        Touch();
        // The interactive sign-in below can take minutes; capture what could go stale while the
        // window is up, and re-check it once the user comes back before committing anything.
        var generation = ConfigGeneration;
        var oldStoreKey = TokenStoreKey(current);
        // ApplyClientId may replace Tokens while the sign-in is up; a session made through this
        // provider must be discarded through it too, or it is orphaned in the old client's cache.
        var provider = Tokens;
        if (method.PinnedClientId is { } pinnedClientId)
        {
            Settings.PinnedClientId = pinnedClientId;
        }

        try
        {
            var signedIn = await provider.SignInAsync(method, ct);
            if (signedIn.Id != identity.Id)
            {
                // Keep a session that belongs to another account already in the list.
                if (!IsListedSession(signedIn))
                {
                    await DiscardThroughAsync(provider, signedIn, ct);
                }

                Notice = $"Signed in as {signedIn.Upn}, but {identity.Upn} was expected. Nothing was changed.";
                LogError($"Change registration: got {signedIn.Upn}, expected {identity.Upn}");
                return false;
            }

            var newStoreKey = TokenStoreKey(method);
            // Re-check everything the guards above already checked: the client id, an in-flight
            // request or the account itself may have changed while the sign-in was open.
            var index = State.Identities.FindIndex(i => i.Id == identity.Id);
            if (ConfigGeneration != generation || IsAccountBusyExcludingThisSignIn(identity.Id) || !IsAvailable(method) || index < 0)
            {
                if (newStoreKey is null || newStoreKey != oldStoreKey)
                {
                    await DiscardThroughAsync(provider, signedIn, ct);
                }

                Notice = "Something changed while you were signing in. Nothing was changed; try again.";
                LogError($"Change registration: state changed while {identity.Upn} was signing in");
                return false;
            }

            var old = State.Identities[index];
            State.Identities[index] = old with { SignInMethod = method };
            // Upgrading from a limited method: the flags it left behind (view-only Entra, blocked
            // groups/Azure reads, a discovery error) no longer apply, and its cached policies were
            // learned under a method that could not activate what they cover.
            if (!current.IsOwnApp)
            {
                foreach (var key in TenantsFor(identity.Id).Select(t => t.Key).ToList())
                {
                    ResetDiscoveryFlags(key);
                }

                ClearTokenHint(identity.Id);
            }

            Persist();
            if (newStoreKey is null || newStoreKey != oldStoreKey)
            {
                await DiscardSignInAsync(old, ct);
            }

            DropRuntime(identity.Id);
            SignInNeeded.Remove(identity.Id);
            Notice = null;
            await RefreshTenantsOfAsync(identity.Id);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            var message = Describe(e);
            Notice = message;
            LogError($"Change registration ({method.DisplayName}): {message}");
            return false;
        }
        finally
        {
            SignInInFlight.Remove(identity.Id);
            Touch();
        }
    }

    /// <summary>
    /// <see cref="IsAccountBusy"/> without this call's own sign-in flag, which is set for the whole
    /// duration of the change and would otherwise fail its own re-check.
    /// </summary>
    private bool IsAccountBusyExcludingThisSignIn(string identityId) =>
        InFlight.Any(k => k.IdentityId == identityId) || Busy.Any(k => k.IdentityId == identityId);

    /// <summary>Signs a stray session out through the provider that made it, ignoring failures.</summary>
    private static async Task DiscardThroughAsync(ITokenProvider provider, Identity identity, CancellationToken ct)
    {
        try
        {
            await provider.SignOutAsync(identity, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The stray sign-in stays in the cache; harmless.
        }
    }

    /// <summary>Reads every tenant of an account again, clearing their errors first.</summary>
    private async Task RefreshTenantsOfAsync(string identityId)
    {
        var keys = TenantsFor(identityId).Select(t => t.Key).ToList();
        foreach (var key in keys)
        {
            TenantErrors.Remove(key);
        }

        var generation = ConfigGeneration;
        await Task.WhenAll(keys.Select(async key =>
        {
            if (ConfigGeneration != generation)
            {
                return;
            }

            await RefreshAsync(key);
        }));
    }

    /// <summary>
    /// Clears everything a past discovery failure or a limited sign-in method left on a tenant: its
    /// discovery mode, last error and per-surface unavailable reasons, and drops its cached
    /// policies (learned under whatever was blocking discovery). Does not persist or refresh.
    /// </summary>
    private void ResetDiscoveryFlags(TenantKey key)
    {
        if (Tenant(key) is not { } t)
        {
            return;
        }

        State.UpsertTenant(t with
        {
            DiscoveryMode = DiscoveryMode.Automatic,
            LastDiscoveryError = null,
            AzureUnavailableReason = null,
            GroupsUnavailableReason = null,
            EntraActivation = null,
            AccessPackagesAvailable = null,
        });
        AccessPackageErrors.Remove(key);
        DropPolicies(k => k.TenantKey == key);
    }

    public void SignOut(Identity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _ = SignOutAsync(identity);
    }

    private async Task SignOutAsync(Identity identity)
    {
        try
        {
            await Tokens.SignOutAsync(identity, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogError($"Sign out ({identity.Upn}): {Describe(e)}");
        }

        ForgetIdentity(identity.Id);
        Persist();
    }

    // MARK: Tenants

    public async Task AddTenantAsync(string identityId, string domainOrId, CancellationToken ct = default)
    {
        var generation = ConfigGeneration;
        var identity = Identity(identityId) ?? throw new PimException(PimErrorKind.Unexpected, "Unknown identity");
        var tenantId = await Discovery.ResolveTenantIdAsync(domainOrId, ct);
        if (generation != ConfigGeneration)
        {
            return;
        }

        if (!IsTenantAllowed(tenantId))
        {
            throw new InvalidOperationException(DisallowedTenantMessage(domainOrId));
        }

        var key = new TenantKey(identityId, tenantId);
        if (Tenant(key) is not null)
        {
            return;
        }

        string name;
        try
        {
            name = await InteractionRetry.RunAsync(
                Tokens, identity, tenantId, [Scopes.GraphUserRead],
                () => Discovery.TenantDisplayNameAsync(identity, tenantId, ct), ct: ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            name = domainOrId;
        }

        if (generation != ConfigGeneration)
        {
            return;
        }

        State.UpsertTenant(new TenantContext(identityId, tenantId, name, TenantSource.Manual));
        Persist();
        await RefreshAsync(key);
    }

    public async Task<IReadOnlyList<DiscoveredTenant>> DiscoverTenantsAsync(string identityId, CancellationToken ct = default)
    {
        if (Identity(identityId) is not { } identity)
        {
            return [];
        }

        return await InteractionRetry.RunAsync(
            Tokens, identity, identity.HomeTenantId, Scopes.ArmAll,
            () => Discovery.DiscoverTenantsAsync(identity, ct), ct: ct);
    }

    public async Task TrackTenantsAsync(string identityId, IEnumerable<DiscoveredTenant> tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        var generation = ConfigGeneration;
        // A tenant the organization does not permit is skipped rather than tracked and dropped again.
        var list = tenants.Where(t => IsTenantAllowed(t.TenantId)).ToList();
        foreach (var t in list)
        {
            var key = new TenantKey(identityId, t.TenantId);
            if (Tenant(key) is not null)
            {
                continue;
            }

            State.UpsertTenant(new TenantContext(identityId, t.TenantId, t.DisplayName, TenantSource.Discovered));
        }

        Persist();
        var keys = list.Select(t => new TenantKey(identityId, t.TenantId)).ToList();
        await Task.WhenAll(keys.Select(async key =>
        {
            if (ConfigGeneration != generation)
            {
                return;
            }

            await RefreshAsync(key);
        }));
    }

    public void RemoveTenant(TenantKey key)
    {
        if (IsPinnedTenant(key))
        {
            // A pinned tenant would only come back on the next launch; say so rather than
            // removing it and re-adding it behind the user's back.
            Notice = PinnedTenantNotice;
            return;
        }

        ForgetTenant(key);
        Persist();
        _ = ReschedulePackageExpiriesAsync();
    }

    /// <summary>
    /// Drops one tenant and everything derived from it, without saving: the callers that remove
    /// several at once persist the result themselves.
    /// </summary>
    internal void ForgetTenant(TenantKey key)
    {
        DeclinedTenants.Remove(key);
        TenantsAwaitingSignIn.Remove(key);
        State.RemoveTenant(key);
        Roles.Remove(key);
        foreach (var roleKey in Active.Keys.Where(k => k.TenantKey == key).ToList())
        {
            Active.Remove(roleKey);
        }

        DropApprovals(k => k == key);
        DropPolicies(k => k.TenantKey == key);
        AccessPackageErrors.Remove(key);
    }

    public async Task RetryDiscoveryAsync(TenantKey key)
    {
        DeclinedTenants.Remove(key);
        if (Tenant(key) is null)
        {
            return;
        }

        ResetDiscoveryFlags(key);
        Persist();
        await RefreshAsync(key);
    }

    /// <summary>Replaces the manually configured roles of one tenant and re-reads it.</summary>
    public void SetManualRoles(IEnumerable<ManualRole> manual, TenantKey key)
    {
        ArgumentNullException.ThrowIfNull(manual);
        State.ManualRoles.RemoveAll(r => r.TenantKey == key);
        State.ManualRoles.AddRange(manual.Where(r => r.TenantKey == key));
        Persist();
        _ = RefreshAsync(key);
    }
}
