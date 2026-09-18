using Elevate.Core.Models;
using Elevate.Core.Providers;

namespace Elevate.Core.Coordination;

/// <summary>Where one role has got to between "PIM says active" and "it works". Port of the Swift <c>PropagationState</c>.</summary>
public enum PropagationState
{
    /// <summary>Active, probed, not usable yet. Keep waiting.</summary>
    Propagating,

    /// <summary>A probe saw the access. This is the state worth telling the user about.</summary>
    Ready,

    /// <summary>Still not usable when the deadline passed. Say so, and say what usually explains it.</summary>
    Unconfirmed,

    /// <summary>
    /// Nothing here can be observed from the client — a directory-scoped Entra role, group
    /// ownership, a sign-in method whose token cannot be read. Not a failure; the role is as active
    /// as it was, we simply cannot add anything to that.
    /// </summary>
    Unobservable,
}

/// <summary>One role's propagation verdict. <see cref="Detail"/> explains the two states that are not plain progress.</summary>
public sealed record PropagationOutcome(RoleKey RoleKey, PropagationState State, string? Detail = null)
{
    /// <summary>Whether the caller can stop waiting on this role, whatever the answer was.</summary>
    public bool IsSettled => State != PropagationState.Propagating;
}

/// <summary>
/// Turns "the service says active" into "the access works", by asking each role's provider until it
/// answers yes or the deadline passes. Roles are independent: one that cannot be observed does not
/// hold up the rest, and one that is ready is reported the moment it is, so a caller can notify.
/// Port of the Swift <c>PropagationWatcher</c>.
/// </summary>
public sealed class PropagationWatcher(
    ActivationCoordinator coordinator,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>How long to wait before the first probe: a round trip costs more than it buys at t=0.</summary>
    public TimeSpan FirstInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The gap between probes settles here. Propagation is measured in minutes; polling faster only costs calls.</summary>
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Probes until every role has settled. <paramref name="deadline"/> defaults to the longest
    /// <see cref="PropagationHints.Deadline(RoleScopeKind)"/> among the roles. <paramref name="onChange"/>
    /// fires once per role, when it settles.
    /// </summary>
    public async Task<IReadOnlyList<PropagationOutcome>> WaitAsync(
        IReadOnlyList<ActiveAssignment> assignments,
        IEnumerable<Identity> identities,
        TimeSpan? deadline = null,
        Action<PropagationOutcome>? onChange = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(identities);

        var identityById = identities.DistinctBy(i => i.Id).ToDictionary(i => i.Id);
        var pending = assignments
            .Where(a => a.Status.Kind == AssignmentStatusKind.Active)
            .DistinctBy(a => a.RoleKey)
            .ToList();

        var settled = new Dictionary<RoleKey, PropagationOutcome>();
        foreach (var assignment in pending.Where(a => !identityById.ContainsKey(a.RoleKey.IdentityId)))
        {
            settled[assignment.RoleKey] = Settle(onChange, new PropagationOutcome(
                assignment.RoleKey, PropagationState.Unobservable, "That account is not signed in here."));
        }

        pending.RemoveAll(a => settled.ContainsKey(a.RoleKey));
        if (pending.Count == 0)
        {
            return [.. settled.Values];
        }

        var until = DateTimeOffset.UtcNow
            + (deadline ?? PropagationHints.Deadline(pending.Select(a => a.RoleKey.Scope.Kind)));
        var interval = FirstInterval;
        while (pending.Count > 0)
        {
            await _delay(interval, ct).ConfigureAwait(false);
            interval = interval < MaxInterval ? MaxInterval : interval;

            var round = await Task.WhenAll(pending.Select(a => ProbeAsync(a, identityById[a.RoleKey.IdentityId], ct)))
                .ConfigureAwait(false);

            var expired = DateTimeOffset.UtcNow >= until;
            foreach (var (assignment, access) in pending.Zip(round))
            {
                var outcome = Verdict(assignment.RoleKey, access, expired);
                if (outcome is not null)
                {
                    settled[assignment.RoleKey] = Settle(onChange, outcome);
                }
            }

            pending.RemoveAll(a => settled.ContainsKey(a.RoleKey));
        }

        return [.. settled.Values];
    }

    /// <summary>The outcome a probe settles on, or null while the role is still worth asking about.</summary>
    private static PropagationOutcome? Verdict(RoleKey key, EffectiveAccess access, bool expired) => access.Kind switch
    {
        EffectiveAccessKind.Confirmed => new PropagationOutcome(key, PropagationState.Ready),
        EffectiveAccessKind.Unknown => new PropagationOutcome(key, PropagationState.Unobservable, access.Detail),
        _ when expired => new PropagationOutcome(
            key, PropagationState.Unconfirmed, PropagationHints.LikelyCause(key.Scope.Kind)),
        _ => null,
    };

    private async Task<EffectiveAccess> ProbeAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct)
    {
        if (coordinator.Provider(assignment.RoleKey.Scope.Kind) is not { } provider)
        {
            return EffectiveAccess.Unknown("No provider for this kind of role.");
        }

        try
        {
            return await provider.EffectiveAccessAsync(assignment, identity, ct).ConfigureAwait(false);
        }
        catch (PimException e)
        {
            // A refused or failed probe is not a failed activation: the role is active either way.
            return EffectiveAccess.Unknown(e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return EffectiveAccess.Unknown(e.Message);
        }
    }

    private static PropagationOutcome Settle(Action<PropagationOutcome>? onChange, PropagationOutcome outcome)
    {
        onChange?.Invoke(outcome);
        return outcome;
    }
}
