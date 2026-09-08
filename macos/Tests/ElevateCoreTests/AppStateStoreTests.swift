import Testing
import Foundation
@testable import ElevateCore

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

    @Test func olderGenerationDoesNotOverwriteNewerState() async throws {
        let store = AppStateStore(directory: tempDir())
        var newer = AppState()
        newer.identities = [Identity(id: "new", upn: "new@x", displayName: "New", homeTenantId: "t")]
        var older = AppState()
        older.identities = [Identity(id: "old", upn: "old@x", displayName: "Old", homeTenantId: "t")]
        try await store.save(newer, generation: 2)
        try await store.save(older, generation: 1)
        let back = try await store.load()
        #expect(back.identities.map(\.id) == ["new"])
    }

    @Test func quarantineMovesUnreadableFileAside() async throws {
        let dir = tempDir()
        let file = dir.appendingPathComponent("state.json")
        try Data("not json".utf8).write(to: file)
        let store = AppStateStore(directory: dir)
        await #expect(throws: (any Error).self) { try await store.load() }
        let backup = try await store.quarantineCorruptFile()
        #expect(backup?.lastPathComponent == "state.json.bak")
        #expect(!FileManager.default.fileExists(atPath: file.path))
        #expect(try await store.load() == AppState())
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

    @Test func accessPackageRecordsRoundTripAndDefaultEmpty() async throws {
        let dir = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = AppStateStore(directory: dir)
        var s = AppState()
        let key = TenantKey(identityId: "i", tenantId: "t")
        let snapshot = AccessPackageSnapshot(requests: [AccessPackageRequest(id: "r", packageId: "p", packageName: "Pkg", requestType: "userAdd", state: .pendingApproval)])
        let polledAt = GraphJSON.parseDate("2026-09-08T07:00:00Z")!
        s.setAccessPackages(key, snapshot: snapshot, polledAt: polledAt)
        var tracker = NewRoleTracker()
        tracker.observe(discovered: [RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))])
        s.setRoleTracker(key, tracker)
        try await store.save(s)
        let loaded = try await store.load()
        #expect(loaded.accessPackageRecord(key)?.snapshot == snapshot)
        #expect(loaded.accessPackageRecord(key)?.polledAt == polledAt)
        #expect(loaded.roleTracker(key) == tracker)
        #expect(loaded.roleTracker(TenantKey(identityId: "i", tenantId: "other")) == NewRoleTracker())
    }

    @Test func legacyStateWithoutAccessPackagesLoads() throws {
        let legacy = Data(#"{"identities":[],"tenants":[],"manualRoles":[],"memory":[],"profiles":[]}"#.utf8)
        let s = try JSONDecoder().decode(AppState.self, from: legacy)
        #expect(s.accessPackages.isEmpty && s.roleTracking.isEmpty)
    }

    @Test func removingATenantDropsItsAccessPackageState() {
        var s = AppState()
        let key = TenantKey(identityId: "i", tenantId: "t")
        s.setAccessPackages(key, snapshot: AccessPackageSnapshot(), polledAt: .now)
        s.setRoleTracker(key, NewRoleTracker())
        s.removeTenant(key)
        #expect(s.accessPackageRecord(key) == nil)
        #expect(s.roleTracking.isEmpty)
    }
}
