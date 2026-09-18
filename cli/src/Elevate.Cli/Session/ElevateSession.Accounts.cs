using Elevate.Cli.Infrastructure;
using Elevate.Core.Auth;
using Elevate.Core.Catalogue;
using Elevate.Core.Discovery;
using Elevate.Core.Models;

namespace Elevate.Cli.Session;

/// <summary>Accounts and tenants. Port of <c>AppModel.Accounts</c>.</summary>
public sealed partial class ElevateSession
{
    /// <summary>Signs in and adds the account with its home tenant. Returns the identity, or throws with the reason.</summary>
    public async Task<Identity> AddAccountAsync(SignInMethod method, CancellationToken ct = default)
    {
        if (!IsMethodAllowed(method))
        {
            throw DisallowedMethod(MethodName(method), Settings.Managed);
        }

        // A registration of the account's own is the user's choice to make; the organization can
        // take it away by fixing the client id, and then only that one may be used.
        if (method.IsPinned && Settings.IsClientIdManaged)
        {
            throw new CliException(CliSettings.ManagedClientIdMessage, ExitCodes.Usage);
        }

        if (method.IsCustom)
        {
            Settings.CustomClientId = method.CustomClientId!;
        }

        var identity = await Tokens.SignInAsync(method, ct).ConfigureAwait(false);
        if (State.Identities.FirstOrDefault(i => i.Id == identity.Id) is { } existing && existing.SignInMethod != method)
        {
            // Discard the sign-in just made, unless it shares a cache slot with the account that is
            // already there: MSAL keys accounts by client id, so a pin onto the settings id — or a
            // custom method over it — is the same entry, and signing out would take its token too.
            var added = TokenStoreKey(method);
            if (added is null || added != TokenStoreKey(existing.SignInMethod))
            {
                try
                {
                    await Tokens.SignOutAsync(identity, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // The duplicate stays in the cache; harmless.
                }
            }

            throw new CliException(
                $"{identity.Upn} is already added with the {existing.SignInMethod.DetailedName}. "
                + $"Sign it out first to change the method, or move it with 'elevate accounts set-client-id {identity.Upn} <application id>'.",
                ExitCodes.Usage);
        }

        lock (_sync)
        {
            if (!State.Identities.Any(i => i.Id == identity.Id))
            {
                State.Identities.Add(identity);
            }
        }

        var homeKey = new TenantKey(identity.Id, identity.HomeTenantId);
        if (Tenant(homeKey) is null)
        {
            string name;
            try
            {
                name = await Discovery.TenantDisplayNameAsync(identity, identity.HomeTenantId, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                name = identity.HomeTenantId;
            }

            Mutate(() => State.UpsertTenant(new TenantContext(identity.Id, identity.HomeTenantId, name, TenantSource.Home)));
        }

        Persist();
        await TrackPinnedTenantsAsync(identity, ct).ConfigureAwait(false);
        return identity;
    }

    public async Task SignOutAsync(Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        try
        {
            await Tokens.SignOutAsync(identity, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogError($"Sign out ({identity.Upn}): {Describe(e)}");
        }

        ForgetIdentity(identity.Id);
        Persist();
    }

    /// <summary>Drops one identity and everything derived from it; the token cache is left alone.</summary>
    public void ForgetIdentity(string identityId)
    {
        lock (_sync)
        {
            State.RemoveIdentity(identityId);
            DropRuntime(identityId);
        }
    }

    /// <summary>
    /// Drops what this run read for an account, keeping the account itself, its tenants, configured
    /// roles, profile entries and role memory. Used when its registration changes and everything
    /// read under the old one is stale. Port of the desktop apps' <c>dropRuntime</c>.
    /// </summary>
    internal void DropRuntime(string identityId)
    {
        lock (_sync)
        {
            foreach (var key in Roles.Keys.Where(k => k.IdentityId == identityId).ToList())
            {
                Roles.Remove(key);
                LoadedTenants.Remove(key);
            }

            foreach (var key in Active.Keys.Where(k => k.IdentityId == identityId).ToList())
            {
                Active.Remove(key);
            }

            foreach (var key in Approvals.Keys.Where(k => k.IdentityId == identityId).ToList())
            {
                Approvals.Remove(key);
            }

            foreach (var key in PolicyCache.Keys.Where(k => k.IdentityId == identityId).ToList())
            {
                PolicyCache.Remove(key);
            }
        }
    }

    // MARK: App registrations

    /// <summary>
    /// Names the MSAL cache slot a method's tokens live in: its client id, normalised. Two methods
    /// with the same key share one account entry and one refresh token, so signing one out signs
    /// the other out too. Null when the method has no usable client id at all — the settings form
    /// before a client id is configured.
    /// </summary>
    internal string? TokenStoreKey(SignInMethod method)
    {
        var id = method.IsOwnApp && !method.IsPinned ? Settings.ClientId : method.ClientId;
        return CliSettings.IsValidClientId(id) ? SignInMethod.NormalizeClientId(id!) : null;
    }

    /// <summary>
    /// Moves an account to an Entra app registration — a client id of its own, or the one in
    /// settings — keeping its tenants, configured roles, profiles and role memory. The account's
    /// current method can be anything; only the target is restricted. The change is saved only
    /// after the same user has signed in with the new registration: a cancelled sign-in or a
    /// different account changes nothing. Port of the desktop apps' <c>changeSignInRegistration</c>.
    /// </summary>
    public async Task<Identity> ChangeRegistrationAsync(Identity identity, SignInMethod method, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var current = identity.SignInMethod;
        if (!method.IsOwnApp)
        {
            throw new CliException("Only an Entra app registration can be chosen here.", ExitCodes.Usage);
        }

        if (method == current)
        {
            return identity;
        }

        if (!IsMethodAllowed(method))
        {
            throw DisallowedMethod(MethodName(method), Settings.Managed);
        }

        // Under a managed client id an account may still move *to* the managed registration.
        if (method.IsPinned && Settings.IsClientIdManaged)
        {
            throw new CliException(CliSettings.ManagedClientIdMessage, ExitCodes.Usage);
        }

        if (!method.IsPinned && !Settings.IsConfigured)
        {
            throw new CliException(
                "No client ID is configured. Run 'elevate config set client-id <application id>' first.", ExitCodes.Usage);
        }

        var oldStoreKey = TokenStoreKey(current);
        var signedIn = await Tokens.SignInAsync(method, ct).ConfigureAwait(false);
        if (signedIn.Id != identity.Id)
        {
            // Keep a session that belongs to another account already in the list; its token is
            // keyed by its own id, so discarding it cannot touch the one we were waiting for.
            if (!IsListedSession(signedIn))
            {
                await DiscardSignInAsync(signedIn, ct).ConfigureAwait(false);
            }

            throw new CliException(
                $"Signed in as {signedIn.Upn}, but {identity.Upn} was expected. Nothing was changed.", ExitCodes.Usage);
        }

        var index = State.Identities.FindIndex(i => i.Id == identity.Id);
        if (index < 0)
        {
            throw new CliException($"{identity.Upn} is no longer added. Nothing was changed.", ExitCodes.Usage);
        }

        var old = State.Identities[index];
        var moved = old with { SignInMethod = method };
        Mutate(() => State.Identities[index] = moved);

        // Upgrading from a limited method: the flags it left behind (view-only Entra, blocked
        // groups or Azure reads, a discovery error) no longer apply, and the policies cached under
        // it were learned by a client that could not activate what they cover.
        if (!current.IsOwnApp)
        {
            foreach (var key in Tenants.Where(t => t.IdentityId == identity.Id).Select(t => t.Key).ToList())
            {
                ResetTenant(key);
            }
        }

        Persist();
        var newStoreKey = TokenStoreKey(method);
        if (newStoreKey is null || newStoreKey != oldStoreKey)
        {
            await DiscardSignInAsync(old, ct).ConfigureAwait(false);
        }

        DropRuntime(identity.Id);
        return moved;
    }

    /// <summary>
    /// Whether a sign-in that returned <paramref name="signedIn"/> is a listed account's own
    /// session: same user, same cache slot as that account uses. Discarding it would then delete
    /// the listed account's fresh token.
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

    /// <summary>Signs a stray or superseded session out, ignoring failures: leaving it cached is harmless.</summary>
    private async Task DiscardSignInAsync(Identity identity, CancellationToken ct)
    {
        try
        {
            await Tokens.SignOutAsync(identity, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The session stays in the cache; harmless.
        }
    }

    // MARK: Tenants

    public async Task<TenantContext> AddTenantAsync(Identity identity, string domainOrId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var tenantId = await Discovery.ResolveTenantIdAsync(domainOrId, ct).ConfigureAwait(false);
        if (!IsTenantAllowed(tenantId))
        {
            throw new CliException($"Tenant {domainOrId} is not permitted by your organization", ExitCodes.Usage);
        }

        var key = new TenantKey(identity.Id, tenantId);
        if (Tenant(key) is { } already)
        {
            return already;
        }

        string name;
        try
        {
            name = await InteractionRetry.RunAsync(
                Tokens, identity, tenantId, [Scopes.GraphUserRead],
                () => Discovery.TenantDisplayNameAsync(identity, tenantId, ct), ct: ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            name = domainOrId;
        }

        var tenant = new TenantContext(identity.Id, tenantId, name, TenantSource.Manual);
        Mutate(() => State.UpsertTenant(tenant));
        Persist();
        return tenant;
    }

    public Task<IReadOnlyList<DiscoveredTenant>> DiscoverTenantsAsync(Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return InteractionRetry.RunAsync(
            Tokens, identity, identity.HomeTenantId, Scopes.ArmAll,
            () => Discovery.DiscoverTenantsAsync(identity, ct), ct: ct);
    }

    /// <summary>Adds discovered tenants that are not tracked yet; returns the ones added.</summary>
    public IReadOnlyList<TenantContext> TrackTenants(Identity identity, IEnumerable<DiscoveredTenant> tenants)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenants);
        var added = new List<TenantContext>();
        lock (_sync)
        {
            foreach (var t in tenants)
            {
                var key = new TenantKey(identity.Id, t.TenantId);
                if (!IsTenantAllowed(t.TenantId) || Tenant(key) is not null)
                {
                    continue;
                }

                var tenant = new TenantContext(identity.Id, t.TenantId, t.DisplayName, TenantSource.Discovered);
                State.UpsertTenant(tenant);
                added.Add(tenant);
            }
        }

        Persist();
        return added;
    }

    public void RemoveTenant(TenantKey key)
    {
        if (IsPinnedTenant(key))
        {
            throw new CliException(PinnedTenantMessage, ExitCodes.Usage);
        }

        ForgetTenant(key);
        Persist();
    }

    /// <summary>
    /// Drops one tenant and everything derived from it, without saving: the callers that remove
    /// several at once persist the result themselves.
    /// </summary>
    internal void ForgetTenant(TenantKey key)
    {
        lock (_sync)
        {
            State.RemoveTenant(key);
            Roles.Remove(key);
            foreach (var roleKey in Active.Keys.Where(k => k.TenantKey == key).ToList())
            {
                Active.Remove(roleKey);
            }

            Approvals.Remove(key);
        }
    }

    /// <summary>Clears every latch on a tenant so the next refresh tries discovery again.</summary>
    public void ResetTenant(TenantKey key)
    {
        if (Tenant(key) is not { } t)
        {
            return;
        }

        lock (_sync)
        {
            State.UpsertTenant(t with
            {
                DiscoveryMode = DiscoveryMode.Automatic,
                LastDiscoveryError = null,
                AzureUnavailableReason = null,
                GroupsUnavailableReason = null,
                EntraActivation = null,
                AccessPackagesAvailable = null,
            });
            foreach (var roleKey in PolicyCache.Keys.Where(k => k.TenantKey == key).ToList())
            {
                PolicyCache.Remove(roleKey);
            }
        }

        Persist();
    }

    public void SetManualRoles(TenantKey key, IEnumerable<ManualRole> manual)
    {
        ArgumentNullException.ThrowIfNull(manual);
        lock (_sync)
        {
            State.ManualRoles.RemoveAll(r => r.TenantKey == key);
            State.ManualRoles.AddRange(manual.Where(r => r.TenantKey == key));
        }

        Persist();
    }
}
