using Elevate.Core.Auth;
using Elevate.Core.Catalogue;
using Elevate.Core.Models;
using Elevate.Core.Providers;

namespace Elevate.Cli.Session;

/// <summary>Reading roles, assignments and approvals per tenant. Port of <c>AppModel.Refresh</c>.</summary>
public sealed partial class ElevateSession
{
    /// <summary>Reads every given tenant, in parallel. Failures land in <see cref="TenantErrors"/>, never throw.</summary>
    public async Task RefreshAsync(IEnumerable<TenantKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var list = keys.Distinct().ToList();
        await Task.WhenAll(list.Select(key => RefreshTenantAsync(key, ct))).ConfigureAwait(false);
    }

    public Task RefreshAllAsync(CancellationToken ct = default) => RefreshAsync(State.Tenants.Select(t => t.Key).ToList(), ct);

    public async Task RefreshTenantAsync(TenantKey key, CancellationToken ct = default, IReadOnlySet<RoleScopeKind>? requestedKinds = null)
    {
        if (Identity(key.IdentityId) is not { } identity || Tenant(key) is not { } tenant)
        {
            return;
        }

        try
        {
            await RefreshCoreAsync(key, identity, tenant, requestedKinds, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Mutate(() => TenantErrors[key] = Describe(e));
            LogError($"{tenant.DisplayName}: {Describe(e)}");
        }
        finally
        {
            Mutate(() => LoadedTenants.Add(key));
        }
    }

    private async Task RefreshCoreAsync(TenantKey key, Identity identity, TenantContext tenant, IReadOnlySet<RoleScopeKind>? requestedKinds, CancellationToken ct)
    {
        if (requestedKinds is null)
        {
            Mutate(() => TenantErrors.Remove(key));
        }

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

        var kinds = eligibleKinds.ToList();
        if (requestedKinds is not null)
        {
            kinds.RemoveAll(k => !requestedKinds.Contains(k));
        }

        var providers = kinds.Select(Coordinator.Provider).OfType<IPimProvider>().ToList();
        Dictionary<RoleScopeKind, List<EligibleRole>> discoveredByKind;
        lock (_sync)
        {
            discoveredByKind = (Roles.TryGetValue(key, out var existing) ? existing : [])
                .Where(r => r.Source == RoleSource.Discovered && eligibleKinds.Contains(r.Key.Scope.Kind))
                .GroupBy(r => r.Key.Scope.Kind)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

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
            var snapshot = tenant;
            if (!(isEntra && consentBlocked))
            {
                try
                {
                    var found = await Acquire(identity, key.TenantId, provider.Scopes, () => provider.EligibleRolesAsync(identity, snapshot, ct), ct).ConfigureAwait(false);
                    var withPolicies = await ApplyPoliciesAsync(found, identity, ct).ConfigureAwait(false);
                    discoveredByKind[kind] = [.. withPolicies];
                    if (isEntra && await ProbeEntraActivationAsync(identity, key.TenantId).ConfigureAwait(false) is { } support
                        && support != tenant.EntraActivation)
                    {
                        tenant = Upsert(tenant with { EntraActivation = support });
                    }
                }
                catch (PimException e) when (isEntra && e.Kind == PimErrorKind.ConsentRequired)
                {
                    discoveredByKind[kind] = [];
                    consentBlocked = true;
                    tenant = Upsert(tenant with
                    {
                        DiscoveryMode = DiscoveryMode.ManualRoles,
                        LastDiscoveryError = "Role discovery not permitted in this tenant. Configure known roles or ask an admin to consent.",
                    });
                }
                catch (PimException e) when (isGroup && IsGroupConsentFailure(e))
                {
                    discoveredByKind[kind] = [];
                    tenant = LatchGroupsOff(tenant);
                    kindsWithActive.Add(kind);
                    continue;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (PimException e) when (e.Kind is PimErrorKind.SignInDeclined or PimErrorKind.InteractionRequired)
                {
                    AddDeclined(errors);
                }
                catch (PimException e) when (isAzure && AzureUnavailableReason(e) is { } reason)
                {
                    azureOff = true;
                    tenant = Upsert(tenant with { AzureUnavailableReason = reason });
                }
                catch (Exception e)
                {
                    errors.Add($"{Label(kind)}: {Describe(e)}");
                }
            }

            if (isAzure && azureOff)
            {
                continue;
            }

            try
            {
                var snapshot2 = tenant;
                var found = isEntra && consentBlocked
                    ? await provider.ActiveAssignmentsAsync(identity, snapshot2, ct).ConfigureAwait(false)
                    : await Acquire(identity, key.TenantId, provider.Scopes, () => provider.ActiveAssignmentsAsync(identity, snapshot2, ct), ct).ConfigureAwait(false);
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
                throw;
            }
            catch (PimException e) when (isGroup && IsGroupConsentFailure(e))
            {
                tenant = LatchGroupsOff(tenant);
                kindsWithActive.Add(kind);
            }
            catch (PimException e) when (isAzure && AzureUnavailableReason(e) is { } reason)
            {
                azureOff = true;
                tenant = Upsert(tenant with { AzureUnavailableReason = reason });
            }
            catch (Exception e)
            {
                errors.Add($"{Label(kind)}: {Describe(e)}");
            }
        }

        // Approvals are opportunistic: a refusal keeps the previous list and says nothing.
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
                var found = await Acquire(identity, key.TenantId, approvalProvider.Scopes, () => approvalProvider.PendingApprovalsAsync(identity, snapshot, ct), ct).ConfigureAwait(false);
                readApprovals[kind] = [.. found];
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Keep the previous list.
            }
        }

        lock (_sync)
        {
            if (readApprovals.Count > 0)
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
            }

            var manual = ManualRoleSource.EligibleRoles(State.ManualRoles, key)
                .Select(role => PolicyCache.TryGetValue(role.Key, out var policy) ? role with { Policy = policy } : role)
                .ToList();
            var discovered = discoveredByKind.Values.SelectMany(v => v)
                .OrderBy(r => r.DisplayName, StringComparer.Ordinal)
                .ToList();
            Roles[key] = [.. ManualRoleSource.Merge(discovered, manual)];
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
        }
    }

    /// <summary>Runs a read with one interactive retry; a declined sign-in is reported, not retried again.</summary>
    private Task<T> Acquire<T>(Identity identity, string tenantId, IReadOnlyList<string> scopes, Func<Task<T>> operation, CancellationToken ct) =>
        InteractionRetry.RunAsync(Tokens, identity, tenantId, scopes, operation, ct: ct);

    private TenantContext Upsert(TenantContext tenant)
    {
        lock (_sync)
        {
            State.UpsertTenant(tenant);
            _store.Save(State);
        }

        return tenant;
    }

    private TenantContext LatchGroupsOff(TenantContext tenant) => Upsert(tenant with
    {
        GroupsUnavailableReason = "PIM for Groups is not permitted in this tenant until an admin consents to the group permissions.",
    });

    private static void AddDeclined(List<string> errors)
    {
        var message = new PimException(PimErrorKind.SignInDeclined).UserMessage;
        if (!errors.Contains(message))
        {
            errors.Add(message);
        }
    }

    private static bool IsGroupConsentFailure(PimException e) =>
        e.Kind is PimErrorKind.ConsentRequired or PimErrorKind.Forbidden;

    private static string? AzureUnavailableReason(PimException e) => e.Kind switch
    {
        PimErrorKind.PolicyViolation => "No Azure access in this tenant",
        PimErrorKind.InteractionRequired or PimErrorKind.ConsentRequired => "Azure sign-in was not completed",
        _ => null,
    };

    internal static string Label(RoleScopeKind kind) => kind switch
    {
        RoleScopeKind.EntraDirectory => "Entra",
        RoleScopeKind.AzureResource => "Azure",
        _ => "Groups",
    };

    /// <summary>Reads the cached Graph token's scopes; silent only, null when nothing can be told.</summary>
    internal async Task<EntraActivationSupport?> ProbeEntraActivationAsync(Identity identity, string tenantId)
    {
        string token;
        try
        {
            token = await Tokens.AccessTokenAsync(identity, tenantId, Scopes.GraphAll, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }

        if (AccessTokenClaims.PermitsEntraActivation(token) is not { } permitted)
        {
            return null;
        }

        if (permitted)
        {
            return EntraActivationSupport.Supported;
        }

        var reason = identity.SignInMethod.EntraViewOnlyReason
            ?? $"The app registration used for this account ({identity.SignInMethod.DisplayName}) was not granted RoleAssignmentSchedule.ReadWrite.Directory in this tenant, so it supports activation of Azure resource roles only here; Entra roles are listed but cannot be activated.";
        return EntraActivationSupport.Unsupported(reason);
    }

    /// <summary>Fills in policies, at most four fetches at a time; a failed fetch keeps the cached policy or the manual default.</summary>
    private async Task<IReadOnlyList<EligibleRole>> ApplyPoliciesAsync(IReadOnlyList<EligibleRole> roles, Identity identity, CancellationToken ct)
    {
        List<EligibleRole> pending;
        lock (_sync)
        {
            pending = roles.Where(r => !PolicyCache.ContainsKey(r.Key)).ToList();
        }

        if (pending.Count > 0)
        {
            using var gate = new SemaphoreSlim(4);
            var results = await Task.WhenAll(pending.Select(async role =>
            {
                if (Coordinator.Provider(role.Key.Scope.Kind) is not { } provider)
                {
                    return (role.Key, (RolePolicy?)null);
                }

                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return (role.Key, await provider.PolicyAsync(role, identity, ct).ConfigureAwait(false));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return (role.Key, null);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
            lock (_sync)
            {
                foreach (var (roleKey, policy) in results)
                {
                    if (policy is not null)
                    {
                        PolicyCache[roleKey] = policy;
                    }
                }
            }
        }

        lock (_sync)
        {
            return
            [
                .. roles.Select(role => role with { Policy = PolicyCache.GetValueOrDefault(role.Key) ?? RolePolicy.ManualDefault })
                    .OrderBy(r => r.DisplayName, StringComparer.Ordinal),
            ];
        }
    }
}
