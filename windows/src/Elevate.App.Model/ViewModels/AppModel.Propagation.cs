using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Support;

namespace Elevate.App.ViewModels;

/// <summary>
/// The gap between "PIM says active" and "the access works". Port of the macOS
/// <c>AppModel+Propagation.swift</c>.
/// <para>
/// A row goes green the moment the request settles, which is the service's own view and not
/// evidence of anything — the access follows minutes later. After an activation the model probes
/// each role until it is genuinely in effect, and the row says so until then.
/// </para>
/// </summary>
public sealed partial class AppModel
{
    /// <summary>
    /// Roles that have been probed since they were activated, and where each got to. A role stays
    /// here once it settles — <see cref="PropagationState.Ready"/> is what lets the activation
    /// dialog close on "Ready" rather than on the word PIM would have used. Only Propagating and
    /// Unconfirmed change a row; the other two read as a plain active row, which is what it was
    /// before this existed. Live state; never persisted.
    /// </summary>
    public Dictionary<RoleKey, PropagationState> Propagation { get; } = [];

    private readonly Dictionary<RoleKey, CancellationTokenSource> _propagationWatches = [];

    /// <summary>Probe pacing. Tests shorten it so they do not sit through the real interval.</summary>
    internal TimeSpan PropagationFirstInterval { get; set; } = TimeSpan.FromSeconds(5);

    internal TimeSpan PropagationMaxInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The role's state, or null when it is not being watched. Not <c>GetValueOrDefault</c>: the
    /// zero value of <see cref="PropagationState"/> is <see cref="PropagationState.Propagating"/>,
    /// which would make every plain active row read as propagating.
    /// </summary>
    private PropagationState? StateOf(RoleKey key) =>
        Propagation.TryGetValue(key, out var state) ? state : null;

    /// <summary>What the row says under a role that is active but not usable yet, or null for a plain active row.</summary>
    public string? PropagationNote(RoleKey key) => StateOf(key) switch
    {
        PropagationState.Propagating => $"propagating (~{Countdown.Label(PropagationHints.Typical(key.Scope.Kind))})",
        PropagationState.Unconfirmed => "not in effect yet",
        _ => null,
    };

    /// <summary>The tooltip behind <see cref="PropagationNote"/>: what to expect, or what to do about it.</summary>
    public string? PropagationTooltip(RoleKey key) => StateOf(key) switch
    {
        PropagationState.Propagating =>
            "PIM has recorded the activation. Elevate is checking that the access actually works; "
            + "the countdown has already started.",
        PropagationState.Unconfirmed => PropagationHints.LikelyCause(key.Scope.Kind),
        _ => null,
    };

    /// <summary>
    /// Starts probing everything <paramref name="outcomes"/> activated. Roles already being watched
    /// are left alone, so a second activation of the same role does not run two probes against it.
    /// </summary>
    internal void WatchPropagation(IReadOnlyList<ActivationOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        var assignments = outcomes
            .Select(o => o.Result is ActivationResult.Activated activated ? activated.Assignment : null)
            .OfType<ActiveAssignment>()
            .Where(a => a.Status.Kind == AssignmentStatusKind.Active && !_propagationWatches.ContainsKey(a.RoleKey))
            .ToList();

        foreach (var assignment in assignments)
        {
            Watch(assignment);
        }

        if (assignments.Count > 0)
        {
            Touch();
        }
    }

    private void Watch(ActiveAssignment assignment)
    {
        var key = assignment.RoleKey;
        var cts = new CancellationTokenSource();
        _propagationWatches[key] = cts;
        Propagation[key] = PropagationState.Propagating;

        _ = Task.Run(async () =>
        {
            var generation = ConfigGeneration;
            var watcher = new PropagationWatcher(Coordinator)
            {
                FirstInterval = PropagationFirstInterval,
                MaxInterval = PropagationMaxInterval,
            };
            try
            {
                await watcher.WaitAsync(
                    [assignment], State.Identities, null,
                    outcome => Post(() => Settle(generation, outcome)), cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Deactivated, signed out, or the configuration changed under it: nothing to report.
            }
            finally
            {
                Post(() => Finish(key, cts));
            }
        }, cts.Token);
    }

    /// <summary>
    /// Records where a role got to, and tells the user when that is worth an interruption: the
    /// moment it becomes usable, and the moment it is clear it has not.
    /// </summary>
    internal void Settle(int generation, PropagationOutcome outcome)
    {
        if (generation != ConfigGeneration || !Propagation.ContainsKey(outcome.RoleKey))
        {
            return;
        }

        // Expired, deactivated elsewhere, or dropped by a refresh while the probe ran: there is no
        // row left to say anything on, so the entry goes rather than lingering.
        if (!Active.ContainsKey(outcome.RoleKey))
        {
            Propagation.Remove(outcome.RoleKey);
            Touch();
            return;
        }

        Propagation[outcome.RoleKey] = outcome.State;
        switch (outcome.State)
        {
            case PropagationState.Ready:
                _ = NotifyReadyAsync(outcome.RoleKey);
                break;
            case PropagationState.Unconfirmed:
                // Whoever activated this has almost certainly moved on; the row alone would leave
                // them believing a role works when it does not. No quiet window here — reaching
                // the deadline means the wait was long enough to have been noticed.
                _ = NotifyUnconfirmedAsync(outcome.RoleKey, outcome.Detail);
                break;
            default:
                break;
        }

        Touch();
    }

    /// <summary>
    /// Waits for <paramref name="keys"/> to stop propagating, up to <paramref name="within"/>, and
    /// answers whether every one of them is in effect. The activation dialog holds for this so it
    /// can close on an honest word instead of on the "active" the service would have given it.
    /// </summary>
    public async Task<bool> SettledInEffectAsync(IReadOnlyList<RoleKey> keys, TimeSpan within, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return false;
        }

        var deadline = DateTimeOffset.UtcNow + within;
        while (keys.Any(k => Propagation.GetValueOrDefault(k, PropagationState.Unobservable) == PropagationState.Propagating))
        {
            if (DateTimeOffset.UtcNow >= deadline || ct.IsCancellationRequested)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        return keys.All(k => Propagation.GetValueOrDefault(k, PropagationState.Unobservable) == PropagationState.Ready);
    }

    /// <summary>
    /// Tells the user the role is usable now. The point of the probe is that they switched away
    /// while it ran, so this is the only way they learn the right moment has come — but a role that
    /// was ready almost at once was never worth interrupting them for.
    /// </summary>
    private async Task NotifyReadyAsync(RoleKey key)
    {
        if (Active.GetValueOrDefault(key) is not { } assignment
            || DateTimeOffset.UtcNow - assignment.StartDateTime < QuietPropagation)
        {
            return;
        }

        var tenant = Tenant(key.TenantKey)?.DisplayName ?? key.TenantId;
        await Notifier.NotifyAsync($"{SummaryName(key)} is ready", $"The access is in effect in {tenant}.");
    }

    /// <summary>
    /// A role ready inside this is one nobody waited for, so it passes without a notification.
    /// Slightly over the watcher's first interval, which is the soonest a probe can confirm.
    /// </summary>
    internal static readonly TimeSpan QuietPropagation = TimeSpan.FromSeconds(8);

    /// <summary>Says the role is active but does not work yet, and what usually explains that.</summary>
    private async Task NotifyUnconfirmedAsync(RoleKey key, string? cause)
    {
        if (!Active.ContainsKey(key))
        {
            return;
        }

        await Notifier.NotifyAsync(
            $"{SummaryName(key)} is active but not in effect",
            cause ?? PropagationHints.LikelyCause(key.Scope.Kind));
    }

    private void Finish(RoleKey key, CancellationTokenSource cts)
    {
        if (_propagationWatches.GetValueOrDefault(key) == cts)
        {
            _propagationWatches.Remove(key);
        }

        cts.Dispose();
        Touch();
    }

    /// <summary>
    /// Stops watching a role and drops its state. Called when it is deactivated or its account
    /// goes away: a propagating row whose assignment no longer exists must not linger.
    /// </summary>
    internal void StopWatchingPropagation(RoleKey key)
    {
        if (_propagationWatches.Remove(key, out var cts))
        {
            cts.Cancel();
        }

        Propagation.Remove(key);
    }

    /// <summary>Stops every watch; used when the configuration changes under them and on dispose.</summary>
    internal void StopAllPropagationWatches()
    {
        foreach (var cts in _propagationWatches.Values)
        {
            cts.Cancel();
        }

        _propagationWatches.Clear();
        Propagation.Clear();
    }
}
