import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// The panel's side of #181: a row is not "ready" because PIM says active.
@MainActor
struct AppModelPropagationTests {
    private static let globalAdmin = "62e90394-69f5-4237-9190-012177145e10"
    private static var entraKey: RoleKey {
        Sample.key(.entraDirectory(roleDefinitionId: globalAdmin, directoryScopeId: "/"))
    }

    /// A model whose probe answers from `token`'s claims, paced so a test does not sit through the real interval.
    private func makePropagationModel(token: String?, notifier: any ExpiryNotifying = NoopNotifier()) async -> AppModel {
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]
        let tokens = FakeTokenProvider()
        await tokens.setRefreshedToken(token)
        let model = await makeModel(state: state, online: true, tokens: tokens, notifier: notifier)
        model.propagationFirstInterval = 0.001
        model.propagationMaxInterval = 0.001
        return model
    }

    private func activated(_ key: RoleKey, started: Date = .now) -> ActivationOutcome {
        ActivationOutcome(roleKey: key, result: .activated(ActiveAssignment(
            roleKey: key, assignmentId: "a1", startDateTime: started,
            endDateTime: Date().addingTimeInterval(3600), status: .active)))
    }

    /// Waits for the watch on `key` to settle, rather than for a fixed time.
    private func settled(_ model: AppModel, _ key: RoleKey) async throws {
        for _ in 0..<200 {
            if model.propagation[key] != .propagating { return }
            try await Task.sleep(for: .milliseconds(25))
        }
        Issue.record("The probe on \(key.scope.kind) never settled.")
    }

    @Test func aRowIsPropagatingTheMomentItIsActivated() async throws {
        // No token to read, so the probe cannot confirm — but the row must say so before it asks.
        let model = await makePropagationModel(token: nil)
        defer { cleanup(model) }
        let key = Self.entraKey
        model.active[key] = Sample.assignment(key)

        model.watchPropagation([activated(key)])

        #expect(model.propagation[key] == .propagating)
        #expect(model.propagationNote(for: key) == "propagating (~3 min)")
        #expect(model.propagationTooltip(for: key)?.contains("checking") == true)
        #expect(model.isPropagating(key))
    }

    @Test func aConfirmedProbeClearsTheRowAndNotifies() async throws {
        let notifier = RecordingNotifier()
        let model = await makePropagationModel(token: Jwt.withRoles(Self.globalAdmin), notifier: notifier)
        defer { cleanup(model) }
        let key = Self.entraKey
        // Started well before now, so it is not the "nobody waited for this" case.
        let started = Date().addingTimeInterval(-60)
        model.active[key] = ActiveAssignment(roleKey: key, assignmentId: "a1", startDateTime: started,
                                             endDateTime: nil, status: .active)

        model.watchPropagation([activated(key, started: started)])
        try await settled(model, key)

        #expect(model.propagation[key] == nil)
        #expect(model.propagationNote(for: key) == nil)
        // The notification is posted from a detached task; give it a turn.
        try await Task.sleep(for: .milliseconds(50))
        #expect(await notifier.notifications.first?.title.contains("is ready") == true)
    }

    @Test func aRoleReadyStraightAwayIsNotWorthANotification() async throws {
        let notifier = RecordingNotifier()
        let model = await makePropagationModel(token: Jwt.withRoles(Self.globalAdmin), notifier: notifier)
        defer { cleanup(model) }
        let key = Self.entraKey
        model.active[key] = Sample.assignment(key)

        model.watchPropagation([activated(key)])
        try await settled(model, key)
        try await Task.sleep(for: .milliseconds(50))

        #expect(model.propagation[key] == nil)
        #expect(await notifier.notifications.isEmpty)
    }

    @Test func anUnobservableRoleGoesBackToAPlainActiveRow() async throws {
        // A role scoped to an administrative unit is not carried in a token, so there is nothing to say.
        let model = await makePropagationModel(token: Jwt.withRoles())
        defer { cleanup(model) }
        let key = Sample.key(.entraDirectory(roleDefinitionId: Self.globalAdmin, directoryScopeId: "/administrativeUnits/au1"))
        model.active[key] = Sample.assignment(key)

        model.watchPropagation([activated(key)])
        try await settled(model, key)

        #expect(model.propagation[key] == nil)
        #expect(model.propagationNote(for: key) == nil)
    }

    @Test func deactivatingWhileItPropagatesDropsTheWatch() async throws {
        let model = await makePropagationModel(token: nil)
        defer { cleanup(model) }
        let key = Self.entraKey
        model.active[key] = Sample.assignment(key)
        model.watchPropagation([activated(key)])

        model.stopWatchingPropagation(key)

        #expect(model.propagation[key] == nil)
        #expect(model.propagationWatches[key]?.task == nil)
    }

    @Test func onlyActivatedOutcomesAreWatched() async throws {
        let model = await makePropagationModel(token: nil)
        defer { cleanup(model) }
        let key = Self.entraKey
        let pending = ActiveAssignment(roleKey: key, assignmentId: "a1", startDateTime: .now,
                                       endDateTime: nil, status: .pendingApproval)

        model.watchPropagation([ActivationOutcome(roleKey: key, result: .pendingApproval(pending))])

        #expect(model.propagation.isEmpty)
    }

    @Test func aRoleThatIsNotBeingWatchedIsNotPropagating() async throws {
        let model = await makePropagationModel(token: nil)
        defer { cleanup(model) }

        #expect(model.propagation.isEmpty)
        #expect(model.propagationNote(for: Self.entraKey) == nil)
        #expect(model.propagationTooltip(for: Self.entraKey) == nil)
        #expect(!model.isPropagating(Self.entraKey))
    }

    @Test func theUnconfirmedNoteNamesWhatToDoAboutIt() async throws {
        let model = await makePropagationModel(token: nil)
        defer { cleanup(model) }
        let key = Self.entraKey
        model.propagation[key] = .unconfirmed

        #expect(model.propagationNote(for: key) == "not in effect yet")
        #expect(model.propagationTooltip(for: key) == PropagationHints.likelyCause(.entraDirectory))
    }
}
