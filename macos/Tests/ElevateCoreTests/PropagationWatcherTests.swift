import Foundation
import Testing
@testable import ElevateCore

@Suite struct PropagationWatcherTests {
    private let me = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t-home")
    private static let entraScope = RoleScope.entraDirectory(roleDefinitionId: "role-1", directoryScopeId: "/")
    private static let groupScope = RoleScope.group(groupId: "grp-1", accessId: .member)

    private func active(_ scope: RoleScope) -> ActiveAssignment {
        ActiveAssignment(roleKey: RoleKey(identityId: "id1", tenantId: "t1", scope: scope),
                         assignmentId: "a1", startDateTime: .now, endDateTime: nil, status: .active)
    }

    /// A provider whose probe answers from a queue, so a test can make propagation take a few rounds.
    actor ProbeState {
        private var answers: [EffectiveAccess]
        private(set) var probes = 0
        private var failure: PIMError?

        init(_ answers: [EffectiveAccess]) { self.answers = answers }
        func setFailure(_ e: PIMError?) { failure = e }
        func next() throws -> EffectiveAccess {
            probes += 1
            if let failure { throw failure }
            return answers.isEmpty ? .notYet : answers.removeFirst()
        }
    }

    struct ProbeProvider: PIMProvider {
        let kind: RoleScopeKind
        let scopes: [String] = []
        let state: ProbeState

        init(kind: RoleScopeKind, answers: [EffectiveAccess] = []) {
            self.kind = kind
            state = ProbeState(answers)
        }

        func effectiveAccess(_ assignment: ActiveAssignment, identity: Identity) async throws -> EffectiveAccess {
            try await state.next()
        }

        func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] { [] }
        func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] { [] }
        func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy { .manualDefault }
        func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
            throw PIMError.notEligible
        }
        func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {}
        func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws {}
    }

    /// Records the delays instead of sleeping, so the tests run in no time.
    actor Clock {
        private(set) var elapsed: TimeInterval = 0
        func advance(_ seconds: TimeInterval) { elapsed += seconds }
    }

    private func watcher(_ clock: Clock, _ providers: [any PIMProvider]) -> PropagationWatcher {
        PropagationWatcher(
            coordinator: ActivationCoordinator(providers: providers, tokens: FakeTokenProvider()),
            sleep: { await clock.advance($0) })
    }

    @Test func readyOnceTheProbeConfirms() async throws {
        let clock = Clock()
        let provider = ProbeProvider(kind: .entraDirectory, answers: [.notYet, .confirmed])

        let outcomes = try await watcher(clock, [provider]).wait(for: [active(Self.entraScope)], identities: [me])

        #expect(outcomes.count == 1)
        #expect(outcomes.first?.state == .ready)
        #expect(await provider.state.probes == 2)
    }

    @Test func reportsEachRoleAsItSettlesRatherThanOnlyAtTheEnd() async throws {
        let clock = Clock()
        let reported = Reported()
        let provider = ProbeProvider(kind: .entraDirectory, answers: [.confirmed])

        _ = try await watcher(clock, [provider])
            .wait(for: [active(Self.entraScope)], identities: [me], onChange: { outcome in reported.add(outcome) })

        #expect(reported.all.count == 1)
        #expect(reported.all.first?.state == .ready)
    }

    /// `onChange` is not async, so the collector guards itself.
    final class Reported: @unchecked Sendable {
        private let lock = NSLock()
        private var items: [PropagationOutcome] = []
        func add(_ o: PropagationOutcome) { lock.lock(); items.append(o); lock.unlock() }
        var all: [PropagationOutcome] { lock.lock(); defer { lock.unlock() }; return items }
    }

    @Test func unconfirmedOnceTheDeadlinePasses() async throws {
        let clock = Clock()
        let provider = ProbeProvider(kind: .entraDirectory)

        let outcomes = try await watcher(clock, [provider])
            .wait(for: [active(Self.entraScope)], identities: [me], deadline: 0)

        #expect(outcomes.count == 1)
        #expect(outcomes.first?.state == .unconfirmed)
        // The user is told what usually explains it, not just that it did not happen.
        #expect(outcomes.first?.detail == PropagationHints.likelyCause(.entraDirectory))
    }

    @Test func unobservableStopsProbingStraightAway() async throws {
        let clock = Clock()
        let provider = ProbeProvider(kind: .entraDirectory, answers: [.unknown("scoped role")])

        let outcomes = try await watcher(clock, [provider]).wait(for: [active(Self.entraScope)], identities: [me])

        #expect(outcomes.first?.state == .unobservable)
        #expect(await provider.state.probes == 1)
    }

    @Test func aFailedProbeIsUnobservableNotAFailedActivation() async throws {
        let clock = Clock()
        let provider = ProbeProvider(kind: .entraDirectory)
        await provider.state.setFailure(.network("offline"))

        let outcomes = try await watcher(clock, [provider]).wait(for: [active(Self.entraScope)], identities: [me])

        #expect(outcomes.first?.state == .unobservable)
    }

    @Test func oneUnobservableRoleDoesNotHoldUpTheOthers() async throws {
        let clock = Clock()
        let entra = ProbeProvider(kind: .entraDirectory, answers: [.unknown("scoped role")])
        let group = ProbeProvider(kind: .group, answers: [.notYet, .confirmed])

        let outcomes = try await watcher(clock, [entra, group])
            .wait(for: [active(Self.entraScope), active(Self.groupScope)], identities: [me])

        #expect(outcomes.count == 2)
        #expect(outcomes.first { $0.roleKey.scope.kind == .entraDirectory }?.state == .unobservable)
        #expect(outcomes.first { $0.roleKey.scope.kind == .group }?.state == .ready)
        // The Entra role was asked once and then left alone.
        #expect(await entra.state.probes == 1)
    }

    @Test func onlyActiveAssignmentsAreProbed() async throws {
        let clock = Clock()
        let provider = ProbeProvider(kind: .entraDirectory, answers: [.confirmed])
        let pending = ActiveAssignment(
            roleKey: RoleKey(identityId: "id1", tenantId: "t1", scope: Self.entraScope),
            assignmentId: "a1", startDateTime: .now, endDateTime: nil, status: .pendingApproval)

        let outcomes = try await watcher(clock, [provider]).wait(for: [pending], identities: [me])

        #expect(outcomes.isEmpty)
        #expect(await provider.state.probes == 0)
    }

    @Test func anAccountThatIsNotSignedInHereIsUnobservable() async throws {
        let clock = Clock()
        let provider = ProbeProvider(kind: .entraDirectory, answers: [.confirmed])

        let outcomes = try await watcher(clock, [provider]).wait(for: [active(Self.entraScope)], identities: [])

        #expect(outcomes.first?.state == .unobservable)
        #expect(await provider.state.probes == 0)
    }

    @Test func aRoleWithNoProviderIsUnobservable() async throws {
        let clock = Clock()
        let outcomes = try await watcher(clock, [ProbeProvider(kind: .group, answers: [.confirmed])])
            .wait(for: [active(Self.entraScope)], identities: [me])

        #expect(outcomes.first?.state == .unobservable)
    }

    @Test func probingWaitsBeforeTheFirstCallAndThenSettlesOnTheLongerInterval() async throws {
        let clock = Clock()
        let provider = ProbeProvider(kind: .entraDirectory, answers: [.notYet, .confirmed])

        _ = try await watcher(clock, [provider]).wait(for: [active(Self.entraScope)], identities: [me])

        #expect(await clock.elapsed == PropagationWatcher.defaultFirstInterval + PropagationWatcher.defaultMaxInterval)
    }
}
