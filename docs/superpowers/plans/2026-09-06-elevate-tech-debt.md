# Elevate Tech Debt Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pay down the debt the reviews deferred: split the 1500-line `AppModel`, share pinned-header chrome, remove duplicated ARM/schedule/approval helpers in Core, and add an app-layer test target with the first `AppModel` tests.

**Architecture:** Pure refactors with no behaviour change, each verified by the unchanged Core suite (220), a green app build, and (for Task 4) new app-target tests. `AppModel` becomes a core file plus feature extensions in `App/AppModel+*.swift`; a `PinnedSectionHeader` view replaces the three copies of header chrome; Core gets one `GraphSchedule` wire-type pair and a static ARM URL helper; a new XcodeGen `ElevateAppTests` bundle reuses the Core test fakes.

**Tech Stack:** Swift 6.2, SwiftUI macOS 26, XcodeGen, Swift Testing (Core) and Swift Testing in an Xcode unit-test bundle (app).

**Spec:** none (refactor; the reviews' findings are the requirements, summarised per task).

## Global Constraints

- Paths relative to `macos/`. Tests `swift test` (220, must stay exactly 220 with unchanged assertions for Tasks 1-3). Build `xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build 2>&1 | grep -E "error:|BUILD"`. Relaunch after each app task.
- No behaviour change in Tasks 1-3: moved code keeps its body; access levels may widen from `private` to `internal`/`fileprivate` only as needed for extensions; no `@unchecked Sendable` in `AppModel`; `ElevateCore` imports only Foundation. Never commit `Elevate.xcodeproj`.
- Branch `tech-debt` from `main`; one commit per task.

## File structure

```
Sources/ElevateApp/App/AppModel.swift                 core: state, init, live(), applyClientId, derived accessors, bootstrap, timers
Sources/ElevateApp/App/AppModel+Accounts.swift        accounts, tenants, discovery, sign-out
Sources/ElevateApp/App/AppModel+Refresh.swift         refresh(_:kinds:), refreshAll, policies, breakers, group refresh, notifications reschedule
Sources/ElevateApp/App/AppModel+Activation.swift      activation capability, activate/deactivate/cancel, quick activate, rekey, notifyOutcome
Sources/ElevateApp/App/AppModel+Approvals.swift       approvals state, read, announce, decide
Sources/ElevateApp/App/AppModel+Profiles.swift        profiles, selection helpers, runRequests, manual roles
Sources/ElevateApp/App/AppModel+Panel.swift           panelTab, search, visibility, summary, PanelStatus inputs
Sources/ElevateApp/App/AppModel+Operations.swift      hot key, diagnostics, launch at login, updates, error log
Sources/ElevateApp/Views/PinnedSectionHeader.swift    new shared chrome
Sources/ElevateCore/Providers/GraphSchedule.swift     new: shared Expiration/ScheduleInfo wire types
Sources/ElevateCore/Providers/GraphApprovals.swift    moved from ApprovalProvider.swift
Sources/ElevateCore/Providers/AzureResourceProvider.swift  static armURL
Tests/ElevateAppTests/*.swift                          new app test bundle
project.yml, .github/workflows/macos.yml               test target + CI
```

---

### Task 1: Split `AppModel.swift` into feature extensions

**Files:** `App/AppModel.swift` and the seven new `App/AppModel+*.swift` files.

- [ ] Read `AppModel.swift` end to end and map every declaration to a target file by the MARK sections (Derived → core or `+Panel` for tab/search/summary/selection; Lifecycle → core; Global shortcut → `+Operations`; Accounts/Tenants → `+Accounts`; Refresh → `+Refresh`; Approvals → `+Approvals`; Entra activation capability + Activation + Quick activate → `+Activation`; Profiles + Manual roles → `+Profiles`; Diagnostics/launch/updates → `+Operations`).
- [ ] Stored properties must stay in the class body (Swift extensions cannot add stored properties): keep every `var`/`let` with storage in `AppModel.swift`, grouped under MARKs by feature, with a one-line comment pointing at the extension that owns the logic. Move only methods and computed properties.
- [ ] Widen access where an extension in another file needs it: `private` → `fileprivate` will not work across files, so use internal with a `// internal for AppModel+X` note, or keep the helper in the same file as its sole caller. Prefer moving helpers with their callers.
- [ ] Build; `swift test` unchanged (220); relaunch; verify with `git diff --stat` that `AppModel.swift` shrank to roughly the stored-state-plus-lifecycle core (target under 450 lines) and that the sum of moved code equals what was removed (use `wc -l` before/after and `git diff -M` to show moves).
- [ ] Commit `git commit -m "Split AppModel into feature extensions (no behaviour change)"`.

---

### Task 2: Core deduplication

**Files:** `Providers/GraphSchedule.swift` (new), `Providers/GraphApprovals.swift` (new, moved), `Providers/{EntraDirectoryProvider,GroupProvider,AzureResourceProvider,AzureApprovalProvider,ApprovalProvider}.swift`.

- [ ] `GraphSchedule.swift`: `struct ScheduleExpiration: Decodable, Sendable { let type: String?; let duration: String?; let endDateTime: Date? }` and `struct ScheduleInfo: Decodable, Sendable { let startDateTime: Date?; let expiration: ScheduleExpiration? }` (internal). Replace the per-provider `Expiration`/`ScheduleInfo` structs in Entra, Group, Azure (and the approval provider's reuse) with these; keep field names so no call site changes beyond the type name.
- [ ] `AzureResourceProvider.armURL` becomes `static func armURL(_ path: String, apiVersion: String = "2020-10-01", query: [String: String] = [:]) throws -> URL`; update its internal call sites (`Self.armURL`) and delete `AzureApprovalProvider.armURL`, calling the static instead.
- [ ] Move `GraphApprovals` out of `ApprovalProvider.swift` into `Providers/GraphApprovals.swift` unchanged; `ApprovalProvider.swift` keeps the protocol and `ApprovalDiff` only.
- [ ] `swift test` must remain 220 passing with no test edits except type renames if a test referenced a moved struct (grep first; `Expiration`/`ScheduleInfo` are unlikely to be referenced by tests).
- [ ] Commit `git commit -m "Share schedule wire types and the ARM URL helper; move GraphApprovals to its own file"`.

---

### Task 3: Shared pinned-section header

**Files:** `Views/PinnedSectionHeader.swift` (new), `Views/ActiveSection.swift`, `Views/ApprovalsSection.swift`, `Views/TenantSection.swift` (chrome only).

- [ ] `PinnedSectionHeader<Leading: View, Trailing: View>`: chevron button toggling `expanded` via an `onToggle` closure, a title `Text` with an optional count `Text` in a `tint` colour, then `leading` extras, `Spacer`, `trailing` content; padding `PanelMetrics.headerInset`/`trailingInset`, `.regularMaterial` background, bottom `Divider`, `frame(maxWidth: .infinity, alignment: .leading)`. Accessibility label "Collapse <title>"/"Expand <title>".
- [ ] `ActiveHeader` and `ApprovalsHeader` become thin wrappers over it (same titles, counts, tints, toggles). `TenantHeader` keeps its two-line layout but replaces its inner chrome modifiers (padding, material, divider) with a shared `pinnedHeaderChrome()` view modifier extracted alongside, so all three share the same numbers.
- [ ] Build, relaunch, visually unchanged by reading; commit `git commit -m "Share pinned section header chrome"`.

---

### Task 4: App test target with the first `AppModel` tests

**Files:** `project.yml`, `Tests/ElevateAppTests/{AppModelPanelTests,AppModelSelectionTests,AppModelUpdatesTests}.swift`, `Tests/ElevateAppTests/Support/TestModel.swift`, `.github/workflows/macos.yml`.

- [ ] `project.yml`: add target `ElevateAppTests` (`type: bundle.unit-test`, `platform: macOS`, `sources: [Tests/ElevateAppTests, Tests/ElevateCoreTests/Support]`, `dependencies: [{target: ElevateApp}, {package: ElevateCore, product: ElevateCore}]`, settings `TEST_HOST` pointing at the app (`$(BUILT_PRODUCTS_DIR)/Elevate.app/Contents/MacOS/Elevate`), `BUNDLE_LOADER: $(TEST_HOST)`, `CODE_SIGN_STYLE: Automatic`, `DEVELOPMENT_TEAM: VLJKN96D7N`); add it to the `ElevateApp` scheme's `test.targets`. The Core test fakes use `@testable import ElevateCore`; in the shared Support files change to plain `import ElevateCore` if the app test bundle cannot use `@testable` (all fake-conformed protocols are public), and make sure `Fixtures.swift` resolves its bundle path in both contexts (`Bundle.module` does not exist in the Xcode target — guard with `#if SWIFT_PACKAGE`; if that is too fiddly, exclude `Fixtures.swift` from the app target's sources and don't use fixtures there).
- [ ] `Support/TestModel.swift`: `@MainActor func makeModel(state: AppState = AppState()) async -> AppModel` building `AppModel(tokens: FakeTokenProvider(), http: StubHTTPClient(), store: AppStateStore(directory: <temp dir>), notifier: NoopNotifier(), settings: AppSettings(defaults: UserDefaults(suiteName: "tests-\(UUID())")!))`, saving `state` through the store first if non-empty, then `await model.bootstrap()` (offline `NetworkMonitor` avoids network: check `NetworkMonitor` for an init that can be forced offline; if none, add `init(forcedOnline: Bool?)` in this task as the only app-code change).
- [ ] Tests (Swift Testing, `@MainActor`): `roles(for:tab:)` filters by kind; `selection` survives a tab change and clears on search change; `selectionBreakdown`; `visibleIdentities` while filtering; `activeAssignmentsOrdered` scoped to the tab; `canActivate` false for an Entra key on a first-party identity; `checkForUpdates(force: false)` is throttled within 24 h (`settings.lastUpdateCheck = .now` → no request on the stub) and `force: true` hits the stub URL with the GitHub headers and sets "No releases yet" on a 404.
- [ ] Run: `xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates test 2>&1 | grep -E "Test Suite|passed|failed|error:" | tail -15`. CI: add a step running the same `xcodebuild test` in `.github/workflows/macos.yml`'s app job with signing disabled (`CODE_SIGN_IDENTITY="" CODE_SIGNING_ALLOWED=NO`).
- [ ] Commit `git commit -m "Add ElevateAppTests with the first AppModel tests"`.
