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
    /// Roles being probed, and how far they have got. A role is absent once it is ready, or once
    /// there was nothing to observe: the row is then a plain active row, which is what it was
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
    /// Records where a role got to. Ready and Unobservable both leave a plain active row — the
    /// first because it is ready, the second because there was never anything to say — but only
    /// Ready is worth a notification.
    /// </summary>
    private void Settle(int generation, PropagationOutcome outcome)
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

        switch (outcome.State)
        {
            case PropagationState.Ready:
                Propagation.Remove(outcome.RoleKey);
                _ = NotifyReadyAsync(outcome.RoleKey);
                break;
            case PropagationState.Unobservable:
                Propagation.Remove(outcome.RoleKey);
                break;
            default:
                Propagation[outcome.RoleKey] = outcome.State;
                break;
        }

        Touch();
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
