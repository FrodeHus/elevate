using Elevate.Core.Coordination;
using Elevate.Core.Models;

namespace Elevate.App.ViewModels;

/// <summary>Profile runs that live in the model so they survive the flyout hiding. Port of <c>AppModel+ProfileRun.swift</c>.</summary>
public sealed partial class AppModel
{
    private readonly Dictionary<Guid, ProfileRunSession> _runSessions = [];

    public IReadOnlyDictionary<Guid, ProfileRunSession> RunSessions => _runSessions;

    /// <summary>True while the flyout is shown; set by <see cref="PanelOpened"/>, cleared by <see cref="PanelClosed"/>.</summary>
    public bool PanelIsOpen { get; private set; }

    /// <summary>Profiles whose run window is open; the window adds and removes itself.</summary>
    public ISet<Guid> RunWindowsOpen { get; } = new HashSet<Guid>();

    /// <summary>The session for this profile: the one in progress, else a fresh plan.</summary>
    public ProfileRunSession RunSession(Guid id)
    {
        if (_runSessions.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var items = Plan(id);
        var session = new ProfileRunSession(id, items, Profile(id)?.LastJustification ?? string.Empty);
        _runSessions[id] = session;
        ClearProgress(items.Select(i => i.RoleKey));
        return session;
    }

    /// <summary>Plans the profile again, as the run window does when it is asked to run the profile anew.</summary>
    public void ResetRunSession(Guid id)
    {
        var session = RunSession(id);
        session.MutableRowResults.Clear();
        session.Items = Plan(id);
        if (session.Finished || session.Justification.Length == 0)
        {
            session.Justification = Profile(id)?.LastJustification ?? string.Empty;
        }

        session.Finished = false;
        session.ResultsSeen = false;
        session.Failure = null;
        // A stale toggle, a start time or unchecked roles from the last run must not carry over.
        session.Excluded.Clear();
        session.ScheduleStart = false;
        session.StartAt = DateTimeOffset.Now.AddHours(1);
        ClearProgress(session.Items.Select(i => i.RoleKey));
        Touch();
    }

    public async Task SubmitRunAsync(Guid id)
    {
        var session = RunSession(id);
        // Return in the reason field and the default button can both land here.
        if (!session.CanSubmit(DateTimeOffset.Now) || !IsOnline)
        {
            return;
        }

        session.MutableRowResults.Clear();
        session.Failure = null;
        session.Running = true;
        Touch();
        var attempted = session.ToActivate.Count;
        var ticket = session.NeedsTicket && !string.IsNullOrWhiteSpace(session.TicketNumber)
            ? new TicketInfo(session.TicketNumber.Trim(), session.TicketSystem.Trim())
            : null;
        // Two minutes of headroom: a start the service sees as "now" would activate immediately.
        var floor = DateTimeOffset.Now.AddMinutes(2);
        DateTimeOffset? start = session.ScheduleStart ? (session.StartAt > floor ? session.StartAt : floor) : null;
        // Unchecked roles never reach the model: no request, no run entry, no remembered duration.
        var chosen = session.Items.Where(i => !session.Excluded.Contains(i.RoleKey)).ToList();
        IReadOnlyList<ActivationOutcome> outcomes = [];
        try
        {
            outcomes = await RunProfileAsync(id, chosen, session.Justification.Trim(), ticket, start);
        }
        catch (Exception ex)
        {
            // Cancellations too: a session left Running is exempt from every drop rule and never ends.
            session.Failure = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            LogError($"Profile run: {Describe(ex)}");
        }
        finally
        {
            foreach (var outcome in outcomes)
            {
                session.MutableRowResults[outcome.RoleKey] = outcome.Result;
            }

            // A role may have become active after the plan was made; the execution recheck skipped it.
            session.Items = [.. session.Items.Select(item =>
                item.Disposition == ProfilePlanDisposition.Activate && !session.Excluded.Contains(item.RoleKey)
                && outcomes.All(o => o.RoleKey != item.RoleKey) && Active.TryGetValue(item.RoleKey, out var current)
                    ? item with { Disposition = current.Status.Kind == AssignmentStatusKind.Active ? ProfilePlanDisposition.AlreadyActive : ProfilePlanDisposition.Pending }
                    : item)];
            session.Running = false;
            session.Finished = true;
            Touch();
        }

        // A failed run posts no toast: empty outcomes would read as a success. The failure stays on
        // the session, and the unseen result reopens the panel on it.
        if (session.Failure is null && !PanelIsOpen && !RunWindowsOpen.Contains(id))
        {
            await NotifyOutcomeAsync(Profile(id)?.Name ?? "Profile", outcomes, attempted);
        }
    }

    /// <summary>Opens the profile in the main view; one that already ran starts over with a fresh plan.</summary>
    public void ShowProfile(Guid id)
    {
        if (RunSession(id).Finished)
        {
            ResetRunSession(id);
        }

        Select(new PanelSelection.Profile(id));
    }

    /// <summary>Only counts when the results can be on screen: the open flyout on this profile, or its run window.</summary>
    public void MarkRunSeen(Guid id)
    {
        var onScreen = (PanelIsOpen && SelectedProfile == id) || RunWindowsOpen.Contains(id);
        if (onScreen && _runSessions.TryGetValue(id, out var session))
        {
            session.ResultsSeen = true;
            Touch();
        }
    }

    public void PanelClosed()
    {
        PanelIsOpen = false;
        Touch();
    }

    /// <summary>Per-row progress of a run in flight, kept on the session so it exists with the flyout hidden too.</summary>
    private void MirrorProgressIntoRunSessions()
    {
        foreach (var session in _runSessions.Values.Where(s => s.Running))
        {
            foreach (var item in session.Items)
            {
                if (Progress.TryGetValue(item.RoleKey, out var result))
                {
                    session.MutableRowResults[item.RoleKey] = result;
                }
            }
        }
    }

    /// <summary>
    /// The reopen rule: results already seen are dropped; a draft stays; otherwise the panel comes
    /// back on a run that is still going or whose results nobody has read.
    /// </summary>
    private void ReopenProfileSelection()
    {
        if (SelectedProfile is { } id && _runSessions.TryGetValue(id, out var session))
        {
            // A run window still showing the results keeps them until it closes.
            if (session.Finished && session.ResultsSeen && !RunWindowsOpen.Contains(id))
            {
                _runSessions.Remove(id);
                SetSelectedProfile(null);
            }
        }
        else
        {
            var next = _runSessions.FirstOrDefault(p => p.Value.Running || (p.Value.Finished && !p.Value.ResultsSeen));
            SetSelectedProfile(next.Value is null ? null : next.Key);
        }
    }

    /// <summary>Drops the session and the selection of a profile that no longer exists.</summary>
    private void ForgetProfile(Guid id)
    {
        _runSessions.Remove(id);
        if (SelectedProfile == id)
        {
            SetSelectedProfile(null);
        }
    }

    /// <summary>
    /// A profile is gone when it is neither the user's nor in the published document. A managed
    /// profile that resolves to nothing while roles load is still published, so it stays; a run in
    /// progress is never dropped.
    /// </summary>
    private void ForgetVanishedProfiles()
    {
        var ids = _runSessions.Keys.ToList();
        if (SelectedProfile is { } selected && !ids.Contains(selected))
        {
            ids.Add(selected);
        }

        foreach (var id in ids)
        {
            if (Profile(id) is null && !IsManagedProfile(id) && !(_runSessions.TryGetValue(id, out var s) && s.Running))
            {
                ForgetProfile(id);
            }
        }
    }
}
