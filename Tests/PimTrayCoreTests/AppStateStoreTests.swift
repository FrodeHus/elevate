import Testing
import Foundation
@testable import PimTrayCore

@Suite struct AppStateStoreTests {
    func tempDir() -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("pimtray-\(UUID().uuidString)")
        try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    @Test func loadReturnsEmptyStateWhenNoFile() async throws {
        let store = AppStateStore(directory: tempDir())
        let s = try await store.load()
        #expect(s == AppState())
    }

    @Test func saveThenLoadRoundTrips() async throws {
        let store = AppStateStore(directory: tempDir())
        var s = AppState()
        s.identities = [Identity(id: "i", upn: "u@x", displayName: "U", homeTenantId: "t")]
        s.upsertTenant(TenantContext(identityId: "i", tenantId: "t", displayName: "Home", source: .home))
        let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
        s.remember(roleKey: key, justification: "Ops work", duration: .seconds(1800))
        s.manualRoles = [ManualRole(tenantKey: TenantKey(identityId: "i", tenantId: "t"), scope: key.scope, displayName: "R")]
        try await store.save(s)
        let back = try await store.load()
        #expect(back == s)
        #expect(back.memory(for: key)?.justification == "Ops work")
        #expect(back.memory(for: key)?.lastDuration == .seconds(1800))
    }

    @Test func rememberOverwritesPerKey() {
        var s = AppState()
        let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
        s.remember(roleKey: key, justification: "a", duration: nil)
        s.remember(roleKey: key, justification: "b", duration: .seconds(60))
        #expect(s.memory.count == 1)
        #expect(s.memory(for: key)?.justification == "b")
    }

    @Test func removingIdentityRemovesTenantsRolesAndMemory() {
        var s = AppState()
        s.identities = [Identity(id: "i", upn: "u@x", displayName: "U", homeTenantId: "t")]
        s.upsertTenant(TenantContext(identityId: "i", tenantId: "t", displayName: "Home", source: .home))
        let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
        s.remember(roleKey: key, justification: "a", duration: nil)
        s.manualRoles = [ManualRole(tenantKey: key.tenantKey, scope: key.scope, displayName: "R")]
        s.removeIdentity("i")
        #expect(s == AppState())
    }
}
