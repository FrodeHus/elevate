using Elevate.Core.Auth;
using Elevate.Core.Coordination;
using Elevate.Core.Models;

namespace Elevate.App.ViewModels;

/// <summary>Activation, deactivation and cancellation. Port of <c>AppModel+Activation.swift</c>.</summary>
public sealed partial class AppModel
{
    // MARK: Entra activation capability

    /// <summary>
    /// Why Entra roles in this tenant are view-only, or null when they can be activated. A tenant
    /// that has been probed answers from its token; otherwise the sign-in method's known
    /// capabilities decide, so a first-party account is view-only from the moment it is added.
    /// </summary>
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

    /// <summary>Whether the account may activate this role in its tenant. Azure and group roles always may.</summary>
    public bool CanActivate(RoleKey key) =>
        key.Scope.Kind != RoleScopeKind.EntraDirectory || EntraViewOnlyReason(key.TenantKey) is null;

    /// <summary>
    /// Reads the cached Graph token's <c>scp</c> claim. Silent only: never prompts, and null when no
    /// token is at hand or it hides its scopes, in which case the caller keeps what it knew.
    /// </summary>
    internal async Task<EntraActivationSupport?> ProbeEntraActivationAsync(Identity identity, string tenantId)
    {
        string token;
        try
        {
            token = await Tokens.AccessTokenAsync(identity, tenantId, Scopes.GraphAll, CancellationToken.None);
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

    // MARK: Activation

    /// <summary>
    /// Activates the requests. Roles that are already active are deactivated first so "Extend" works.
    /// Returns the coordinator's outcomes so callers can report on them; empty when the run was
    /// abandoned because the configuration changed under it.
    /// </summary>
    public async Task<IReadOnlyList<ActivationOutcome>> ActivateAsync(IReadOnlyList<ActivationRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var generation = ConfigGeneration;
        foreach (var r in requests)
        {
            Progress.Remove(r.RoleKey);
            InFlight.Add(r.RoleKey);
        }

        Touch();
        try
        {
            return await ActivateCoreAsync(requests, generation, ct);
        }
        finally
        {
            foreach (var r in requests)
            {
                InFlight.Remove(r.RoleKey);
            }

            Touch();
        }
    }

    private async Task<IReadOnlyList<ActivationOutcome>> ActivateCoreAsync(IReadOnlyList<ActivationRequest> requests, int generation, CancellationToken ct)
    {
        var deactivated = new HashSet<RoleKey>();
        var skipped = new HashSet<RoleKey>();
        foreach (var r in requests)
        {
            if (Active.GetValueOrDefault(r.RoleKey) is not { } existing
                || existing.Status.Kind != AssignmentStatusKind.Active
                || Identity(r.RoleKey.IdentityId) is not { } identity)
            {
                continue;
            }

            try
            {
                await Coordinator.DeactivateAsync(existing, identity, ct);
                if (generation != ConfigGeneration)
                {
                    return [];
                }

                Active.Remove(r.RoleKey);
                deactivated.Add(r.RoleKey);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                if (generation != ConfigGeneration)
                {
                    return [];
                }

                var message = Describe(e);
                TenantErrors[r.RoleKey.TenantKey] = message;
                Progress[r.RoleKey] = new ActivationResult.Failed(
                    new PimException(PimErrorKind.Unexpected, $"Could not deactivate before re-activating: {message}"));
                LogError($"{SummaryName(r.RoleKey)}: could not deactivate before re-activating: {message}");
                skipped.Add(r.RoleKey);
            }

            Touch();
        }

        var attempted = requests.Where(r => !skipped.Contains(r.RoleKey)).ToList();
        var outcomes = await Coordinator.ActivateAsync(attempted, State.Identities, outcome => Post(() =>
        {
            // This hop can land after the final loop below, which is authoritative; only fill a gap.
            if (generation != ConfigGeneration)
            {
                return;
            }

            Progress.TryAdd(outcome.RoleKey, outcome.Result);
            Touch();
        }), ct);

        if (generation != ConfigGeneration)
        {
            return [];
        }

        var consentBlocked = new HashSet<TenantKey>();
        foreach (var outcome in outcomes)
        {
            Progress[outcome.RoleKey] = outcome.Result;
            if (attempted.FirstOrDefault(r => r.RoleKey == outcome.RoleKey) is not { } request)
            {
                continue;
            }

            switch (outcome.Result)
            {
                case ActivationResult.Activated or ActivationResult.PendingApproval or ActivationResult.Scheduled:
                    var a = AssignmentOf(outcome.Result)!;
                    // A manual Azure role is keyed by role name; the provider answers with the resolved
                    // definition id, so move the stored role, its memory and its row onto that key.
                    if (a.RoleKey != request.RoleKey)
                    {
                        Rekey(request.RoleKey, a.RoleKey);
                    }

                    Active[a.RoleKey] = a;
                    State.Remember(a.RoleKey, request.Justification, request.Duration);
                    break;
                case ActivationResult.Failed { Error: var error }:
                    Active.Remove(request.RoleKey);
                    LogError($"{SummaryName(request.RoleKey)}: {error.UserMessage}");
                    if (deactivated.Contains(request.RoleKey))
                    {
                        Progress[request.RoleKey] = new ActivationResult.Failed(new PimException(
                            PimErrorKind.Unexpected, $"Deactivated, but re-activation failed: {error.UserMessage}"));
                    }

                    // Spec §8 step 4: an activation refused for consent puts the tenant in manual mode.
                    if (error.Kind == PimErrorKind.ConsentRequired)
                    {
                        consentBlocked.Add(request.RoleKey.TenantKey);
                    }

                    // A first-party sign-in refused for the write scope: the tenant's Entra rows become view-only.
                    if (error.Kind == PimErrorKind.Forbidden
                        && request.RoleKey.Scope.Kind == RoleScopeKind.EntraDirectory
                        && Tenant(request.RoleKey.TenantKey) is { } t)
                    {
                        State.UpsertTenant(t with { EntraActivation = EntraActivationSupport.Unsupported(error.UserMessage) });
                    }

                    break;
                default:
                    break;
            }
        }

        foreach (var tenantKey in consentBlocked)
        {
            if (Tenant(tenantKey) is not { } t)
            {
                continue;
            }

            // Only an own-app registration can be consented to; the first-party client ids are
            // Microsoft's and are not ours to request consent for.
            var method = State.Identities.FirstOrDefault(i => i.Id == tenantKey.IdentityId)?.SignInMethod ?? SignInMethod.OwnApp;
            State.UpsertTenant(t with
            {
                DiscoveryMode = DiscoveryMode.ManualRoles,
                LastDiscoveryError = method.UsesMsal
                    ? "Activation not permitted in this tenant until an admin consents."
                    : $"Activation not permitted in this tenant for the {method.DisplayName}; try your own app registration instead.",
            });
        }

        Persist();
        var changedGroupTenants = outcomes
            .Where(o => o.RoleKey.Scope.Kind == RoleScopeKind.Group && o.Result is ActivationResult.Activated)
            .Select(o => o.RoleKey.TenantKey)
            .ToHashSet();
        RefreshRolesAfterGroupChange(changedGroupTenants);
        SelectMode = false;
        await RescheduleNotificationsAsync();
        // Runs independently so a slow policy fetch cannot hold the activation spinner; it only
        // updates cached policy/role data afterwards, never InFlight.
        _ = LearnPoliciesForManualRolesAsync(outcomes);
        return outcomes;
    }

    // MARK: Activation helpers

    /// <summary>The assignment an outcome carries, or null for a failure.</summary>
    public static ActiveAssignment? AssignmentOf(ActivationResult result) => result switch
    {
        ActivationResult.Activated r => r.Assignment,
        ActivationResult.PendingApproval r => r.Assignment,
        ActivationResult.Scheduled r => r.Assignment,
        _ => null,
    };

    /// <summary>
    /// Moves a manual role from the key the user typed to the key the provider resolved it to.
    /// The row keeps its manual source and detail; <see cref="Core.Catalogue.ManualRoleSource.Merge"/>
    /// drops it once discovery returns the same scope and name.
    /// </summary>
    private void Rekey(RoleKey old, RoleKey @new)
    {
        var tenantKey = old.TenantKey;
        if (tenantKey != @new.TenantKey)
        {
            return;
        }

        var manualIndex = State.ManualRoles.FindIndex(m => m.TenantKey == tenantKey && m.Scope == old.Scope);
        if (manualIndex >= 0)
        {
            State.ManualRoles[manualIndex] = State.ManualRoles[manualIndex] with { Scope = @new.Scope };
        }

        if (State.MemoryFor(old) is { } remembered)
        {
            State.Memory.RemoveAll(m => m.RoleKey == old);
            State.Remember(@new, remembered.Justification, remembered.LastDuration);
        }

        if (Roles.TryGetValue(tenantKey, out var list))
        {
            var index = list.FindIndex(r => r.Key == old);
            if (index >= 0)
            {
                list[index] = list[index] with { Key = @new };
            }
        }

        if (PolicyCache.Remove(old, out var policy))
        {
            PolicyCache[@new] = policy;
        }

        if (Progress.Remove(old, out var progress))
        {
            Progress[@new] = progress;
        }

        Active.Remove(old);
        foreach (var profile in State.Profiles)
        {
            for (var i = 0; i < profile.Entries.Count; i++)
            {
                if (profile.Entries[i].RoleKey == old)
                {
                    profile.Entries[i] = profile.Entries[i] with { RoleKey = @new };
                }
            }
        }
    }

    /// <summary>A manual role has no policy until Entra accepts an activation; that is the moment we can read one.</summary>
    private async Task LearnPoliciesForManualRolesAsync(IReadOnlyList<ActivationOutcome> outcomes)
    {
        var generation = ConfigGeneration;
        foreach (var outcome in outcomes)
        {
            if (outcome.Result is not ActivationResult.Activated
                || Role(outcome.RoleKey) is not { Source: RoleSource.Manual } role
                || Identity(outcome.RoleKey.IdentityId) is not { } identity
                || Coordinator.Provider(outcome.RoleKey.Scope.Kind) is not { } provider)
            {
                continue;
            }

            RolePolicy policy;
            try
            {
                policy = await provider.PolicyAsync(role, identity);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue;
            }

            if (generation != ConfigGeneration)
            {
                return;
            }

            PolicyCache[outcome.RoleKey] = policy;
            if (Roles.TryGetValue(outcome.RoleKey.TenantKey, out var list))
            {
                var index = list.FindIndex(r => r.Key == outcome.RoleKey);
                if (index >= 0)
                {
                    list[index] = list[index] with { Policy = policy };
                }
            }

            Touch();
        }
    }

    // MARK: Deactivation

    /// <summary>Withdraws a request that is still waiting for an approver.</summary>
    public async Task CancelPendingAsync(RoleKey key, CancellationToken ct = default)
    {
        var generation = ConfigGeneration;
        InFlight.Add(key);
        Touch();
        try
        {
            if (Active.GetValueOrDefault(key) is not { } a || Identity(key.IdentityId) is not { } identity)
            {
                return;
            }

            try
            {
                await Coordinator.CancelPendingRequestAsync(a, identity, ct);
                if (generation != ConfigGeneration)
                {
                    return;
                }

                Active.Remove(key);
                await RescheduleNotificationsAsync();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                if (generation != ConfigGeneration)
                {
                    return;
                }

                var message = Describe(e);
                TenantErrors[key.TenantKey] = message;
                LogError($"{SummaryName(key)}: {message}");
            }
        }
        finally
        {
            InFlight.Remove(key);
            Touch();
        }
    }

    public async Task DeactivateAsync(RoleKey key, CancellationToken ct = default)
    {
        var generation = ConfigGeneration;
        InFlight.Add(key);
        Touch();
        try
        {
            if (Active.GetValueOrDefault(key) is not { } a || Identity(key.IdentityId) is not { } identity)
            {
                return;
            }

            try
            {
                // A booked-ahead activation is still only a request: withdraw it. Providers differ on
                // whether cancel is accepted once the schedule exists, so fall back to a deactivation.
                if (a.Status.Kind == AssignmentStatusKind.Scheduled)
                {
                    try
                    {
                        await Coordinator.CancelPendingRequestAsync(a, identity, ct);
                    }
                    catch (PimException)
                    {
                        await Coordinator.DeactivateAsync(a, identity, ct);
                    }
                }
                else
                {
                    await Coordinator.DeactivateAsync(a, identity, ct);
                }

                if (generation != ConfigGeneration)
                {
                    return;
                }

                Active.Remove(key);
                if (key.Scope.Kind == RoleScopeKind.Group)
                {
                    RefreshRolesAfterGroupChange([key.TenantKey]);
                }

                await RescheduleNotificationsAsync();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                if (generation != ConfigGeneration)
                {
                    return;
                }

                var message = Describe(e);
                TenantErrors[key.TenantKey] = message;
                LogError($"{SummaryName(key)}: {message}");
            }
        }
        finally
        {
            InFlight.Remove(key);
            Touch();
        }
    }

    /// <summary>Membership changes carry roles with them; give the directory a moment, then re-read roles only.</summary>
    private void RefreshRolesAfterGroupChange(IEnumerable<TenantKey> keys)
    {
        var tenantKeys = keys.ToHashSet();
        if (tenantKeys.Count == 0)
        {
            return;
        }

        var generation = ConfigGeneration;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Post(async () =>
            {
                if (generation != ConfigGeneration)
                {
                    return;
                }

                // A refresh already in flight would make RefreshAsync return without re-reading; wait it out once.
                if (tenantKeys.Any(Busy.Contains))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    if (generation != ConfigGeneration)
                    {
                        return;
                    }
                }

                foreach (var key in tenantKeys.Where(k => Tenant(k) is not null))
                {
                    await RefreshAsync(key, new HashSet<RoleScopeKind> { RoleScopeKind.EntraDirectory, RoleScopeKind.AzureResource });
                }
            });
        });
    }

    public void ClearProgress(IEnumerable<RoleKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var k in keys)
        {
            Progress.Remove(k);
        }

        Touch();
    }
}
