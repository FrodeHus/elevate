import Foundation
import ElevateCore
@testable import Elevate

/// Fresh `AppSettings` over a throwaway UserDefaults suite, so nothing leaks between tests
/// or into the host app's own defaults.
@MainActor
func makeSettings() -> AppSettings {
    AppSettings(defaults: UserDefaults(suiteName: "elevate-tests-\(UUID().uuidString)")!)
}

/// Builds an `AppModel` wired entirely to fakes: no MSAL, no real network, its own state file and
/// its own UserDefaults suite. The monitor is pinned (offline by default) so `bootstrap()` performs
/// no refresh and no update check of its own.
@MainActor
func makeModel(state: AppState = AppState(), http: StubHTTPClient = StubHTTPClient(),
               online: Bool = false, settings: AppSettings? = nil) async -> AppModel {
    let directory = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("elevate-tests-\(UUID().uuidString)", isDirectory: true)
    let store = AppStateStore(directory: directory)
    if state != AppState() { try? await store.save(state) }
    let model = AppModel(tokens: FakeTokenProvider(), http: http, store: store, notifier: NoopNotifier(),
                         network: NetworkMonitor(forcedOnline: online),
                         settings: settings ?? makeSettings())
    await model.bootstrap()
    return model
}

// MARK: Sample data

enum Sample {
    static let identityId = "id-1"
    static let tenantId = "tenant-1"
    static var tenantKey: TenantKey { TenantKey(identityId: identityId, tenantId: tenantId) }

    static func identity(_ id: String = identityId, method: SignInMethod = .ownApp) -> Identity {
        Identity(id: id, upn: "\(id)@example.com", displayName: id.uppercased(), homeTenantId: tenantId, signInMethod: method)
    }

    static func tenant(identityId: String = identityId, tenantId: String = tenantId, name: String = "Contoso") -> TenantContext {
        TenantContext(identityId: identityId, tenantId: tenantId, displayName: name, source: .home)
    }

    static func key(_ scope: RoleScope, identityId: String = identityId, tenantId: String = tenantId) -> RoleKey {
        RoleKey(identityId: identityId, tenantId: tenantId, scope: scope)
    }

    static var entraKey: RoleKey { key(.entraDirectory(roleDefinitionId: "role-def", directoryScopeId: "/")) }
    static var azureKey: RoleKey { key(.azureResource(scope: "/subscriptions/s1", roleDefinitionId: "owner-def")) }
    static var groupKey: RoleKey { key(.group(groupId: "group-1", accessId: .member)) }

    static func role(_ key: RoleKey, name: String) -> EligibleRole {
        EligibleRole(key: key, displayName: name, source: .discovered, policy: .manualDefault)
    }

    static func assignment(_ key: RoleKey, ends: Date? = Date().addingTimeInterval(3600)) -> ActiveAssignment {
        ActiveAssignment(roleKey: key, assignmentId: "a", startDateTime: .now, endDateTime: ends, status: .active)
    }
}
