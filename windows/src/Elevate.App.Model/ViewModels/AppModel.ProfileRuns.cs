using Elevate.Core.Coordination;
using Elevate.Core.Models;

namespace Elevate.App.ViewModels;

public sealed partial class AppModel
{
    public Dictionary<RoleKey, string> DeactivationErrors { get; } = [];

    public Dictionary<RoleKey, ActivationIconPhase> DeactivationPhases { get; } = [];

    public IReadOnlyList<ProfileRun> ProfileRuns(Guid profileId) =>
        State.ProfileRuns.Where(r => r.ProfileId == profileId && r.Entries.Any(e => !e.Completed))
            .OrderByDescending(r => r.StartedAt).ToList();

    private readonly Lock _profileRunGate = new();

    private void RecordProfileOutcome(Guid? runId, ActivationOutcome outcome)
    {
        lock (_profileRunGate)
        {
            if (runId is null || State.ProfileRuns.Find(r => r.Id == runId) is not { } run
                || AssignmentOf(outcome.Result) is not { } assignment
                || run.Entries.Any(e => e.Assignment.RoleKey == assignment.RoleKey)) return;
            run.Entries.Add(new ProfileRun.Entry(assignment, SummaryName(outcome.RoleKey)));
            Persist();
        }
    }

    public string? DeactivationRefusal(ActiveAssignment expected, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        if (!Active.TryGetValue(expected.RoleKey, out var current)) return "No matching active assignment";
        if (!ProfileRun.Matches(expected, current)) return "Assignment changed · skipped";
        if (current.Status.Kind != AssignmentStatusKind.Active) return "Not active · skipped";
        if (current.EndDateTime <= now) return "Expired · skipped";
        if (Identity(current.RoleKey.IdentityId) is null) return "Account unavailable";
        var remaining = current.StartDateTime.AddMinutes(5) - now;
        if (remaining > TimeSpan.Zero) return $"Available in {Math.Ceiling(remaining.TotalSeconds)} s (5-minute minimum)";
        if (InFlight.Contains(current.RoleKey)) return "Operation in progress";
        if (!IsOnline) return "Offline";
        return null;
    }

    /// <summary>
    /// Only confirmed provider success completes an entry; retries leave successful entries alone.
    /// <paramref name="only"/> restricts one pass to the roles the user kept selected; entries left
    /// out are untouched and remain available to a later pass.
    /// </summary>
    public async Task<IReadOnlyDictionary<RoleKey, string?>> DeactivateProfileRunAsync(Guid runId, CancellationToken ct = default, IReadOnlySet<RoleKey>? only = null)
    {
        var results = new Dictionary<RoleKey, string?>();
        if (State.ProfileRuns.Find(r => r.Id == runId) is not { } run) return results;
        foreach (var entry in run.Entries.ToList())
        {
            if (entry.Completed || only?.Contains(entry.Assignment.RoleKey) == false) continue;
            var key = entry.Assignment.RoleKey;
            results[key] = await DeactivateAssignmentAsync(entry.Assignment, ct);
            if (results[key] is null)
            {
                var index = run.Entries.IndexOf(entry);
                if (index >= 0) run.Entries[index] = entry with { Completed = true };
                Persist();
            }
            Touch();
        }
        return results;
    }

    public bool ProfileAssignmentCompleted(ActiveAssignment expected) => State.ProfileRuns
        .SelectMany(r => r.Entries).Any(e => e.Assignment == expected && e.Completed);

    private void CompleteSkippedAssignment(ActiveAssignment expected)
    {
        foreach (var run in State.ProfileRuns)
            for (var i = 0; i < run.Entries.Count; i++)
                if (run.Entries[i].Assignment == expected) run.Entries[i] = run.Entries[i] with { Completed = true };
        Persist();
    }

    public async Task<string?> DeactivateAssignmentAsync(ActiveAssignment expected, CancellationToken ct = default)
    {
        DeactivationErrors.Remove(expected.RoleKey);
        var result = await DeactivateAssignmentCoreAsync(expected, ct);
        if (result is not null) DeactivationErrors[expected.RoleKey] = result;
        Touch();
        return result;
    }

    private async Task<string?> DeactivateAssignmentCoreAsync(ActiveAssignment expected, CancellationToken ct)
    {
        var generation = ConfigGeneration;
        var key = expected.RoleKey;
        if (ProfileAssignmentCompleted(expected)) return "Already handled · skipped";
        if (!IsOnline) return "Offline";
        if (InFlight.Contains(key)) return "Operation in progress";
        if (Identity(key.IdentityId) is not { } identity || Tenant(key.TenantKey) is not { } tenant)
            return "Account or tenant unavailable";
        if (Coordinator.Provider(key.Scope.Kind) is not { } provider) return "Provider unavailable";
        InFlight.Add(key);
        DeactivationPhases[key] = ActivationIconPhase.Working;
        Touch();
        try
        {
            // Cached state is a preview only. Re-read the exact instance before every write,
            // including each retry, so another client cannot leave a replacement vulnerable.
            var fresh = await provider.ActiveAssignmentsAsync(identity, tenant, ct);
            if (generation != ConfigGeneration) return "Account configuration changed";
            var visible = fresh.FirstOrDefault(a => a.RoleKey == key);
            if (visible is not null) Active[key] = visible;
            else if (Active.TryGetValue(key, out var cached) && ProfileRun.Matches(expected, cached)) Active.Remove(key);
            Touch();
            var now = DateTimeOffset.UtcNow;
            if (expected.EndDateTime <= now)
            {
                CompleteSkippedAssignment(expected);
                return "Expired · skipped";
            }
            if (expected.Status.Kind is AssignmentStatusKind.PendingApproval or AssignmentStatusKind.PendingProvisioning
                && expected.EndDateTime is null)
                return "Original activation interval could not be verified; deactivate this role individually";
            var assignment = fresh.FirstOrDefault(a => ProfileRun.Matches(expected, a));
            if (assignment is null)
            {
                if (expected.Status.Kind == AssignmentStatusKind.Active || fresh.Any(a => a.RoleKey == key && a.Status.Kind == AssignmentStatusKind.Active))
                {
                    CompleteSkippedAssignment(expected);
                    return "No matching active assignment · skipped";
                }
                return "No matching active assignment · refresh and retry";
            }
            if (assignment.Status.Kind != AssignmentStatusKind.Active) return "Not active · retry after activation";
            if (assignment.EndDateTime <= now) { CompleteSkippedAssignment(expected); return "Expired · skipped"; }
            var remaining = assignment.StartDateTime.AddMinutes(5) - now;
            if (remaining > TimeSpan.Zero) return $"Available in {Math.Ceiling(remaining.TotalSeconds)} s (5-minute minimum)";
            await Coordinator.DeactivateAsync(assignment, identity, ct);
            if (generation != ConfigGeneration) return "Account configuration changed";

            foreach (var run in State.ProfileRuns)
                for (var i = 0; i < run.Entries.Count; i++)
                    if (ProfileRun.Matches(run.Entries[i].Assignment, assignment))
                        run.Entries[i] = run.Entries[i] with { Completed = true };
            Persist();
            if (key.Scope.Kind == RoleScopeKind.Group) RefreshRolesAfterGroupChange([key.TenantKey]);
            DeactivationPhases[key] = ActivationIconPhase.Success;
            Touch();
            // Keep the active summary row mounted while the confirmed result morphs in place.
            await Task.Delay(TimeSpan.FromSeconds(ActivationIconPlayback.MorphDuration + .15));
            if (generation == ConfigGeneration && Active.TryGetValue(key, out var current) && ProfileRun.Matches(assignment, current)) Active.Remove(key);
            try { await RescheduleNotificationsAsync(); }
            catch (Exception ex) { LogError($"Notifications: {Describe(ex)}"); }
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var message = Describe(ex);
            TenantErrors[key.TenantKey] = message;
            LogError($"{SummaryName(key)}: {message}");
            return message;
        }
        finally { DeactivationPhases.Remove(key); InFlight.Remove(key); Touch(); }
    }
}
