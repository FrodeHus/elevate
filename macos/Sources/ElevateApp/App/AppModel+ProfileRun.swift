import Foundation
import ElevateCore

@MainActor
extension AppModel {
    /// The session for this profile: the one in progress, else a fresh plan.
    func runSession(for id: UUID) -> ProfileRunSession {
        if let existing = runSessions[id] { return existing }
        let items = plan(for: id)
        let session = ProfileRunSession(profileId: id, items: items, justification: profile(id: id)?.lastJustification ?? "")
        runSessions[id] = session
        clearProgress(items.map(\.roleKey))
        return session
    }

    /// Plans the profile again, as the run window does when it is asked to run the profile anew.
    func resetRunSession(_ id: UUID) {
        let session = runSession(for: id)
        session.rowResults.removeAll()
        session.items = plan(for: id)
        if session.finished || session.justification.isEmpty { session.justification = profile(id: id)?.lastJustification ?? "" }
        session.finished = false
        session.resultsSeen = false
        // A stale toggle, a start time or unchecked roles from the last run must not carry over.
        session.excluded.removeAll()
        session.scheduleStart = false
        session.startAt = Date.now.addingTimeInterval(3600)
        clearProgress(session.items.map(\.roleKey))
    }

    func submitRun(_ id: UUID) async {
        let session = runSession(for: id)
        // Return in the reason field and the default button can both land here.
        guard session.canSubmit else { return }
        session.rowResults.removeAll()
        session.running = true
        let attempted = session.toActivate.count
        let ticket = session.needsTicket && !session.ticketNumber.isEmpty
            ? TicketInfo(number: session.ticketNumber, system: session.ticketSystem) : nil
        // Two minutes of headroom: a start the service sees as "now" would activate immediately.
        let start: Date? = session.scheduleStart ? max(session.startAt, Date.now.addingTimeInterval(120)) : nil
        // Unchecked roles never reach the model: no request, no run entry, no remembered duration.
        let chosen = session.items.filter { !session.excluded.contains($0.roleKey) }
        let outcomes = await runProfile(id: id, items: chosen, justification: session.justification, ticket: ticket,
                                        startDateTime: start)
        for outcome in outcomes { session.rowResults[outcome.roleKey] = outcome.result }
        // A role may have become active after the plan was made. The execution recheck skips it.
        for index in session.items.indices where session.items[index].disposition == .activate
            && !session.excluded.contains(session.items[index].roleKey) {
            let item = session.items[index]
            if !outcomes.contains(where: { $0.roleKey == item.roleKey }), let current = active[item.roleKey] {
                session.items[index] = ProfilePlanItem(roleKey: item.roleKey, role: item.role, duration: item.duration,
                    disposition: current.status == .active ? .alreadyActive : .pending)
            }
        }
        session.running = false
        session.finished = true
        if !panelIsOpen {
            await notifyOutcome(title: profile(id: id)?.name ?? "Profile", outcomes: outcomes, attempted: attempted)
        }
    }

    /// Opens the profile in the main view; one that already ran starts over with a fresh plan.
    func showProfile(_ id: UUID) {
        if runSession(for: id).finished { resetRunSession(id) }
        select(.profile(id))
    }

    /// Per-row progress of a run in flight, kept on the session so it shows with the panel closed too.
    func mirrorProgressIntoRunSessions() {
        for session in runSessions.values where session.running {
            for item in session.items { if let result = progress[item.roleKey] { session.rowResults[item.roleKey] = result } }
        }
    }

    func markRunSeen(_ id: UUID) { runSessions[id]?.resultsSeen = true }

    func panelClosed() { panelIsOpen = false }

    /// The reopen rule: results already seen are dropped; a draft stays; otherwise the panel
    /// comes back on a run that is still going or whose results nobody has read.
    func reopenProfileSelection() {
        if let id = selectedProfile, let session = runSessions[id] {
            if session.finished && session.resultsSeen {
                runSessions[id] = nil
                selectedProfile = nil
            }
        } else {
            selectedProfile = runSessions.first { $0.value.running || ($0.value.finished && !$0.value.resultsSeen) }?.key
        }
    }

    /// Drops the session and the selection of a profile that no longer exists.
    func forgetProfile(_ id: UUID) {
        runSessions[id] = nil
        if selectedProfile == id { selectedProfile = nil }
    }

    /// A profile is gone when it is neither the user's nor in the published document. A managed
    /// profile that resolves to nothing while roles load is still published, so it stays; a run
    /// in progress is never dropped.
    func forgetVanishedProfiles() {
        for id in Set(runSessions.keys).union(selectedProfile.map { [$0] } ?? [])
        where profile(id: id) == nil && !isManagedProfile(id) && runSessions[id]?.running != true {
            forgetProfile(id)
        }
    }
}
