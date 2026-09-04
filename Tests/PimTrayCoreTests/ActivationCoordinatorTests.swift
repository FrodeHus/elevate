import Testing
import Foundation
@testable import PimTrayCore

@Suite struct ActivationCoordinatorTests {
    let identity = Identity(id: "id1", upn: "u@x", displayName: "U", homeTenantId: "t1")
    func key(_ tenant: String, _ role: String) -> RoleKey {
        RoleKey(identityId: "id1", tenantId: tenant, scope: .entraDirectory(roleDefinitionId: role, directoryScopeId: "/"))
    }

    @Test func activatesSingleRole() async throws {
        let provider = FakeProvider(kind: .entraDirectory)
        let c = ActivationCoordinator(providers: [provider], tokens: FakeTokenProvider())
        let outcomes = await c.activate([ActivationRequest(roleKey: key("t1", "r1"), duration: .seconds(3600), justification: "j")], identities: [identity]) { _ in }
        #expect(outcomes.count == 1)
        guard case .activated(let a) = outcomes[0].result else { Issue.record("expected activated"); return }
        #expect(a.status == .active)
        #expect(await provider.state.activated.count == 1)
    }

    @Test func bulkRunsSequentiallyWithinTenantAndReportsProgress() async throws {
        let provider = FakeProvider(kind: .entraDirectory)
        let c = ActivationCoordinator(providers: [provider], tokens: FakeTokenProvider())
        let requests = [
            ActivationRequest(roleKey: key("t1", "r1"), duration: .seconds(60), justification: "a"),
            ActivationRequest(roleKey: key("t1", "r2"), duration: .seconds(60), justification: "b"),
            ActivationRequest(roleKey: key("t2", "r1"), duration: .seconds(60), justification: "c"),
        ]
        let progress = ProgressSink()
        let outcomes = await c.activate(requests, identities: [identity]) { o in Task { await progress.add(o) } }
        #expect(outcomes.count == 3)
        #expect(Set(outcomes.map(\.roleKey)) == Set(requests.map(\.roleKey)))
        let order = await provider.state.order
        #expect(order.firstIndex(of: "t1:a")! < order.firstIndex(of: "t1:b")!)
        try await Task.sleep(for: .milliseconds(50))
        #expect(await progress.items.count == 3)
    }

    @Test func claimsChallengeTriggersInteractiveAndRetries() async throws {
        let provider = FakeProvider(kind: .entraDirectory)
        await provider.state.pushFailure(.claimsChallenge(#"{"access_token":{}}"#))
        let tokens = FakeTokenProvider()
        let c = ActivationCoordinator(providers: [provider], tokens: tokens)
        let outcomes = await c.activate([ActivationRequest(roleKey: key("t1", "r1"), duration: .seconds(60), justification: "j")], identities: [identity]) { _ in }
        guard case .activated = outcomes[0].result else { Issue.record("expected activated after retry"); return }
        let calls = await tokens.interactiveCalls
        #expect(calls.count == 1)
        #expect(calls[0].tenantId == "t1")
        #expect(calls[0].claims == #"{"access_token":{}}"#)
        #expect(calls[0].scopes == ["scope"])
    }

    @Test func secondFailureIsReportedNotRetriedForever() async throws {
        let provider = FakeProvider(kind: .entraDirectory)
        await provider.state.pushFailure(.claimsChallenge("{}"))
        await provider.state.pushFailure(.claimsChallenge("{}"))
        let c = ActivationCoordinator(providers: [provider], tokens: FakeTokenProvider())
        let outcomes = await c.activate([ActivationRequest(roleKey: key("t1", "r1"), duration: .seconds(60), justification: "j")], identities: [identity]) { _ in }
        #expect(outcomes[0].result == .failed(.claimsChallenge("{}")))
    }

    @Test func pendingApprovalAndPolicyViolationAreReported() async throws {
        let provider = FakeProvider(kind: .entraDirectory)
        await provider.state.pushFailure(.policyViolation("JustificationRule"))
        let c = ActivationCoordinator(providers: [provider], tokens: FakeTokenProvider())
        let outcomes = await c.activate([
            ActivationRequest(roleKey: key("t1", "r1"), duration: .seconds(60), justification: ""),
            ActivationRequest(roleKey: key("t1", "r2"), duration: .seconds(60), justification: "approve-me"),
        ], identities: [identity]) { _ in }
        let byKey = Dictionary(uniqueKeysWithValues: outcomes.map { ($0.roleKey, $0.result) })
        #expect(byKey[key("t1", "r1")] == .failed(.policyViolation("JustificationRule")))
        #expect(byKey[key("t1", "r2")] == .pendingApproval)
    }

    @Test func unknownIdentityOrProviderFails() async throws {
        let c = ActivationCoordinator(providers: [FakeProvider(kind: .entraDirectory)], tokens: FakeTokenProvider())
        let outcomes = await c.activate([
            ActivationRequest(roleKey: RoleKey(identityId: "ghost", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/")), duration: .seconds(60), justification: "j"),
            ActivationRequest(roleKey: RoleKey(identityId: "id1", tenantId: "t", scope: .group(groupId: "g", accessId: .member)), duration: .seconds(60), justification: "j"),
        ], identities: [identity]) { _ in }
        #expect(outcomes.allSatisfy { if case .failed = $0.result { true } else { false } })
    }

    @Test func deactivateRetriesOnInteractionRequired() async throws {
        let provider = FakeProvider(kind: .entraDirectory)
        await provider.state.pushFailure(.interactionRequired)
        let tokens = FakeTokenProvider()
        let c = ActivationCoordinator(providers: [provider], tokens: tokens)
        let a = ActiveAssignment(roleKey: key("t1", "r1"), assignmentId: "x", startDateTime: .now, endDateTime: nil, status: .active)
        try await c.deactivate(a, identity: identity)
        #expect(await provider.state.deactivated.count == 1)
        #expect(await tokens.interactiveCalls.count == 1)
    }
}

actor ProgressSink {
    var items: [ActivationOutcome] = []
    func add(_ o: ActivationOutcome) { items.append(o) }
}
