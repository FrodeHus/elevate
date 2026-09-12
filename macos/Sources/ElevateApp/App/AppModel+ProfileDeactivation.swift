import Foundation
import ElevateCore

@MainActor
extension AppModel {
    func profileRuns(for profileId: UUID) -> [ProfileRun] {
        state.profileRuns.filter { $0.profileId == profileId && $0.entries.contains(where: { !$0.completed }) }
            .sorted { $0.startedAt > $1.startedAt }
    }

    /// Includes a run after its final result so an open results window can retain confirmation.
    func profileRunHistory(for profileId: UUID) -> [ProfileRun] {
        state.profileRuns.filter { $0.profileId == profileId && !$0.entries.isEmpty }
            .sorted { $0.startedAt > $1.startedAt }
    }

    func recordProfileOutcome(_ outcome: ActivationOutcome, runId: UUID) {
        guard let index = state.profileRuns.firstIndex(where: { $0.id == runId }) else { return }
        let assignment: ActiveAssignment
        switch outcome.result {
        case .activated(let a), .pendingApproval(let a), .scheduled(let a): assignment = a
        case .failed: return
        }
        guard !state.profileRuns[index].entries.contains(where: { $0.assignment.roleKey == assignment.roleKey }) else { return }
        state.profileRuns[index].entries.append(.init(assignment: assignment, displayName: summaryName(for: outcome.roleKey)))
        persist()
    }

    /// Names here describe shared access, not ownership: a skipped role in another profile remains
    /// owned only by the run that activated it.
    func sharedProfileNames(for key: RoleKey, excluding profileId: UUID) -> String {
        profiles.filter { $0.id != profileId && $0.entries.contains(where: { $0.roleKey == key }) }
            .map(\.name).joined(separator: ", ")
    }

    /// Re-read the provider immediately before each write. A stale panel must never make a later
    /// activation the target of an old run. Read failures remain retryable, never look like expiry.
    func deactivateProfileRun(_ runId: UUID) async {
        guard let run = state.profileRuns.first(where: { $0.id == runId }) else { return }
        let generation = configGeneration
        func report(_ key: RoleKey, _ phase: DeactivationPhase) {
            deactivationProgress[key] = phase
            profileDeactivationProgress[runId, default: [:]][key] = phase
        }
        for entry in run.entries where !entry.completed {
            if state.profileRuns.first(where: { $0.id == runId })?.entries.first(where: { $0.assignment.roleKey == entry.assignment.roleKey })?.completed == true { continue }
            guard generation == configGeneration else { return }
            let key = entry.assignment.roleKey
            guard !inFlight.contains(key) else { continue }
            guard isOnline, let identity = identity(key.identityId), let tenant = tenant(key.tenantKey),
                  let provider = coordinator.provider(for: key.scope.kind) else {
                report(key, .blocked("Account unavailable or offline"))
                continue
            }
            // Hold the row while the verification read yields to other requests.
            inFlight.insert(key)
            report(key, .working)
            do {
                let assignments = try await provider.activeAssignments(identity: identity, tenant: tenant)
                guard generation == configGeneration else { inFlight.remove(key); return }
                let candidates = assignments.filter { $0.roleKey == key }
                let current = candidates.first { entry.matches($0) } ?? candidates.first
                let disposition = entry.disposition(current: current, now: .now)
                switch disposition {
                case .ready:
                    active[key] = current
                    inFlight.remove(key)
                    let result = await deactivate(key)
                    guard generation == configGeneration else { return }
                    report(key, result)
                    if result == .succeeded { completeProfileEntry(runId: runId, key: key) }
                case .completed:
                    inFlight.remove(key)
                case .skipped(let reason):
                    // Skips are not successful deactivations: show static explanatory text.
                    report(key, .blocked(reason))
                    active[key] = current
                    completeProfileEntry(runId: runId, key: key)
                    inFlight.remove(key)
                case .blocked(let reason):
                    report(key, .blocked(reason))
                    active[key] = current
                    inFlight.remove(key)
                }
            } catch {
                inFlight.remove(key)
                guard generation == configGeneration else { return }
                report(key, .failed((error as? PIMError)?.userMessage ?? error.localizedDescription))
            }
        }
    }

    private func completeProfileEntry(runId: UUID, key: RoleKey) {
        guard let index = state.profileRuns.firstIndex(where: { $0.id == runId }),
              let row = state.profileRuns[index].entries.firstIndex(where: { $0.assignment.roleKey == key }) else { return }
        state.profileRuns[index].entries[row].completed = true
        persist()
    }
}
