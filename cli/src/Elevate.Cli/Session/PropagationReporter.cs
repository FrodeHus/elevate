using Elevate.Cli.Commands;
using Elevate.Cli.Infrastructure;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Spectre.Console;

namespace Elevate.Cli.Session;

/// <summary>
/// Runs the effective-access check after an activation has settled, and says what came of it.
/// <para>
/// PIM reports an assignment active as soon as it writes it; the access behind it arrives minutes
/// later, which is why "I activated and nothing happened" is the most common complaint about PIM.
/// <c>--wait</c> and <c>elevate run</c> therefore wait for the access, not for the record — a role
/// that is merely active is not something a command can be handed.
/// </para>
/// </summary>
public static class PropagationReporter
{
    /// <summary>
    /// Probes <paramref name="keys"/> until each is usable, unobservable, or out of time. Returns
    /// false when at least one role never confirmed, so a caller can decide whether that is fatal.
    /// </summary>
    public static async Task<bool> VerifyAsync(
        CommandContext context,
        ElevateSession session,
        IReadOnlyList<RoleKey> keys,
        TimeSpan? deadline,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(keys);

        var assignments = keys.Distinct()
            .Select(session.Active.GetValueOrDefault)
            .OfType<ActiveAssignment>()
            .Where(a => a.Status.Kind == AssignmentStatusKind.Active)
            .ToList();

        if (assignments.Count == 0)
        {
            return true;
        }

        var watcher = new PropagationWatcher(session.Coordinator);
        var outcomes = await context.Output.StatusAsync(
            Title(session, assignments),
            () => watcher.WaitAsync(assignments, session.Identities, deadline, null, ct)).ConfigureAwait(false);

        var confirmed = true;
        foreach (var outcome in outcomes)
        {
            var name = Markup.Escape(session.RoleName(outcome.RoleKey));
            switch (outcome.State)
            {
                case PropagationState.Ready:
                    context.Output.Note($"{name}: in effect.");
                    break;
                case PropagationState.Unconfirmed:
                    confirmed = false;
                    context.Output.Warn($"{name}: active, but still not in effect. {Markup.Escape(outcome.Detail ?? "")}");
                    break;
                case PropagationState.Unobservable:
                    context.Output.Note($"{name}: active; Elevate cannot confirm it is in effect. {Markup.Escape(outcome.Detail ?? "")}");
                    break;
                default:
                    break;
            }
        }

        return confirmed;
    }

    /// <summary>The spinner line, naming how long this kind of role usually takes so the wait reads as expected.</summary>
    private static string Title(ElevateSession session, IReadOnlyList<ActiveAssignment> assignments)
    {
        var typical = Countdown.Label(assignments.Max(a => PropagationHints.Typical(a.RoleKey.Scope.Kind)));
        return assignments.Count == 1
            ? $"Checking that {session.RoleName(assignments[0].RoleKey)} is in effect (usually under {typical})…"
            : $"Checking that {assignments.Count} roles are in effect (usually under {typical})…";
    }
}
