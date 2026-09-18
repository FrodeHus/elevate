import Foundation
import ElevateCore

/// One running probe, with an id so a watch that finishes cannot clear the one that replaced it.
@MainActor
struct PropagationWatch {
    let id: UUID
    let task: Task<Void, Never>
}

/// The gap between "PIM says active" and "the access works".
///
/// A row goes green the moment the request settles, which is the service's own view and not
/// evidence of anything — the access follows minutes later. After an activation the model probes
/// each role until it is genuinely in effect, and the row says so until then.
@MainActor
extension AppModel {
    /// What the row says under a role that is active but not usable yet, or nil for a plain active row.
    func propagationNote(for key: RoleKey) -> String? {
        switch propagation[key] {
        case .propagating: "propagating (~\(Countdown.label(PropagationHints.typical(key.scope.kind))))"
        case .unconfirmed: "not in effect yet"
        default: nil
        }
    }

    /// The tooltip behind `propagationNote`: what to expect, or what to do about it.
    func propagationTooltip(for key: RoleKey) -> String? {
        switch propagation[key] {
        case .propagating:
            "PIM has recorded the activation. Elevate is checking that the access actually works; "
                + "the countdown has already started."
        case .unconfirmed: PropagationHints.likelyCause(key.scope.kind)
        default: nil
        }
    }

    /// Whether the row should read as active-but-not-ready.
    func isPropagating(_ key: RoleKey) -> Bool { propagationNote(for: key) != nil }

    /// Starts probing everything `outcomes` activated. Roles already being watched are left alone,
    /// so a second activation of the same role does not run two probes against it.
    func watchPropagation(_ outcomes: [ActivationOutcome]) {
        for outcome in outcomes {
            guard case let .activated(assignment) = outcome.result,
                  assignment.status == .active,
                  propagationWatches[assignment.roleKey] == nil else { continue }
            watch(assignment)
        }
    }

    private func watch(_ assignment: ActiveAssignment) {
        let key = assignment.roleKey
        let generation = configGeneration
        propagation[key] = .propagating
        let id = UUID()
        propagationWatches[key] = PropagationWatch(id: id, task: Task { [weak self] in
            guard let self else { return }
            let watcher = PropagationWatcher(
                coordinator: coordinator,
                firstInterval: propagationFirstInterval, maxInterval: propagationMaxInterval)
            // A cancelled watch — deactivated, signed out, configuration changed — reports nothing.
            try? await watcher.wait(for: [assignment], identities: state.identities) { outcome in
                Task { @MainActor [weak self] in self?.settle(outcome, generation: generation) }
            }
            finish(key, id: id)
        })
    }

    /// Forgets a finished watch, unless the role was activated again and a newer one replaced it.
    private func finish(_ key: RoleKey, id: UUID) {
        guard propagationWatches[key]?.id == id else { return }
        propagationWatches[key] = nil
    }

    /// Records where a role got to. `.ready` and `.unobservable` both leave a plain active row — the
    /// first because it is ready, the second because there was never anything to say — but only
    /// `.ready` is worth a notification.
    private func settle(_ outcome: PropagationOutcome, generation: Int) {
        guard generation == configGeneration, propagation[outcome.roleKey] != nil else { return }
        // Expired, deactivated elsewhere, or dropped by a refresh while the probe ran: there is no
        // row left to say anything on, so the entry goes rather than lingering.
        guard active[outcome.roleKey] != nil else {
            propagation[outcome.roleKey] = nil
            return
        }
        switch outcome.state {
        case .ready:
            propagation[outcome.roleKey] = nil
            Task { await notifyReady(outcome.roleKey) }
        case .unobservable:
            propagation[outcome.roleKey] = nil
        default:
            propagation[outcome.roleKey] = outcome.state
        }
    }

    /// A role ready inside this is one nobody waited for, so it passes without a notification.
    /// Slightly over the watcher's first interval, which is the soonest a probe can confirm.
    static let quietPropagation: TimeInterval = 8

    /// Tells the user the role is usable now. The point of the probe is that they switched away
    /// while it ran, so this is the only way they learn the right moment has come — but a role that
    /// was ready almost at once was never worth interrupting them for.
    private func notifyReady(_ key: RoleKey) async {
        guard let assignment = active[key],
              Date.now.timeIntervalSince(assignment.startDateTime) >= Self.quietPropagation else { return }
        let tenantName = tenant(key.tenantKey)?.displayName ?? key.tenantId
        await notifier.notify(title: "\(summaryName(for: key)) is ready",
                              body: "The access is in effect in \(tenantName).")
    }

    /// Stops watching a role and drops its state. Called when it is deactivated or its account goes
    /// away: a propagating row whose assignment no longer exists must not linger.
    func stopWatchingPropagation(_ key: RoleKey) {
        propagationWatches.removeValue(forKey: key)?.task.cancel()
        propagation[key] = nil
    }

    /// Stops every watch; used when the configuration changes under them.
    func stopAllPropagationWatches() {
        for watch in propagationWatches.values { watch.task.cancel() }
        propagationWatches.removeAll()
        propagation.removeAll()
    }
}
