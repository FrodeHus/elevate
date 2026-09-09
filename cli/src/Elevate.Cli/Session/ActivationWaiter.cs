using Elevate.Cli.Infrastructure;
using Elevate.Core.Models;
using Elevate.Core.Support;

namespace Elevate.Cli.Session;

/// <summary>
/// Polls the service until every named role reports active. Provisioning takes a moment after an
/// activation; approval takes as long as the approver takes, and a scheduled start as long as the
/// schedule says, so the caller chooses the timeout and gets one line per change of state for the
/// spinner. A request that was pending and then disappears was denied or withdrawn, which is a
/// failure, not something to wait out.
/// </summary>
public sealed class ActivationWaiter(ElevateSession session, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>What <c>--wait</c> allows for plain provisioning, which is rarely more than a minute.</summary>
    public static readonly TimeSpan ProvisioningTimeout = TimeSpan.FromMinutes(5);

    /// <summary>What <c>elevate run</c> allows by default, since it also sits through approvals.</summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(15);

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Returns once every key is active. Throws a <see cref="CliException"/> when the timeout
    /// passes first or a pending request is gone; the roles that did activate stay active either way.
    /// </summary>
    public async Task WaitAsync(IReadOnlyList<RoleKey> keys, TimeSpan timeout, Action<string>? report = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        var seenPending = new HashSet<RoleKey>();
        string? last = null;
        while (true)
        {
            var waiting = keys.Where(k => session.Active.GetValueOrDefault(k)?.Status.Kind != AssignmentStatusKind.Active).Distinct().ToList();
            if (waiting.Count == 0)
            {
                return;
            }

            foreach (var key in waiting)
            {
                var assignment = session.Active.GetValueOrDefault(key);
                if (assignment is null && seenPending.Contains(key))
                {
                    throw new CliException($"{session.RoleName(key)}: the request is no longer pending; it was denied, withdrawn or has expired.", ExitCodes.Failure);
                }

                if (assignment?.Status.Kind is AssignmentStatusKind.PendingApproval or AssignmentStatusKind.Scheduled)
                {
                    seenPending.Add(key);
                }

                if (assignment?.Status.Kind == AssignmentStatusKind.Failed)
                {
                    throw new CliException($"{session.RoleName(key)}: {assignment.Status.FailureReason ?? "the activation failed"}", ExitCodes.Failure);
                }
            }

            var message = Describe(waiting);
            if (message != last)
            {
                report?.Invoke(message);
                last = message;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                var names = string.Join(", ", waiting.Select(session.RoleName));
                throw new CliException($"Not active after {Countdown.Label(timeout)}: {names}. Whatever did activate stays active; 'elevate status' shows where things stand.", ExitCodes.Failure);
            }

            await _delay(Interval, ct).ConfigureAwait(false);
            await session.RefreshAsync(waiting.Select(k => k.TenantKey).Distinct().ToList(), ct).ConfigureAwait(false);
        }
    }

    /// <summary>One line naming what is being waited for, with the reason when it is not plain provisioning.</summary>
    internal string Describe(IReadOnlyList<RoleKey> waiting)
    {
        var now = DateTimeOffset.UtcNow;
        var approvals = waiting.Where(k => session.Active.GetValueOrDefault(k)?.Status.Kind == AssignmentStatusKind.PendingApproval).ToList();
        var scheduled = waiting.Where(k => session.Active.GetValueOrDefault(k)?.Status.Kind == AssignmentStatusKind.Scheduled).ToList();
        if (approvals.Count > 0)
        {
            var names = string.Join(", ", approvals.Select(session.RoleName));
            return $"Waiting for an approver to decide on {names}… (Ctrl+C stops waiting; the request stays open)";
        }

        if (scheduled.Count > 0)
        {
            var first = session.Active[scheduled[0]];
            return $"Waiting for {session.RoleName(scheduled[0])} to start (scheduled, in {Countdown.Until(first.StartDateTime, now)})…";
        }

        return waiting.Count == 1
            ? $"Waiting for {session.RoleName(waiting[0])} to become active…"
            : $"Waiting for {waiting.Count} roles to become active…";
    }
}
