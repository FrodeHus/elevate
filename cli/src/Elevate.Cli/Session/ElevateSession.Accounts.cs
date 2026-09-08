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
        if (method.IsCustom)
        {
            Settings.CustomClientId = method.CustomClientId!;
        }

        var identity = await Tokens.SignInAsync(method, ct).ConfigureAwait(false);
        if (State.Identities.FirstOrDefault(i => i.Id == identity.Id) is { } existing && existing.SignInMethod != method)
        {
            if (method.ClientId is null || !string.Equals(method.ClientId, existing.SignInMethod.ClientId, StringComparison.OrdinalIgnoreCase))
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

            throw new CliException($"{identity.Upn} is already added with the {existing.SignInMethod.DisplayName}. Sign it out first to change the method.", ExitCodes.Usage);
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
            foreach (var key in Roles.Keys.Where(k => k.IdentityId == identityId).ToList())
            {
                Roles.Remove(key);
            }

            foreach (var key in Active.Keys.Where(k => k.IdentityId == identityId).ToList())
            {
                Active.Remove(key);
            }

            foreach (var key in Approvals.Keys.Where(k => k.IdentityId == identityId).ToList())
            {
                Approvals.Remove(key);
            }
        }
    }

    // MARK: Tenants

    public async Task<TenantContext> AddTenantAsync(Identity identity, string domainOrId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var tenantId = await Discovery.ResolveTenantIdAsync(domainOrId, ct).ConfigureAwait(false);
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
                if (Tenant(key) is not null)
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

        Persist();
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
