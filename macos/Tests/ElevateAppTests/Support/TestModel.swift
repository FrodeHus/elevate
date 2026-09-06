import Foundation
import ElevateCore
@testable import Elevate

/// Tracks the throwaway UserDefaults suite backing each `AppSettings` created by `makeSettings()`,
/// so `cleanup(_:)` can remove it after the test is done with it.
@MainActor
private var settingsSuites: [ObjectIdentifier: String] = [:]

/// Tracks the throwaway state directory backing each `AppModel` created by `makeModel(...)`,
/// so `cleanup(_:)` can remove it after the test is done with it.
@MainActor
private var modelDirectories: [ObjectIdentifier: URL] = [:]

/// Fresh `AppSettings` over a throwaway UserDefaults suite, so nothing leaks between tests
/// or into the host app's own defaults. Its suite is removed by `cleanup(_:)` once the
/// `AppModel` it ends up attached to is done with it.
@MainActor
func makeSettings() -> AppSettings {
    let suiteName = "elevate-tests-\(UUID().uuidString)"
    let settings = AppSettings(defaults: UserDefaults(suiteName: suiteName)!)
    settingsSuites[ObjectIdentifier(settings)] = suiteName
    return settings
}

/// Builds an `AppModel` wired entirely to fakes: no MSAL, no real network, its own state file and
/// its own UserDefaults suite. The monitor is pinned (offline by default) so `bootstrap()` performs
/// no refresh and no update check of its own. Call `cleanup(_:)` once the test is done with the
/// model to remove its temp state directory and UserDefaults suite.
///
/// `ownAppViaLoopback` overrides what `BuildInfo.signingState` would say (it describes the test
/// host, not a build under test): pass true to model an unsigned build, where the own-app
/// registration signs in through the loopback flow instead of MSAL.
@MainActor
func makeModel(state: AppState = AppState(), http: StubHTTPClient = StubHTTPClient(),
               online: Bool = false, settings: AppSettings? = nil,
               ownAppViaLoopback: Bool? = nil) async -> AppModel {
    let directory = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("elevate-tests-\(UUID().uuidString)", isDirectory: true)
    let store = AppStateStore(directory: directory)
    if state != AppState() { try? await store.save(state) }
    let model = AppModel(tokens: FakeTokenProvider(), http: http, store: store, notifier: NoopNotifier(),
                         network: NetworkMonitor(forcedOnline: online),
                         settings: settings ?? makeSettings(),
                         ownAppViaLoopbackOverride: ownAppViaLoopback)
    await model.bootstrap()
    modelDirectories[ObjectIdentifier(model)] = directory
    return model
}

/// Removes the temp state directory and UserDefaults suite created for `model` by `makeModel(...)`
/// (and, transitively, by `makeSettings()`), so nothing leaks on disk or into `UserDefaults`
/// between test runs. Safe to call even if either was never registered.
@MainActor
func cleanup(_ model: AppModel) {
    if let directory = modelDirectories.removeValue(forKey: ObjectIdentifier(model)) {
        try? FileManager.default.removeItem(at: directory)
    }
    if let suiteName = settingsSuites.removeValue(forKey: ObjectIdentifier(model.settings)) {
        UserDefaults.standard.removePersistentDomain(forName: suiteName)
    }
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
