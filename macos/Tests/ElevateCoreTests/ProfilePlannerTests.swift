import Testing
import Foundation
@testable import ElevateCore

@Suite struct ProfilePlannerTests {
    let k1 = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r1", directoryScopeId: "/"))
    let k2 = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r2", directoryScopeId: "/"))
    let k3 = RoleKey(identityId: "i", tenantId: "t", scope: .group(groupId: "g", accessId: .member))
    let k4 = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "gone", directoryScopeId: "/"))
    var policy: RolePolicy { RolePolicy(defaultDuration: .seconds(3600), maximumDuration: .seconds(4 * 3600), requiresJustification: true, requiresTicket: false, requiresMFA: false, requiresApproval: false) }
    func role(_ k: RoleKey) -> EligibleRole { EligibleRole(key: k, displayName: "R", source: .discovered, policy: policy) }
    func assignment(_ k: RoleKey, _ s: ActiveAssignment.Status) -> ActiveAssignment { ActiveAssignment(roleKey: k, assignmentId: "a", startDateTime: .now, endDateTime: nil, status: s) }
    var loaded: Set<TenantKey> { [TenantKey(identityId: "i", tenantId: "t")] }

    @Test func durationPrecedenceAndCap() {
        let profile = ActivationProfile(name: "p", entries: [
            .init(roleKey: k1, lastDuration: .seconds(8 * 3600)),   // capped to the 4 h maximum
            .init(roleKey: k2, lastDuration: nil),                   // falls to memory
            .init(roleKey: k3, lastDuration: nil),                   // falls to policy default
        ])
        let roles = [k1: role(k1), k2: role(k2), k3: role(k3)]
        let memory = [k2: RoleMemory(roleKey: k2, justification: "x", lastDuration: .seconds(1800))]
        let items = ProfilePlanner.plan(profile, roles: roles, active: [:], memory: memory, loadedTenants: loaded)
        #expect(items.map(\.duration) == [.seconds(4 * 3600), .seconds(1800), .seconds(3600)])
        #expect(items.allSatisfy { $0.disposition == .activate })
        #expect(items.map(\.roleKey) == [k1, k2, k3])   // profile order preserved
    }

    @Test func dispositions() {
        let profile = ActivationProfile(name: "p", entries: [k1, k2, k3, k4].map { .init(roleKey: $0) })
        let roles = [k1: role(k1), k2: role(k2), k3: role(k3)]
        let active = [k1: assignment(k1, .active), k2: assignment(k2, .pendingApproval), k3: assignment(k3, .pendingProvisioning)]
        let items = ProfilePlanner.plan(profile, roles: roles, active: active, memory: [:], loadedTenants: loaded)
        #expect(items.map(\.disposition) == [.alreadyActive, .pending, .pending, .notEligible])
        #expect(items[3].role == nil)
        #expect(items[3].duration == RolePolicy.manualDefault.defaultDuration)
    }

    @Test func unloadedTenantIsNotLoadedRatherThanNotEligible() {
        let profile = ActivationProfile(name: "p", entries: [.init(roleKey: k1)])
        let items = ProfilePlanner.plan(profile, roles: [:], active: [:], memory: [:], loadedTenants: [])
        #expect(items.map(\.disposition) == [.notLoaded])
        // The duration still resolves, so it survives a run untouched.
        #expect(items[0].duration == RolePolicy.manualDefault.defaultDuration)
        let eligible = ProfilePlanner.plan(profile, roles: [:], active: [:], memory: [:], loadedTenants: loaded)
        #expect(eligible.map(\.disposition) == [.notEligible])
    }

    @Test func failedAssignmentPlansAsActivate() {
        let profile = ActivationProfile(name: "p", entries: [.init(roleKey: k1)])
        let items = ProfilePlanner.plan(profile, roles: [k1: role(k1)], active: [k1: assignment(k1, .failed("boom"))],
                                        memory: [:], loadedTenants: loaded)
        #expect(items.map(\.disposition) == [.activate])
    }
}
