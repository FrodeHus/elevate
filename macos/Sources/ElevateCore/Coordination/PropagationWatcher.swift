import Foundation

/// Where one role has got to between "PIM says active" and "it works".
public enum PropagationState: Hashable, Sendable {
    /// Active, probed, not usable yet. Keep waiting.
    case propagating
    /// A probe saw the access. This is the state worth telling the user about.
    case ready
    /// Still not usable when the deadline passed. Say so, and say what usually explains it.
    case unconfirmed
    /// Nothing here can be observed from the client — a directory-scoped Entra role, group
    /// ownership, a sign-in method whose token cannot be read. Not a failure; the role is as active
    /// as it was, we simply cannot add anything to that.
    case unobservable
}

/// One role's propagation verdict. `detail` explains the two states that are not plain progress.
public struct PropagationOutcome: Hashable, Sendable {
    public let roleKey: RoleKey
    public let state: PropagationState
    public let detail: String?

    public init(roleKey: RoleKey, state: PropagationState, detail: String? = nil) {
        self.roleKey = roleKey
        self.state = state
        self.detail = detail
    }

    /// Whether the caller can stop waiting on this role, whatever the answer was.
    public var isSettled: Bool { state != .propagating }
}

/// Turns "the service says active" into "the access works", by asking each role's provider until it
/// answers yes or the deadline passes. Roles are independent: one that cannot be observed does not
/// hold up the rest, and one that is ready is reported the moment it is, so a caller can notify.
public final class PropagationWatcher: Sendable {
    /// How long to wait before the first probe: a round trip costs more than it buys at t=0.
    public static let defaultFirstInterval: TimeInterval = 5
    /// The gap between probes settles here. Propagation is measured in minutes; polling faster only costs calls.
    public static let defaultMaxInterval: TimeInterval = 20

    public let firstInterval: TimeInterval
    public let maxInterval: TimeInterval
    private let coordinator: ActivationCoordinator
    private let sleep: @Sendable (TimeInterval) async throws -> Void

    public init(
        coordinator: ActivationCoordinator,
        firstInterval: TimeInterval = PropagationWatcher.defaultFirstInterval,
        maxInterval: TimeInterval = PropagationWatcher.defaultMaxInterval,
        sleep: @escaping @Sendable (TimeInterval) async throws -> Void = { try await Task.sleep(for: .seconds($0)) }
    ) {
        self.coordinator = coordinator
        self.firstInterval = firstInterval
        self.maxInterval = maxInterval
        self.sleep = sleep
    }

    /// Probes until every role has settled. `deadline` defaults to the longest
    /// `PropagationHints.deadline` among the roles. `onChange` fires once per role, when it settles.
    @discardableResult
    public func wait(
        for assignments: [ActiveAssignment],
        identities: [Identity],
        deadline: TimeInterval? = nil,
        onChange: (@Sendable (PropagationOutcome) -> Void)? = nil
    ) async throws -> [PropagationOutcome] {
        let identityById = Dictionary(identities.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        var pending = assignments.filter { $0.status == .active }
        var seen = Set<RoleKey>()
        pending = pending.filter { seen.insert($0.roleKey).inserted }

        var settled: [RoleKey: PropagationOutcome] = [:]
        for assignment in pending where identityById[assignment.roleKey.identityId] == nil {
            let outcome = PropagationOutcome(
                roleKey: assignment.roleKey, state: .unobservable, detail: "That account is not signed in here.")
            onChange?(outcome)
            settled[assignment.roleKey] = outcome
        }
        pending.removeAll { settled[$0.roleKey] != nil }
        guard !pending.isEmpty else { return Array(settled.values) }

        let until = Date.now.addingTimeInterval(deadline ?? PropagationHints.deadline(pending.map(\.roleKey.scope.kind)))
        var interval = firstInterval
        while !pending.isEmpty {
            try await sleep(interval)
            interval = maxInterval

            var answers: [(ActiveAssignment, EffectiveAccess)] = []
            for assignment in pending {
                guard let identity = identityById[assignment.roleKey.identityId] else { continue }
                answers.append((assignment, await probe(assignment, identity: identity)))
            }

            let expired = Date.now >= until
            for (assignment, access) in answers {
                if let outcome = Self.verdict(assignment.roleKey, access: access, expired: expired) {
                    onChange?(outcome)
                    settled[assignment.roleKey] = outcome
                }
            }
            pending.removeAll { settled[$0.roleKey] != nil }
        }
        return Array(settled.values)
    }

    /// The outcome a probe settles on, or nil while the role is still worth asking about.
    static func verdict(_ key: RoleKey, access: EffectiveAccess, expired: Bool) -> PropagationOutcome? {
        switch access {
        case .confirmed:
            PropagationOutcome(roleKey: key, state: .ready)
        case let .unknown(reason):
            PropagationOutcome(roleKey: key, state: .unobservable, detail: reason)
        case .notYet:
            expired
                ? PropagationOutcome(roleKey: key, state: .unconfirmed, detail: PropagationHints.likelyCause(key.scope.kind))
                : nil
        }
    }

    private func probe(_ assignment: ActiveAssignment, identity: Identity) async -> EffectiveAccess {
        guard let provider = coordinator.provider(for: assignment.roleKey.scope.kind) else {
            return .unknown("No provider for this kind of role.")
        }
        do {
            // A refused or failed probe is not a failed activation: the role is active either way.
            return try await provider.effectiveAccess(assignment, identity: identity)
        } catch let error as PIMError {
            return .unknown(error.userMessage)
        } catch {
            return .unknown(error.localizedDescription)
        }
    }
}
