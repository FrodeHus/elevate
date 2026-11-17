import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// Last session's roles are on screen at launch, before the first (slow) refresh.
@MainActor
struct AppModelRolesCacheTests {
    private static func state(tenants: [TenantContext] = [Sample.tenant()]) -> AppState {
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = tenants
        return state
    }

    @Test func launchShowsLastSessionsRolesForTenantsStillConfigured() async {
        let dir = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("elevate-tests-\(UUID().uuidString)", isDirectory: true)
        let other = Sample.tenant(tenantId: "tenant-2", name: "Fabrikam")
        let first = await makeModel(state: Self.state(tenants: [Sample.tenant(), other]), directory: dir)
        first.roles[Sample.tenantKey] = [Sample.role(Sample.entraKey, name: "Global Reader")]
        first.roles[other.id] = [Sample.role(Sample.key(.entraDirectory(roleDefinitionId: "x", directoryScopeId: "/"), tenantId: "tenant-2"), name: "Gone")]

        // Next launch: tenant-2 was removed meanwhile, so its cached rows must not come back.
        // Its own directory with just the cache, so the first model's async state saves cannot race it.
        let dir2 = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("elevate-tests-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir2, withIntermediateDirectories: true)
        try? FileManager.default.copyItem(at: dir.appendingPathComponent(AppModel.rolesCacheFile), to: dir2.appendingPathComponent(AppModel.rolesCacheFile))
        let second = await makeModel(state: Self.state(), directory: dir2)
        #expect(second.roles(for: Sample.tenantKey).map(\.displayName) == ["Global Reader"])
        #expect(second.roles[other.id] == nil)
        cleanup(first)
        cleanup(second)
    }
}
