using Elevate.Core.Auth;
using Elevate.Core.Catalogue;
using Elevate.Core.Models;
using Elevate.Core.Providers;

namespace Elevate.App.ViewModels;

/// <summary>Reading roles and assignments per tenant. Port of <c>AppModel+Refresh.swift</c>.</summary>
public sealed partial class AppModel
{
    // MARK: Refresh

    /// <summary>Called when the flyout opens. Runs on its own so closing the flyout cannot cancel it.</summary>
    public void PanelOpened()
    {
        // A stale filter must never survive a reopen: the panel always opens showing everything.
        SearchQuery = string.Empty;
        if (!Bootstrapped || !IsOnline || Identities.Count == 0 || DateTimeOffset.UtcNow - LastRefresh <= TimeSpan.FromSeconds(30))
        {
            return;
        }

        _ = RefreshAllAsync();
    }

    public async Task RefreshAllAsync(bool userInitiated = false)
    {
        if (userInitiated)
        {
            DeclinedTenants.Clear();
        }

        LastRefresh = DateTimeOffset.UtcNow;
        var generation = ConfigGeneration;
        var keys = State.Tenants.Select(t => t.Key).ToList();
        await Task.WhenAll(keys.Select(async key =>
        {
            if (ConfigGeneration != generation)
            {
                return;
            }

            await RefreshAsync(key);
        }));
        if (generation == ConfigGeneration)
        {
            PruneSeenApprovals();
        }
    }

    public async Task RefreshAsync(TenantKey key, IReadOnlySet<RoleScopeKind>? requestedKinds = null)
    {
        var generation = ConfigGeneration;
        if (Identity(key.IdentityId) is not { } identity || Tenant(key) is not { } tenant)
        {
            return;
        }

        if (!Busy.Add(key))
        {
            return;
        }

        Touch();
        try
        {
            await RefreshCoreAsync(key, identity, tenant, requestedKinds, generation);
        }
        catch (OperationCanceledException)
        {
            // The tenant was removed while it was being read.
        }
        finally
        {
            Busy.Remove(key);
            Touch();
        }
    }

    private async Task RefreshCoreAsync(TenantKey key, Identity identity, TenantContext tenant, IReadOnlySet<RoleScopeKind>? requestedKinds, int generation)
    {
        // A kinds-restricted refresh re-reads only some providers, so it must not clear errors it cannot re-earn.
        if (requestedKinds is null)
        {
            TenantErrors.Remove(key);
        }

        // A tenant with no Azure at all is not worth a request per refresh; the breaker is cleared by Retry discovery.
        // A first-party sign-in (Azure CLI / PowerShell) has no Graph PIM scopes at all, so its
        // Entra reads fail every time: skip that provider outright and keep the account Azure-only.
        // Every kind this identity can read at all, before any per-refresh restriction.
        var eligibleKinds = tenant.AzureUnavailableReason is null
            ? new List<RoleScopeKind> { RoleScopeKind.EntraDirectory, RoleScopeKind.AzureResource, RoleScopeKind.Group }
            : [RoleScopeKind.EntraDirectory, RoleScopeKind.Group];
        if (!identity.SignInMethod.IsPreauthorisedForEntraActivation)
        {
            eligibleKinds.RemoveAll(k => k is RoleScopeKind.EntraDirectory or RoleScopeKind.Group);
        }

        if (tenant.GroupsUnavailableReason is not null)
        {
            eligibleKinds.Remove(RoleScopeKind.Group);
        }

        // A kinds-restricted refresh reads only these providers.
        var kinds = eligibleKinds.ToList();
        if (requestedKinds is not null)
        {
            kinds.RemoveAll(k => !requestedKinds.Contains(k));
        }

        var providers = kinds.Select(Coordinator.Provider).OfType<IPimProvider>().ToList();
        // Start from what we already know so a transient failure never blanks a provider's rows, and so a
        // kinds-restricted refresh keeps the rows of the kinds it does not re-read. Kinds this identity
        // cannot read at all (a first-party account's Entra rows, a consent-refused tenant's groups) still drop.
        var discoveredByKind = RolesFor(key)
            .Where(r => r.Source == RoleSource.Discovered && eligibleKinds.Contains(r.Key.Scope.Kind))
            .GroupBy(r => r.Key.Scope.Kind)
            .ToDictionary(g => g.Key, g => g.ToList());
        var errors = new List<string>();
        var consentBlocked = tenant.DiscoveryMode != DiscoveryMode.Automatic;
        var kindsWithActive = new HashSet<RoleScopeKind>();
        var current = new List<ActiveAssignment>();
        var azureOff = false;

        foreach (var provider in providers)
        {
            var kind = provider.Kind;
            var isEntra = kind == RoleScopeKind.EntraDirectory;
            var isAzure = kind == RoleScopeKind.AzureResource;
            var isGroup = kind == RoleScopeKind.Group;
            var tenantSnapshot = tenant;
            // Eligible roles. Entra honours the consent block; ARM consent is user-consentable.
            if (!(isEntra && consentBlocked))
            {
                try
                {
                    var found = await AcquireAsync(key, identity, provider.Scopes,
                        () => provider.EligibleRolesAsync(identity, tenantSnapshot));
                    var withPolicies = await ApplyPoliciesAsync(found, identity);
                    if (generation != ConfigGeneration)
                    {
                        return;
                    }

                    discoveredByKind[kind] = withPolicies.ToList();
                    if (isEntra && await ProbeEntraActivationAsync(identity, key.TenantId) is { } support
                        && support != tenant.EntraActivation)
                    {
                        if (generation != ConfigGeneration || Tenant(key) is null)
                        {
                            return;
                        }

                        tenant = tenant with { EntraActivation = support };
                        State.UpsertTenant(tenant);
                        Persist();
                    }
                }
                catch (PimException e) when (isEntra && e.Kind == PimErrorKind.ConsentRequired)
                {
                    if (generation != ConfigGeneration)
                    {
                        return;
                    }

                    discoveredByKind[kind] = [];
                    consentBlocked = true;
                    tenant = tenant with
                    {
                        DiscoveryMode = DiscoveryMode.ManualRoles,
                        LastDiscoveryError = "Role discovery not permitted in this tenant. Configure known roles or ask an admin to consent.",
                    };
                    State.UpsertTenant(tenant);
                    Persist();
                }
                catch (PimException e) when (isGroup && IsGroupConsentFailure(e))
                {
                    if (generation != ConfigGeneration || Tenant(key) is null)
                    {
                        return;
                    }

                    discoveredByKind[kind] = [];
                    tenant = LatchGroupsOff(tenant);
                    // Claim the kind so the tail filter drops group rows we read before consent was refused.
                    kindsWithActive.Add(kind);
                    continue; // skip the active read for this provider
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (PimException e) when (e.Kind is PimErrorKind.SignInDeclined or PimErrorKind.InteractionRequired)
                {
                    AddDeclined(errors);
                }
                catch (PimException e) when (isAzure && AzureUnavailableReason(e) is { } reason)
                {
                    if (generation != ConfigGeneration)
                    {
                        return;
                    }

                    azureOff = true;
                    tenant = tenant with { AzureUnavailableReason = reason };
                    State.UpsertTenant(tenant);
                    Persist();
                }
                catch (Exception e)
                {
                    errors.Add($"{Label(kind)}: {Describe(e)}");
                }
            }

            // Active assignments. Whatever Azure rows we already know stay as they are.
            if (isAzure && azureOff)
            {
                continue;
            }

            try
            {
                var snapshot = tenant;
                var found = isEntra && consentBlocked
                    ? await provider.ActiveAssignmentsAsync(identity, snapshot)
                    : await AcquireAsync(key, identity, provider.Scopes, () => provider.ActiveAssignmentsAsync(identity, snapshot));
                if (generation != ConfigGeneration)
                {
                    return;
                }

                current.AddRange(found);
                kindsWithActive.Add(kind);
            }
            catch (PimException e) when (isEntra && consentBlocked && e.Kind is PimErrorKind.InteractionRequired or PimErrorKind.ConsentRequired)
            {
            }
            catch (PimException e) when (e.Kind is PimErrorKind.SignInDeclined or PimErrorKind.InteractionRequired)
            {
                AddDeclined(errors);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (PimException e) when (isGroup && IsGroupConsentFailure(e))
            {
                if (generation != ConfigGeneration || Tenant(key) is null)
                {
                    return;
                }

                tenant = LatchGroupsOff(tenant);
                // Claim the kind so the tail filter drops any group rows read before consent was refused.
                kindsWithActive.Add(kind);
            }
            catch (PimException e) when (isAzure && AzureUnavailableReason(e) is { } reason)
            {
                if (generation != ConfigGeneration)
                {
                    return;
                }

                azureOff = true;
                tenant = tenant with { AzureUnavailableReason = reason };
                State.UpsertTenant(tenant);
                Persist();
            }
            catch (Exception e)
            {
                errors.Add($"{Label(kind)}: {Describe(e)}");
            }
        }

        if (generation != ConfigGeneration)
        {
            return;
        }

        // Approvals are opportunistic: a 403, a missing consent or a network failure leaves the
        // previous list for that tenant and kind untouched and surfaces nothing to the user.
        // `kinds` already excludes Entra and groups for a first-party sign-in, so those accounts
        // read only the Azure approvals.
        var readApprovals = new Dictionary<RoleScopeKind, List<ApprovalRequest>>();
        foreach (var kind in kinds)
        {
            if ((kind == RoleScopeKind.AzureResource && azureOff) || (kind == RoleScopeKind.EntraDirectory && consentBlocked)
                || !ApprovalProviders.TryGetValue(kind, out var approvalProvider))
            {
                continue;
            }

            var snapshot = tenant;
            try
            {
                var found = await AcquireAsync(key, identity, approvalProvider.Scopes,
                    () => approvalProvider.PendingApprovalsAsync(identity, snapshot));
                readApprovals[kind] = [.. found];
            }
            catch (OperationCanceledException)
            {
                // The tenant was removed while it was being read.
                return;
            }
            catch (Exception)
            {
                // Keep the previous list; nothing to show the user.
            }
        }

        if (generation != ConfigGeneration)
        {
            return;
        }

        if (Tenant(key) is not null && readApprovals.Count > 0)
        {
            if (!Approvals.TryGetValue(key, out var byKind))
            {
                byKind = [];
                Approvals[key] = byKind;
            }

            foreach (var (kind, list) in readApprovals)
            {
                byKind[kind] = list;
            }

            await AnnounceNewApprovalsAsync();
        }

        if (generation != ConfigGeneration)
        {
            return;
        }

        var manual = ManualRoleSource.EligibleRoles(State.ManualRoles, key)
            .Select(role => PolicyCache.TryGetValue(role.Key, out var policy) ? role with { Policy = policy } : role)
            .ToList();
        var discovered = discoveredByKind.Values.SelectMany(v => v)
            .OrderBy(r => r.DisplayName, StringComparer.Ordinal)
            .ToList();
        Roles[key] = [.. ManualRoleSource.Merge(discovered, manual)];
        // Replace only the kinds we successfully re-read; keep the rest.
        foreach (var roleKey in Active.Keys.Where(k => k.TenantKey == key && kindsWithActive.Contains(k.Scope.Kind)).ToList())
        {
            Active.Remove(roleKey);
        }

        foreach (var a in current)
        {
            Active[a.RoleKey] = a;
        }

        if (errors.Count > 0)
        {
            var message = string.Join(" · ", errors);
            TenantErrors[key] = message;
            LogError($"{tenant.DisplayName}: {message}");
        }

        Touch();
        await RescheduleNotificationsAsync();
    }

    /// <summary>
    /// Runs a provider read; prompts at most once per tenant per session, never for a tenant that
    /// was removed meanwhile.
    /// </summary>
    private async Task<T> AcquireAsync<T>(TenantKey key, Identity identity, IReadOnlyList<string> scopes, Func<Task<T>> operation)
    {
        if (Tenant(key) is null)
        {
            throw new OperationCanceledException();
        }

        if (DeclinedTenants.Contains(key))
        {
            return await operation();
        }

        try
        {
            return await InteractionRetry.RunAsync(Tokens, identity, key.TenantId, scopes, operation);
        }
        catch (PimException e) when (IsDeclinedSignIn(e))
        {
            if (Tenant(key) is null)
            {
                throw new OperationCanceledException();
            }

            DeclinedTenants.Add(key);
            throw new PimException(PimErrorKind.SignInDeclined);
        }
    }

    // MARK: Refresh helpers

    private static void AddDeclined(List<string> errors)
    {
        var message = new PimException(PimErrorKind.SignInDeclined).UserMessage;
        if (!errors.Contains(message))
        {
            errors.Add(message);
        }
    }

    private static bool IsDeclinedSignIn(PimException e) => e.Kind switch
    {
        PimErrorKind.InteractionRequired => true,
        PimErrorKind.Network => (e.Detail ?? string.Empty).Contains("cancel", StringComparison.OrdinalIgnoreCase)
            || (e.Detail ?? string.Empty).Contains("timed out", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// Latches PIM for Groups off for this tenant. Callers must already have checked the
    /// generation and that the tenant still exists.
    /// </summary>
    private TenantContext LatchGroupsOff(TenantContext tenant)
    {
        tenant = tenant with
        {
            GroupsUnavailableReason = "PIM for Groups is not permitted in this tenant until an admin consents to the group permissions.",
        };
        State.UpsertTenant(tenant);
        Persist();
        return tenant;
    }

    /// <summary>
    /// A group read refused for permissions: the tenant has no PIM for Groups we can reach. A
    /// first-party or custom app without the group scopes answers 403 rather than a consent
    /// challenge, which the transport reports as Forbidden.
    /// </summary>
    private static bool IsGroupConsentFailure(PimException e) =>
        e.Kind is PimErrorKind.ConsentRequired or PimErrorKind.Forbidden;

    /// <summary>
    /// Spec §1: a tenant without Azure access shows no Azure rows and no error. These failures from
    /// an Azure list call mean "there is nothing here for this user", not "this refresh failed".
    /// </summary>
    private static string? AzureUnavailableReason(PimException e) => e.Kind switch
    {
        PimErrorKind.PolicyViolation => "No Azure access in this tenant",
        PimErrorKind.InteractionRequired or PimErrorKind.ConsentRequired => "Azure sign-in was not completed",
        _ => null,
    };

    private static string Label(RoleScopeKind kind) => kind switch
    {
        RoleScopeKind.EntraDirectory => "Entra",
        RoleScopeKind.AzureResource => "Azure",
        _ => "Groups",
    };

    /// <summary>
    /// Fills in policies, reusing the cache and fetching at most four at a time. A failed fetch
    /// keeps the cached policy when there is one, otherwise the manual default.
    /// </summary>
    private async Task<IReadOnlyList<EligibleRole>> ApplyPoliciesAsync(IReadOnlyList<EligibleRole> roles, Identity identity)
    {
        var generation = ConfigGeneration;
        var pending = roles.Where(r => !PolicyCache.ContainsKey(r.Key)).ToList();
        var fetched = new Dictionary<RoleKey, RolePolicy>();
        if (pending.Count > 0)
        {
            using var gate = new SemaphoreSlim(4);
            var results = await Task.WhenAll(pending.Select(async role =>
            {
                if (Coordinator.Provider(role.Key.Scope.Kind) is not { } provider)
                {
                    return (role.Key, (RolePolicy?)null);
                }

                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    return (role.Key, await provider.PolicyAsync(role, identity).ConfigureAwait(false));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return (role.Key, null);
                }
                finally
                {
                    gate.Release();
                }
            }));
            foreach (var (roleKey, policy) in results)
            {
                if (policy is not null)
                {
                    fetched[roleKey] = policy;
                }
            }
        }

        if (generation != ConfigGeneration)
        {
            return roles;
        }

        foreach (var (roleKey, policy) in fetched)
        {
            PolicyCache[roleKey] = policy;
        }

        return
        [
            .. roles.Select(role => role with { Policy = PolicyCache.GetValueOrDefault(role.Key) ?? RolePolicy.ManualDefault })
                .OrderBy(r => r.DisplayName, StringComparer.Ordinal),
        ];
    }

    // MARK: Notifications

    internal async Task RescheduleNotificationsAsync()
    {
        var names = new Dictionary<RoleKey, string>();
        foreach (var list in Roles.Values)
        {
            foreach (var r in list)
            {
                names[r.Key] = r.DisplayName;
            }
        }

        // "Contoso · alex@contoso.com": the toast body names the tenant and the account it belongs to.
        var tenantNames = State.Tenants.ToDictionary(
            t => t.Key,
            t => Identity(t.IdentityId) is { } identity ? $"{t.DisplayName} · {identity.Upn}" : t.DisplayName);
        try
        {
            await Notifier.RescheduleAsync([.. Active.Values], names, tenantNames);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogError($"Notifications: {e.Message}");
        }
    }
}
