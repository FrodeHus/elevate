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

    /// <summary>Fixed sign-in methods offered by "Add account" (a custom client id is typed there).</summary>
    public IReadOnlyList<SignInMethod> AvailableMethods => SignInMethod.BuiltIn;

    /// <summary>The custom client id used last time, for prefilling the add-account dialog.</summary>
    public string RememberedCustomClientId => Settings.CustomClientId;

    /// <summary>Whether a method can be used right now. A custom method needs a well-formed client id.</summary>
    public bool IsAvailable(SignInMethod method) => method.Kind switch
    {
        SignInMethodKind.OwnApp => IsConfigured,
        SignInMethodKind.Custom => AppSettings.IsValidClientId(method.CustomClientId),
        _ => method.ClientId is not null,
    };

    // MARK: Accounts

    /// <summary>
    /// Signs in with <paramref name="method"/> and adds the resulting account, its home tenant and
    /// its roles. Sets <see cref="Notice"/> and leaves the state untouched when the sign-in fails.
    /// Returns whether an account was actually added.
    /// </summary>
    public async Task<bool> AddAccountAsync(SignInMethod method, CancellationToken ct = default)
    {
        if (!IsAvailable(method))
        {
            Notice = method.Kind switch
            {
                SignInMethodKind.OwnApp => "Complete initial setup first",
                SignInMethodKind.Custom => "Enter the custom app's application (client) ID as a GUID",
                _ => "That sign-in method is unavailable",
            };
            LogError($"Add account ({method.DisplayName}): {Notice}");
            return false;
        }

        if (method.IsCustom)
        {
            Settings.CustomClientId = method.CustomClientId!;
        }

        try
        {
            var identity = await Tokens.SignInAsync(method, ct);
            // The same account under a different method would fight over the same rows and tenants.
            if (State.Identities.FirstOrDefault(i => i.Id == identity.Id) is { } existing && existing.SignInMethod != method)
            {
                Notice = $"This account is already added with {existing.SignInMethod.DisplayName}";
                LogError($"Add account: already added with {existing.SignInMethod.DisplayName}");
                // Discard the sign-in we just made. Caches are per client id here, so signing the
                // duplicate out cannot touch the existing account's tokens unless both methods share
                // a client id (a custom method over the Settings client id).
                if (method.ClientId is null || !string.Equals(method.ClientId, existing.SignInMethod.ClientId, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await Tokens.SignOutAsync(identity, ct);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // The duplicate stays in the cache; harmless.
                    }
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
        var list = tenants.ToList();
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
        DeclinedTenants.Remove(key);
        State.RemoveTenant(key);
        Roles.Remove(key);
        foreach (var roleKey in Active.Keys.Where(k => k.TenantKey == key).ToList())
        {
            Active.Remove(roleKey);
        }

        DropPolicies(k => k.TenantKey == key);
        Persist();
    }

    public async Task RetryDiscoveryAsync(TenantKey key)
    {
        DeclinedTenants.Remove(key);
        if (Tenant(key) is not { } t)
        {
            return;
        }

        t = t with
        {
            DiscoveryMode = DiscoveryMode.Automatic,
            LastDiscoveryError = null,
            AzureUnavailableReason = null,
            GroupsUnavailableReason = null,
            EntraActivation = null,
        };
        DropPolicies(k => k.TenantKey == key);
        State.UpsertTenant(t);
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
