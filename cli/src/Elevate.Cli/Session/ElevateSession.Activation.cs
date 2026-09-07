using Elevate.Core.Coordination;
using Elevate.Core.Models;

namespace Elevate.Cli.Session;

/// <summary>Activation, extension, deactivation and cancellation. Port of <c>AppModel.Activation</c>.</summary>
public sealed partial class ElevateSession
{
    /// <summary>
    /// Sends the requests. With <paramref name="deactivateFirst"/> an already active role is
    /// deactivated before it is re-activated, which is how Extend works. Outcomes come back in
    /// request order; the state (memory, rekeyed manual roles, consent latches) is persisted.
    /// </summary>
    public async Task<IReadOnlyList<ActivationOutcome>> ActivateAsync(
        IReadOnlyList<ActivationRequest> requests, bool deactivateFirst, Action<ActivationOutcome>? onProgress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var deactivated = new HashSet<RoleKey>();
        var early = new List<ActivationOutcome>();
        var attempted = new List<ActivationRequest>();
        foreach (var r in requests)
        {
            if (!deactivateFirst || Active.GetValueOrDefault(r.RoleKey) is not { } existing
                || existing.Status.Kind != AssignmentStatusKind.Active || Identity(r.RoleKey.IdentityId) is not { } identity)
            {
                attempted.Add(r);
                continue;
            }

            try
            {
                await Coordinator.DeactivateAsync(existing, identity, ct).ConfigureAwait(false);
                Mutate(() => Active.Remove(r.RoleKey));
                deactivated.Add(r.RoleKey);
                attempted.Add(r);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                var message = Describe(e);
                LogError($"{RoleName(r.RoleKey)}: could not deactivate before re-activating: {message}");
                early.Add(new ActivationOutcome(r.RoleKey, new ActivationResult.Failed(
                    new PimException(PimErrorKind.Unexpected, $"Could not deactivate before re-activating: {message}"))));
            }
        }

        var outcomes = await Coordinator.ActivateAsync(attempted, State.Identities, onProgress, ct).ConfigureAwait(false);
        var consentBlocked = new HashSet<TenantKey>();
        lock (_sync)
        {
            foreach (var outcome in outcomes)
            {
                if (attempted.FirstOrDefault(r => r.RoleKey == outcome.RoleKey) is not { } request)
                {
                    continue;
                }

                switch (outcome.Result)
                {
                    case ActivationResult.Activated or ActivationResult.PendingApproval or ActivationResult.Scheduled:
                        var a = AssignmentOf(outcome.Result)!;
                        if (a.RoleKey != request.RoleKey)
                        {
                            Rekey(request.RoleKey, a.RoleKey);
                        }

                        Active[a.RoleKey] = a;
                        State.Remember(a.RoleKey, request.Justification, request.Duration);
                        break;
                    case ActivationResult.Failed { Error: var error }:
                        Active.Remove(request.RoleKey);
                        LogError($"{RoleName(request.RoleKey)}: {error.UserMessage}");
                        if (error.Kind == PimErrorKind.ConsentRequired)
                        {
                            consentBlocked.Add(request.RoleKey.TenantKey);
                        }

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

                var method = Identity(tenantKey.IdentityId)?.SignInMethod ?? SignInMethod.OwnApp;
                State.UpsertTenant(t with
                {
                    DiscoveryMode = DiscoveryMode.ManualRoles,
                    LastDiscoveryError = method.UsesMsal
                        ? "Activation not permitted in this tenant until an admin consents."
                        : $"Activation not permitted in this tenant for the {method.DisplayName}; try your own app registration instead.",
                });
            }

            _store.Save(State);
        }

        // Report a failed re-activation of something we deactivated for what it is.
        var reported = outcomes.Select(o =>
            o.Result is ActivationResult.Failed f && deactivated.Contains(o.RoleKey)
                ? new ActivationOutcome(o.RoleKey, new ActivationResult.Failed(new PimException(
                    PimErrorKind.Unexpected, $"Deactivated, but re-activation failed: {f.Error.UserMessage}")))
                : o);
        return [.. early, .. reported];
    }

    public async Task DeactivateAsync(RoleKey key, CancellationToken ct = default)
    {
        var assignment = Active.GetValueOrDefault(key) ?? throw new PimException(PimErrorKind.Unexpected, "That role is not active");
        var identity = Identity(key.IdentityId) ?? throw new PimException(PimErrorKind.Unexpected, "Unknown account");
        await Coordinator.DeactivateAsync(assignment, identity, ct).ConfigureAwait(false);
        Mutate(() => Active.Remove(key));
    }

    public async Task CancelPendingAsync(RoleKey key, CancellationToken ct = default)
    {
        var assignment = Active.GetValueOrDefault(key) ?? throw new PimException(PimErrorKind.Unexpected, "That role has no pending request");
        var identity = Identity(key.IdentityId) ?? throw new PimException(PimErrorKind.Unexpected, "Unknown account");
        await Coordinator.CancelPendingRequestAsync(assignment, identity, ct).ConfigureAwait(false);
        Mutate(() => Active.Remove(key));
    }

    public static ActiveAssignment? AssignmentOf(ActivationResult result) => result switch
    {
        ActivationResult.Activated r => r.Assignment,
        ActivationResult.PendingApproval r => r.Assignment,
        ActivationResult.Scheduled r => r.Assignment,
        _ => null,
    };

    /// <summary>Moves a manual role from the key the user typed to the key the provider resolved it to.</summary>
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
}
