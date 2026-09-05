# Elevate Activation Shortcuts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Option-click quick activation with the remembered reason, an optional future start time on activations, and one global hot key that runs a profile.

**Architecture:** Core gains `ActivationRequest.startDateTime`, a `.scheduled` assignment status read back from the schedule endpoints, `ActivationOutcome.Result.scheduled`, a pure `QuickActivate` decision helper and a `Countdown.until` label. The app adds `quickActivate`/`quickRun` on `AppModel` with a completion notification, Option-click handling on the Activate/Extend buttons and profile chips, "Start at" pickers in the two activation sheets, a scheduled row state, a Carbon `HotKeyCenter`, and a Settings section with a key recorder.

**Tech Stack:** Swift 6.2, SwiftUI macOS 26, Carbon HotKey API (`RegisterEventHotKey`), UserNotifications, Swift Testing. XcodeGen in `macos/`.

**Spec:** `docs/superpowers/specs/2026-09-05-elevate-shortcuts-design.md`

## Global Constraints

- Paths relative to `macos/`. Tests `swift test` (135 now). Build `xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build 2>&1 | grep -E "error:|BUILD"`. Relaunch `pkill -x Elevate; sleep 1; open build/Build/Products/Debug/Elevate.app`.
- `ElevateCore` imports only Foundation. Swift 6 strict concurrency; no `@unchecked Sendable` in `AppModel`. Never commit `Elevate.xcodeproj`. Swift Testing.
- Every `switch` over `ActiveAssignment.Status` must gain a `.scheduled` arm: `PanelStatus.compute`, `ActiveSummary.order` (filters), `ProfilePlanner.plan`, `ActivationCoordinator.activateOne`, `AssignmentControls.forStatus`, `RoleRow.statusDot`, `ActiveRow` dot.
- "Future" for `.scheduled` = start more than 60 s after now. Old `state.json` has no persisted assignments, so no decode concern.
- Quick activate rules (spec §1): dialog when justification is required and none is remembered; when a ticket is required; when approval is required (single role); for profiles also when any item is `.notLoaded`. MFA / authentication context never block.
- Hot key: Carbon, no Accessibility permission; at least one of ⌘ ⌃ ⌥ required; stored as `HotKeyBinding { keyCode: UInt32, modifiers: UInt32 (Carbon mask), display: String }`.
- Branch `shortcuts` from `main`; commit after every task with the given message.

## File structure

```
Sources/ElevateCore/Models/Roles.swift                  ActivationRequest.startDateTime; Status.scheduled
Sources/ElevateCore/Coordination/ActivationCoordinator.swift  Result.scheduled
Sources/ElevateCore/Coordination/QuickActivate.swift    new
Sources/ElevateCore/Coordination/ProfilePlanner.swift   .scheduled → .pending
Sources/ElevateCore/Support/{PanelStatus,ActiveSummary,ExtendWindow,Countdown}.swift
Sources/ElevateCore/Providers/{EntraDirectoryProvider,AzureResourceProvider,GroupProvider}.swift  start time in bodies; schedule reads; scheduled outcome
Sources/ElevateApp/App/{AppModel,AppSettings,HotKeyCenter,PanelRoute}.swift
Sources/ElevateApp/Notifications/ExpiryNotifier.swift   notify(title:body:)
Sources/ElevateApp/Views/{AssignmentControls,ActiveSection,RoleRow,ProfilesRow,ActivationView,RunProfileView,SettingsView,HotKeyRecorder}.swift
Tests/ElevateCoreTests/{QuickActivateTests,CountdownTests,EntraDirectoryProviderTests,AzureResourceProviderTests,GroupProviderTests,PanelStatusTests,ActiveSummaryTests,ProfilePlannerTests}.swift + Fixtures/{entra,arm,group}-schedules.json
```

---

### Task 1: Core — startDateTime, `.scheduled`, QuickActivate, Countdown.until, helper arms

**Files:** `Models/Roles.swift`, `Coordination/{ActivationCoordinator,QuickActivate,ProfilePlanner}.swift`, `Support/{PanelStatus,ActiveSummary,ExtendWindow,Countdown}.swift`; tests `QuickActivateTests.swift`, `CountdownTests.swift`, plus one new case each in `PanelStatusTests`, `ActiveSummaryTests`, `ProfilePlannerTests`, `ExtendWindowTests`.

**Interfaces (produces):**
```swift
// Roles.swift
public var startDateTime: Date?   // on ActivationRequest; init gains `startDateTime: Date? = nil` as the last parameter
public enum Status { case active, pendingApproval, pendingProvisioning, scheduled, failed(String) }
// ActivationCoordinator
public enum Result { case activated(ActiveAssignment), pendingApproval(ActiveAssignment), scheduled(ActiveAssignment), failed(PIMError) }
// QuickActivate.swift
public enum QuickActivate {
    public enum Decision: Equatable, Sendable { case ready([ActivationRequest]), needsDialog(String) }
    public static func decide(role: EligibleRole, memory: RoleMemory?) -> Decision
    public static func decide(items: [ProfilePlanItem], justification: String?) -> Decision
}
// Countdown
public static func until(_ date: Date, now: Date = .now) -> String   // "2 h 15 m", "15 m", "now"
```

- [ ] **Step 1: Tests**

`QuickActivateTests.swift`:

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct QuickActivateTests {
    let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
    func role(justification: Bool = true, ticket: Bool = false, approval: Bool = false, mfa: Bool = true, max: Int = 4) -> EligibleRole {
        EligibleRole(key: key, displayName: "R", source: .discovered,
                     policy: RolePolicy(defaultDuration: .seconds(3600), maximumDuration: .seconds(max * 3600), requiresJustification: justification,
                                        requiresTicket: ticket, requiresMFA: mfa, requiresApproval: approval, authenticationContext: "c1"))
    }
    let memory = RoleMemory(roleKey: RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/")), justification: "INC-1", lastDuration: .seconds(8 * 3600))

    @Test func readyUsesRememberedReasonAndCappedDuration() {
        guard case .ready(let reqs) = QuickActivate.decide(role: role(), memory: memory) else { Issue.record("expected ready"); return }
        #expect(reqs.count == 1 && reqs[0].justification == "INC-1" && reqs[0].duration == .seconds(4 * 3600))
        #expect(reqs[0].authenticationContext == "c1" && reqs[0].startDateTime == nil && reqs[0].ticket == nil)
    }

    @Test func dialogReasons() {
        #expect(QuickActivate.decide(role: role(), memory: nil) == .needsDialog("no remembered reason"))
        #expect(QuickActivate.decide(role: role(justification: false), memory: nil) != .needsDialog("no remembered reason"))
        #expect(QuickActivate.decide(role: role(ticket: true), memory: memory) == .needsDialog("ticket required"))
        #expect(QuickActivate.decide(role: role(approval: true), memory: memory) == .needsDialog("approval required"))
    }

    @Test func profileDecision() {
        let r = role()
        let ok = ProfilePlanItem(roleKey: key, role: r, duration: .seconds(3600), disposition: .activate)
        let skipped = ProfilePlanItem(roleKey: key, role: r, duration: .seconds(3600), disposition: .alreadyActive)
        guard case .ready(let reqs) = QuickActivate.decide(items: [ok, skipped], justification: "INC-2") else { Issue.record("expected ready"); return }
        #expect(reqs.count == 1 && reqs[0].justification == "INC-2")
        #expect(QuickActivate.decide(items: [ok], justification: nil) == .needsDialog("no remembered reason"))
        #expect(QuickActivate.decide(items: [ProfilePlanItem(roleKey: key, role: nil, duration: .seconds(60), disposition: .notLoaded)], justification: "x") == .needsDialog("roles still loading"))
        #expect(QuickActivate.decide(items: [ProfilePlanItem(roleKey: key, role: role(ticket: true), duration: .seconds(60), disposition: .activate)], justification: "x") == .needsDialog("ticket required"))
        #expect(QuickActivate.decide(items: [skipped], justification: "x") == .needsDialog("nothing to activate"))
    }
}
```

`CountdownTests.swift` additions (append to the existing suite):

```swift
    @Test func untilLabels() {
        let now = Date(timeIntervalSince1970: 1_000_000)
        #expect(Countdown.until(now.addingTimeInterval(2 * 3600 + 15 * 60), now: now) == "2 h 15 m")
        #expect(Countdown.until(now.addingTimeInterval(15 * 60 + 59), now: now) == "15 m")
        #expect(Countdown.until(now.addingTimeInterval(3600), now: now) == "1 h")
        #expect(Countdown.until(now.addingTimeInterval(30), now: now) == "now")
        #expect(Countdown.until(now.addingTimeInterval(-5), now: now) == "now")
    }
```

Add to `PanelStatusTests`: a `.scheduled` assignment sets `pendingApproval` (clock) and does not count as active. `ActiveSummaryTests`: scheduled entries sort after active (by start) and before pending. `ProfilePlannerTests`: a `.scheduled` active maps to `.pending`. `ExtendWindowTests`: `.scheduled` never extends.

- [ ] **Step 2: Implement**

`Roles.swift`: add the `Status.scheduled` case; `ActivationRequest.startDateTime: Date?` with the init parameter appended last (`startDateTime: Date? = nil`).

`ActivationCoordinator`: add `case scheduled(ActiveAssignment)` to `Result`; in `activateOne`'s switch add `case .scheduled: return ActivationOutcome(roleKey: request.roleKey, result: .scheduled(assignment))`.

`QuickActivate.swift`:

```swift
import Foundation

/// Decides whether an activation can go ahead without the dialog, and builds the requests when it can.
public enum QuickActivate {
    public enum Decision: Equatable, Sendable { case ready([ActivationRequest]), needsDialog(String) }

    public static func decide(role: EligibleRole, memory: RoleMemory?) -> Decision {
        let p = role.policy
        if p.requiresTicket { return .needsDialog("ticket required") }
        if p.requiresApproval { return .needsDialog("approval required") }
        let reason = memory?.justification.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if p.requiresJustification && reason.isEmpty { return .needsDialog("no remembered reason") }
        let duration = min(memory?.lastDuration ?? p.defaultDuration, p.maximumDuration)
        return .ready([ActivationRequest(roleKey: role.key, duration: duration, justification: reason, ticket: nil,
                                         authenticationContext: p.authenticationContext)])
    }

    public static func decide(items: [ProfilePlanItem], justification: String?) -> Decision {
        if items.contains(where: { $0.disposition == .notLoaded }) { return .needsDialog("roles still loading") }
        let toRun = items.filter { $0.disposition == .activate }
        if toRun.isEmpty { return .needsDialog("nothing to activate") }
        if toRun.contains(where: { $0.role?.policy.requiresTicket == true }) { return .needsDialog("ticket required") }
        let reason = justification?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if reason.isEmpty && toRun.contains(where: { ($0.role?.policy.requiresJustification ?? true) }) { return .needsDialog("no remembered reason") }
        return .ready(toRun.map { ActivationRequest(roleKey: $0.roleKey, duration: $0.duration, justification: reason, ticket: nil,
                                                    authenticationContext: $0.role?.policy.authenticationContext) })
    }
}
```

`Countdown.until`: whole minutes floored; `< 60 s` → "now"; hours and minutes as "H h M m" omitting a zero part.

`PanelStatus.compute`: `case .scheduled: pending = true`. `ActiveSummary.order`: `active + scheduled(by start) + pending + provisioning`. `ExtendWindow`: unchanged guard (`status == .active`) already excludes it; add the test only. `ProfilePlanner`: `case .scheduled: disposition = .pending`.

- [ ] **Step 3:** `swift test` all pass (135 + new). Commit `git commit -m "Add scheduled status, request start time, QuickActivate and Countdown.until"`.

---

### Task 2: Providers — start time in bodies, scheduled outcome, schedule reads

**Files:** the three providers; fixtures `entra-schedules.json`, `arm-schedules.json`, `group-schedules.json`; provider tests.

**Interfaces:** none new; behaviour per spec §2.

- [ ] **Step 1: Fixtures** (one future activated schedule each; dates far in the future so tests stay valid)

`entra-schedules.json`:
```json
{ "value": [
  { "id": "sched-1", "principalId": "user-obj-1", "roleDefinitionId": "f2ef992c-3afb-46b9-b7cf-a126ee74c451", "directoryScopeId": "/",
    "assignmentType": "Activated", "status": "Provisioned",
    "scheduleInfo": { "startDateTime": "2099-01-01T09:00:00Z", "expiration": { "type": "afterDuration", "duration": "PT2H", "endDateTime": "2099-01-01T11:00:00Z" } },
    "roleDefinition": { "id": "f2ef992c-3afb-46b9-b7cf-a126ee74c451", "displayName": "Global Reader" } },
  { "id": "sched-old", "principalId": "user-obj-1", "roleDefinitionId": "fe930be7-5e62-47db-91af-98c3a49a38b1", "directoryScopeId": "/",
    "assignmentType": "Activated", "status": "Provisioned",
    "scheduleInfo": { "startDateTime": "2020-01-01T09:00:00Z", "expiration": { "type": "afterDuration", "duration": "PT2H" } } }
] }
```
`arm-schedules.json` (ARM shape: `name`, `id`, `properties.scope/roleDefinitionId/assignmentType/startDateTime/endDateTime/status`): one entry for `/subscriptions/sub-1` + the Contributor id with `startDateTime` `2099-01-01T09:00:00Z`, `endDateTime` `2099-01-01T11:00:00Z`, `assignmentType` `Activated`; one past entry.
`group-schedules.json`: one entry `groupId: grp-ops, accessId: member, assignmentType: activated, scheduleInfo.startDateTime 2099-01-01T09:00:00Z, expiration.endDateTime 2099-01-01T11:00:00Z`; one past.

- [ ] **Step 2: Tests** (per provider): (a) activate with `startDateTime` set to `2099-01-01T09:00:00Z` puts that value in `scheduleInfo.startDateTime` and the returned assignment has `status == .scheduled` with that `startDateTime` (stub the POST/PUT response with the existing activate fixture; the provider must prefer the request's future start when the response's `scheduleInfo.startDateTime` is absent or in the past); (b) `activeAssignments` with the schedules fixture stubbed (Entra `roleAssignmentSchedules/filterByCurrentUser`, ARM `roleAssignmentSchedules?`, Group `assignmentSchedules/filterByCurrentUser`) yields a `.scheduled` entry for the future one only and does not override an existing `.active` entry for the same key.

- [ ] **Step 3: Implement**

Each provider: in `activate`, `"startDateTime": GraphJSON.encoderDateString(request.startDateTime ?? .now)`; after decoding, `let start = created.scheduleInfo?.startDateTime ?? request.startDateTime ?? .now`; `let status: Status = start.timeIntervalSinceNow > 60 ? .scheduled : <existing mapping>`; `endDateTime` for `.scheduled` = the computed end (keep it, unlike pending). In `activeAssignments`, add a third read of the schedules endpoint (shape: Entra `Schedule` already has `scheduleInfo`? add `let scheduleInfo: ScheduleInfo?` to the Entra `Schedule` struct; ARM `Properties` has `scheduleInfo`; Group `Instance` needs `let scheduleInfo: ScheduleInfo?`), keep `assignmentType` Activated (case-insensitive) and `scheduleInfo.startDateTime > now + 60`, and insert `.scheduled` entries only where `result[key] == nil`. ARM schedule list: `providers/Microsoft.Authorization/roleAssignmentSchedules?$filter=asTarget()`.

- [ ] **Step 4:** `swift test` green; commit `git commit -m "Providers: send start time, report scheduled activations, read future schedules"`.

---

### Task 3: AppModel — quickActivate, quickRun, scheduled outcomes, notify

**Files:** `App/AppModel.swift`, `App/PanelRoute.swift` (`ExpiryNotifying.notify`), `Notifications/ExpiryNotifier.swift`.

**Interfaces (produces):**
```swift
protocol ExpiryNotifying { func reschedule(...); func notify(title: String, body: String) async }
func quickActivate(_ key: RoleKey) async -> Bool     // true when handled (activated or failed with a notification)
func quickRun(profileId: UUID) async -> Bool
```

- [ ] **Step 1:** `ExpiryNotifying` gains `func notify(title: String, body: String) async`; `NoopNotifier` no-op; `ExpiryNotifier.notify` adds a `UNNotificationRequest` with a `UUID` id and no trigger. `reschedule` skips `.scheduled` (only `.active` already, so verify the `where` clause).

- [ ] **Step 2:** In `activate(_:)` outcome loop, treat `.scheduled(let a)` like `.activated`: rekey if needed, `active[a.roleKey] = a`, remember. Extend the `case .activated(let a), .pendingApproval(let a):` pattern with `, .scheduled(let a)`. The deferred group refresh trigger stays on `.activated` only.

- [ ] **Step 3:** Add:

```swift
    // MARK: Quick activate

    /// Option-click path. Returns false when the dialog is needed; the caller opens it.
    func quickActivate(_ key: RoleKey) async -> Bool {
        guard let role = role(for: key) else { return false }
        guard case .ready(let requests) = QuickActivate.decide(role: role, memory: remembered(for: key)) else { return false }
        await activate(requests)
        await notifyOutcome(title: role.displayName, keys: requests.map(\.roleKey))
        return true
    }

    func quickRun(profileId: UUID) async -> Bool {
        guard let profile = state.profile(id: profileId) else { return false }
        let items = plan(for: profileId)
        guard case .ready(let requests) = QuickActivate.decide(items: items, justification: profile.lastJustification) else { return false }
        await runProfile(id: profileId, items: items, justification: requests.first?.justification ?? "", ticket: nil)
        await notifyOutcome(title: profile.name, keys: requests.map(\.roleKey))
        return true
    }

    private func notifyOutcome(title: String, keys: [RoleKey]) async {
        var ok = 0, pending = 0, scheduled = 0
        var failures: [String] = []
        for k in keys {
            switch progress[k] {
            case .activated: ok += 1
            case .pendingApproval: pending += 1
            case .scheduled: scheduled += 1
            case .failed(let e): failures.append("\(summaryName(for: k)): \(e.userMessage)")
            case nil: break
            }
        }
        var parts: [String] = []
        if ok > 0 { parts.append("\(ok) activated") }
        if scheduled > 0 { parts.append("\(scheduled) scheduled") }
        if pending > 0 { parts.append("\(pending) awaiting approval") }
        if !failures.isEmpty { parts.append("\(failures.count) failed: " + failures.joined(separator: "; ")) }
        await notifier.notify(title: title, body: parts.isEmpty ? "Nothing to do" : parts.joined(separator: ", "))
    }
```

`runProfile` must accept an explicit justification for the quick path (it already does) — but it activates only `.activate` items and records durations; unchanged.

- [ ] **Step 4:** Build; commit `git commit -m "Quick activate and quick run with outcome notifications; scheduled outcomes"`.

---

### Task 4: Views — Option-click, Start at, scheduled rows

**Files:** `Views/{AssignmentControls,RoleRow,ActiveSection,ProfilesRow,ActivationView,RunProfileView}.swift`.

- [ ] **Step 1: Option-click.** In `AssignmentControls`, replace the Activate button action with:

```swift
                Button("Activate") {
                    if NSEvent.modifierFlags.contains(.option) {
                        Task { if await !model.quickActivate(key) { open(.activate([key])) } }
                    } else { open(.activate([key])) }
                }
                .controlSize(.small).disabled(!model.isOnline)
                .help("Activate this role. Option-click to activate with the last reason and duration")
```

Same pattern for Extend (help: "Extend by activating again; Option-click to extend with the last reason"). In `ProfilesRow`, the chip action: `if NSEvent.modifierFlags.contains(.option) { Task { if await !model.quickRun(profileId: p.id) { model.requestRun(p.id); open(.runProfile(p.id)) } } } else { model.requestRun(p.id); open(.runProfile(p.id)) }`; help "Run \(p.name). Option-click to run with the last reason and durations".

- [ ] **Step 2: Scheduled rows.** `AssignmentControls.forStatus` add:

```swift
        case .scheduled:
            let start = assignment?.startDateTime ?? .now
            TimelineView(.periodic(from: .now, by: 30)) { ctx in
                HStack(spacing: 8) {
                    Text("starts in \(Countdown.until(start, now: ctx.date))").font(.caption).foregroundStyle(.secondary)
                    Button("Cancel") { Task { await model.deactivate(key) } }.controlSize(.small).disabled(!model.isOnline)
                        .help("Cancel this scheduled activation")
                }
            }
```

`RoleRow.statusDot` and `ActiveRow`'s dot: `.scheduled` → `Circle().fill(.blue)`. `ActiveRow` currently uses a ternary; switch to an explicit `switch`.

- [ ] **Step 3: Start at.** In `ActivationView` add `@State private var scheduleStart = false`, `@State private var startAt = Date.now.addingTimeInterval(3600)`, a row after the duration/table: `Toggle("Start at", isOn: $scheduleStart)` and, when on, `DatePicker("", selection: $startAt, in: Date.now..., displayedComponents: [.date, .hourAndMinute]).labelsHidden()`. Pass `startDateTime: scheduleStart ? startAt : nil` into every `ActivationRequest`. `progressLabel` adds `case .scheduled: Label("Scheduled", systemImage: "calendar").foregroundStyle(.blue).font(.caption)`. Same three additions in `RunProfileView` (`statusLabel`, requests built in `AppModel.runProfile` — add a `startDateTime: Date? = nil` parameter to `runProfile` and forward it into each request).

- [ ] **Step 4:** Build, relaunch, commit `git commit -m "Option-click quick activation, Start at scheduling, scheduled rows"`.

---

### Task 5: Global hot key — HotKeyCenter, settings, recorder

**Files:** `App/HotKeyCenter.swift` (new), `App/AppSettings.swift`, `App/AppModel.swift`, `Views/HotKeyRecorder.swift` (new), `Views/SettingsView.swift`.

**Interfaces:**
```swift
struct HotKeyBinding: Codable, Equatable, Sendable { var keyCode: UInt32; var modifiers: UInt32; var display: String }
@MainActor final class HotKeyCenter { var onFire: (() -> Void)?; func register(_ b: HotKeyBinding) throws; func unregister() }
// AppSettings: var hotKey: HotKeyBinding? ; var hotKeyProfileId: UUID?
// AppModel: func applyHotKey(); var hotKeyError: String?
```

- [ ] **Step 1: HotKeyCenter**

```swift
import AppKit
import Carbon.HIToolbox

struct HotKeyBinding: Codable, Equatable, Sendable {
    var keyCode: UInt32
    var modifiers: UInt32   // Carbon: cmdKey, optionKey, controlKey, shiftKey
    var display: String
}

/// One global hot key via Carbon's RegisterEventHotKey; works without Accessibility permission.
@MainActor final class HotKeyCenter {
    var onFire: (() -> Void)?
    private var ref: EventHotKeyRef?
    private var handler: EventHandlerRef?
    private static let signature: OSType = 0x454C5654 // 'ELVT'

    init() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let selfPtr = Unmanaged.passUnretained(self).toOpaque()
        InstallEventHandler(GetApplicationEventTarget(), { _, _, userData in
            guard let userData else { return noErr }
            let center = Unmanaged<HotKeyCenter>.fromOpaque(userData).takeUnretainedValue()
            Task { @MainActor in center.onFire?() }
            return noErr
        }, 1, &spec, selfPtr, &handler)
    }

    func register(_ b: HotKeyBinding) throws {
        unregister()
        let id = EventHotKeyID(signature: Self.signature, id: 1)
        let status = RegisterEventHotKey(b.keyCode, b.modifiers, id, GetApplicationEventTarget(), 0, &ref)
        guard status == noErr else { throw HotKeyError.registrationFailed(status) }
    }

    func unregister() {
        if let ref { UnregisterEventHotKey(ref); self.ref = nil }
    }

    enum HotKeyError: Error, LocalizedError {
        case registrationFailed(OSStatus)
        var errorDescription: String? { "The shortcut could not be registered; it may be in use by another app." }
    }
}
```

The C callback closure must not capture context (it does not: it only uses `userData`). `Task { @MainActor in … }` hops from the Carbon callback to the main actor.

- [ ] **Step 2: Settings + model.** `AppSettings`: `hotKey: HotKeyBinding?` stored as JSON under key `hotKey`, `hotKeyProfileId: UUID?` under `hotKeyProfileId` (string). `AppModel`: `private let hotKeys = HotKeyCenter()` created in init (only when not running tests: guard `ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] == nil` is unnecessary since there is no app test target; keep simple), `var hotKeyError: String?`, `func applyHotKey()` called at end of `bootstrap()` and after Settings changes: unregister; if both binding and profile exist, register and set `onFire = { [weak self] in Task { @MainActor in guard let self, let id = self.settings.hotKeyProfileId else { return }; if await !self.quickRun(profileId: id) { self.requestRun(id); self.pendingProfileRun = id } } }` where `pendingProfileRun: UUID?` is a new observable that `MenuBarLabel` watches like `pendingExtend` to `openWindow(value: .runProfile(id))`. Set `hotKeyError` on throw.

- [ ] **Step 3: Recorder.**

```swift
import SwiftUI
import AppKit
import Carbon.HIToolbox

/// Click, press a combination; Escape cancels. Requires at least one of ⌘ ⌃ ⌥.
struct HotKeyRecorder: View {
    @Binding var binding: HotKeyBinding?
    @State private var recording = false
    @State private var monitor: Any?

    var body: some View {
        Button(recording ? "Press keys…" : (binding?.display ?? "Record shortcut")) { start() }
            .onDisappear { stop() }
    }

    private func start() {
        recording = true
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            defer { stop() }
            if event.keyCode == UInt16(kVK_Escape) { return nil }
            let flags = event.modifierFlags.intersection([.command, .option, .control, .shift])
            guard !flags.intersection([.command, .option, .control]).isEmpty else { return nil }
            var carbon: UInt32 = 0
            if flags.contains(.command) { carbon |= UInt32(cmdKey) }
            if flags.contains(.option) { carbon |= UInt32(optionKey) }
            if flags.contains(.control) { carbon |= UInt32(controlKey) }
            if flags.contains(.shift) { carbon |= UInt32(shiftKey) }
            let symbols = (flags.contains(.control) ? "⌃" : "") + (flags.contains(.option) ? "⌥" : "") + (flags.contains(.shift) ? "⇧" : "") + (flags.contains(.command) ? "⌘" : "")
            let keyName = (event.charactersIgnoringModifiers ?? "?").uppercased()
            binding = HotKeyBinding(keyCode: UInt32(event.keyCode), modifiers: carbon, display: symbols + keyName)
            return nil
        }
    }

    private func stop() {
        recording = false
        if let monitor { NSEvent.removeMonitor(monitor); self.monitor = nil }
    }
}
```

`SettingsView`: new `Section("Global shortcut")` with `HotKeyRecorder(binding: $hotKey)`, a `Picker("Runs profile", selection: $profileId)` over `model.profiles` plus a "None" tag (`UUID?`), a "Clear" button, `model.hotKeyError` in red, and a caption "Runs the profile like Option-clicking its chip; opens the run sheet if input is needed." Changes write to `model.settings` and call `model.applyHotKey()`.

- [ ] **Step 4:** Build (Carbon links automatically via AppKit), relaunch, commit `git commit -m "Global hot key that runs a profile, with a recorder in Settings"`.

---

### Task 6: Final checks and docs

- [ ] `swift test` and build; relaunch.
- [ ] `macos/README.md`: "**Shortcuts.**" paragraph: Option-click, Start at, the global shortcut and its limits (runs a profile; cannot open the panel). Commit "Document quick activate, scheduling and the global shortcut".
