import Foundation
import Observation
import ElevateCore

/// One profile run in progress, kept in the app model rather than a view: the panel closes whenever
/// it loses focus (an MFA step opens the browser) and the run must survive that. Never persisted.
@MainActor @Observable
final class ProfileRunSession {
    let profileId: UUID
    var items: [ProfilePlanItem]
    /// Roles the user unchecked for this run only; nothing is remembered onto the profile.
    var excluded: Set<RoleKey> = []
    var justification: String
    var ticketNumber = ""
    var ticketSystem = ""
    var scheduleStart = false
    var startAt = Date.now.addingTimeInterval(3600)
    var running = false
    var finished = false
    var resultsSeen = false
    var rowResults: [RoleKey: ActivationOutcome.Result] = [:]

    init(profileId: UUID, items: [ProfilePlanItem], justification: String) {
        self.profileId = profileId
        self.items = items
        self.justification = justification
    }

    var toActivate: [ProfilePlanItem] { items.filter { $0.disposition == .activate && !excluded.contains($0.roleKey) } }
    var needsTicket: Bool { toActivate.contains { $0.role?.policy.requiresTicket == true } }
    var justificationRequired: Bool { toActivate.contains { $0.role?.policy.requiresJustification == true } }
    var canSubmit: Bool {
        !running && !finished && !toActivate.isEmpty
            && (!justificationRequired || !justification.trimmingCharacters(in: .whitespaces).isEmpty)
            && (!needsTicket || !ticketNumber.trimmingCharacters(in: .whitespaces).isEmpty)
            // A start that has slipped into the past while the panel sat open is not submittable.
            && !(scheduleStart && startAt <= Date.now)
    }
}
