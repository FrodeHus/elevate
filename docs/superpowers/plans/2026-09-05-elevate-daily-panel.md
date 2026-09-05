# Elevate Daily-Use Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A pinned "Active now" summary, stateful menu bar badges, inline Extend, an "Activate again" notification on expiry, a search filter, and Azure scope tooltips.

**Architecture:** Four pure helpers in `ElevateCore/Support` (`PanelStatus`, `ActiveSummary`, `PanelFilter`, `ExtendWindow`) carry all the logic and the tests. The app extracts the per-status trailing controls of `RoleRow` into `AssignmentControls` so the new `ActiveSection` shares them, adds `searchQuery`/visibility filtering and `collapsedActive` to `AppModel`, rebuilds `MenuBarLabel` on `PanelStatus`, and teaches `ExpiryNotifier` a second "expired" notification with an "Activate again" action.

**Tech Stack:** Swift 6.2, SwiftUI macOS 26, Swift Testing, UserNotifications. XcodeGen project in `macos/`.

**Spec:** `docs/superpowers/specs/2026-09-05-elevate-daily-panel-design.md`

## Global Constraints

- Paths relative to `macos/`. `swift test` from `macos/`; app build `xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build 2>&1 | grep -E "error:|BUILD"`; relaunch `pkill -x Elevate; sleep 1; open build/Build/Products/Debug/Elevate.app`.
- `ElevateCore` imports only Foundation. Swift 6 strict concurrency; no `@unchecked Sendable` in `AppModel`. Never commit `Elevate.xcodeproj`. Swift Testing (`@Suite`, `@Test`, `#expect`).
- Exact values: expiring-soon window 300 s; Extend window 900 s; menu bar recompute every 30 s; expired notification fires at `endDateTime + 5 s`; UserDefaults key `collapsedActive`; notification category `PIMTRAY_EXPIRED`, action `PIMTRAY_ACTIVATE_AGAIN` titled "Activate again"; symbols `shield`, `checkmark.shield.fill`, `exclamationmark.shield.fill`, `clock`.
- `ActiveAssignment.Status` cases: `.active`, `.pendingApproval`, `.pendingProvisioning`, `.failed(String)`. `Countdown.remaining(until:now:)`/`Countdown.label` exist. `RolePolicy.maximumDuration` exists.
- Commit after every task with the given message, on branch `daily-panel` from `main`.

## File structure

```
Sources/ElevateCore/Support/PanelStatus.swift        new: menu bar state from assignments
Sources/ElevateCore/Support/ActiveSummary.swift      new: ordering of the summary
Sources/ElevateCore/Support/PanelFilter.swift        new: search matching
Sources/ElevateCore/Support/ExtendWindow.swift       new: when Extend is offered
Sources/ElevateApp/App/AppSettings.swift             + collapsedActive
Sources/ElevateApp/App/AppModel.swift                + activeAssignmentsOrdered, collapsedActive, searchQuery, visibility filters
Sources/ElevateApp/App/ElevateApp.swift              MenuBarLabel rebuilt on PanelStatus
Sources/ElevateApp/Notifications/ExpiryNotifier.swift + expired notification and Activate again
Sources/ElevateApp/Views/AssignmentControls.swift    new: trailing controls shared by RoleRow and ActiveRow (+ Extend)
Sources/ElevateApp/Views/ActiveSection.swift         new: pinned summary
Sources/ElevateApp/Views/RoleRow.swift               uses AssignmentControls; Azure scope tooltip
Sources/ElevateApp/Views/PanelView.swift             summary, search field, visible identities/tenants
Tests/ElevateCoreTests/{PanelStatusTests,ActiveSummaryTests,PanelFilterTests,ExtendWindowTests}.swift
```

---

### Task 1: Core helpers — PanelStatus, ActiveSummary, ExtendWindow, PanelFilter

**Files:**
- Create: the four files under `Sources/ElevateCore/Support/` listed above
- Test: `Tests/ElevateCoreTests/PanelStatusTests.swift`, `ActiveSummaryTests.swift`, `PanelFilterTests.swift`, `ExtendWindowTests.swift`

**Interfaces (produces):**
```swift
public struct PanelStatus: Equatable, Sendable { public let activeCount: Int; public let expiringSoon: Bool; public let pendingApproval: Bool
    public static func compute(_ assignments: [ActiveAssignment], now: Date, soonWithin: TimeInterval = 300) -> PanelStatus }
public enum ActiveSummary { public static func order(_ assignments: [ActiveAssignment]) -> [ActiveAssignment] }
public enum PanelFilter { public static func matches(query: String, role: EligibleRole, tenantName: String, upn: String) -> Bool
                          public static func isActive(_ query: String) -> Bool }
public enum ExtendWindow { public static func canExtend(_ assignment: ActiveAssignment, now: Date, within: TimeInterval = 900) -> Bool }
```

- [ ] **Step 1: Write the failing tests**

`Tests/ElevateCoreTests/PanelStatusTests.swift`:

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct PanelStatusTests {
    let now = Date(timeIntervalSince1970: 1_000_000)
    func key(_ n: String) -> RoleKey { RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: n, directoryScopeId: "/")) }
    func assignment(_ n: String, status: ActiveAssignment.Status, endsIn: TimeInterval? = 3600) -> ActiveAssignment {
        ActiveAssignment(roleKey: key(n), assignmentId: n, startDateTime: now.addingTimeInterval(-600),
                         endDateTime: endsIn.map { now.addingTimeInterval($0) }, status: status)
    }

    @Test func emptyIsIdle() {
        #expect(PanelStatus.compute([], now: now) == PanelStatus(activeCount: 0, expiringSoon: false, pendingApproval: false))
    }

    @Test func countsActiveOnly() {
        let s = PanelStatus.compute([assignment("a", status: .active), assignment("b", status: .pendingApproval), assignment("c", status: .pendingProvisioning), assignment("d", status: .failed("x"))], now: now)
        #expect(s.activeCount == 1)
        #expect(s.pendingApproval)
        #expect(!s.expiringSoon)
    }

    @Test func expiringSoonBoundary() {
        #expect(PanelStatus.compute([assignment("a", status: .active, endsIn: 300)], now: now).expiringSoon)
        #expect(!PanelStatus.compute([assignment("a", status: .active, endsIn: 301)], now: now).expiringSoon)
        #expect(!PanelStatus.compute([assignment("a", status: .active, endsIn: -1)], now: now).expiringSoon)   // already past: not "soon"
        #expect(!PanelStatus.compute([assignment("a", status: .active, endsIn: nil)], now: now).expiringSoon)
        #expect(!PanelStatus.compute([assignment("a", status: .pendingApproval, endsIn: 10)], now: now).expiringSoon)
    }
}
```

`Tests/ElevateCoreTests/ActiveSummaryTests.swift`:

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct ActiveSummaryTests {
    let now = Date(timeIntervalSince1970: 1_000_000)
    func key(_ n: String) -> RoleKey { RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: n, directoryScopeId: "/")) }
    func a(_ n: String, _ status: ActiveAssignment.Status, start: TimeInterval = 0, end: TimeInterval? = nil) -> ActiveAssignment {
        ActiveAssignment(roleKey: key(n), assignmentId: n, startDateTime: now.addingTimeInterval(start),
                         endDateTime: end.map { now.addingTimeInterval($0) }, status: status)
    }

    @Test func activeFirstBySoonestExpiryThenPendingByStart() {
        let input = [
            a("prov", .pendingProvisioning),
            a("late", .active, end: 7200),
            a("pend2", .pendingApproval, start: 20),
            a("noend", .active, end: nil),
            a("soon", .active, end: 600),
            a("pend1", .pendingApproval, start: 10),
            a("failed", .failed("x")),
        ]
        let ids = ActiveSummary.order(input).map(\.assignmentId)
        #expect(ids == ["soon", "late", "noend", "pend1", "pend2", "prov"])
    }
}
```

`Tests/ElevateCoreTests/PanelFilterTests.swift`:

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct PanelFilterTests {
    let role = EligibleRole(key: RoleKey(identityId: "i", tenantId: "t", scope: .azureResource(scope: "/subscriptions/s1", roleDefinitionId: "r")),
                            displayName: "Contributor", detail: "Pay-As-You-Go · subscription", source: .discovered, policy: .manualDefault, viaGroup: "Platform Team")

    @Test func emptyQueryMatchesEverything() {
        #expect(PanelFilter.matches(query: "", role: role, tenantName: "Contoso", upn: "u@contoso.com"))
        #expect(PanelFilter.matches(query: "   ", role: role, tenantName: "Contoso", upn: "u@contoso.com"))
        #expect(!PanelFilter.isActive("  "))
        #expect(PanelFilter.isActive(" x"))
    }

    @Test func matchesEachFieldCaseInsensitively() {
        for q in ["contrib", "CONTRIBUTOR", "pay-as", "platform", "contoso", "U@CONTOSO"] {
            #expect(PanelFilter.matches(query: q, role: role, tenantName: "Contoso", upn: "u@contoso.com"), "query \(q)")
        }
        #expect(!PanelFilter.matches(query: "reader", role: role, tenantName: "Contoso", upn: "u@contoso.com"))
    }

    @Test func ignoresDiacritics() {
        var r = role
        r.displayName = "Sécurité"
        #expect(PanelFilter.matches(query: "securite", role: r, tenantName: "", upn: ""))
    }
}
```

`Tests/ElevateCoreTests/ExtendWindowTests.swift`:

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct ExtendWindowTests {
    let now = Date(timeIntervalSince1970: 1_000_000)
    func a(_ status: ActiveAssignment.Status, end: TimeInterval?) -> ActiveAssignment {
        ActiveAssignment(roleKey: RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/")),
                         assignmentId: "x", startDateTime: now.addingTimeInterval(-3600), endDateTime: end.map { now.addingTimeInterval($0) }, status: status)
    }

    @Test func offeredOnlyInsideTheWindowWhileActive() {
        #expect(ExtendWindow.canExtend(a(.active, end: 900), now: now))
        #expect(ExtendWindow.canExtend(a(.active, end: 1), now: now))
        #expect(!ExtendWindow.canExtend(a(.active, end: 901), now: now))
        #expect(!ExtendWindow.canExtend(a(.active, end: 0), now: now))
        #expect(!ExtendWindow.canExtend(a(.active, end: nil), now: now))
        #expect(!ExtendWindow.canExtend(a(.pendingApproval, end: 100), now: now))
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `swift test --filter "PanelStatusTests|ActiveSummaryTests|PanelFilterTests|ExtendWindowTests"` — expected: compile errors, types not found.

- [ ] **Step 3: Implement**

`Sources/ElevateCore/Support/PanelStatus.swift`:

```swift
import Foundation

/// What the menu bar label needs to know, derived from the assignments alone.
public struct PanelStatus: Equatable, Sendable {
    public let activeCount: Int
    public let expiringSoon: Bool
    public let pendingApproval: Bool

    public init(activeCount: Int, expiringSoon: Bool, pendingApproval: Bool) {
        self.activeCount = activeCount
        self.expiringSoon = expiringSoon
        self.pendingApproval = pendingApproval
    }

    /// `expiringSoon` is true when an active assignment ends within `soonWithin` seconds of `now` (and has not ended yet).
    public static func compute(_ assignments: [ActiveAssignment], now: Date, soonWithin: TimeInterval = 300) -> PanelStatus {
        var active = 0, soon = false, pending = false
        for a in assignments {
            switch a.status {
            case .active:
                active += 1
                if let end = a.endDateTime {
                    let left = end.timeIntervalSince(now)
                    if left > 0 && left <= soonWithin { soon = true }
                }
            case .pendingApproval: pending = true
            case .pendingProvisioning, .failed: break
            }
        }
        return PanelStatus(activeCount: active, expiringSoon: soon, pendingApproval: pending)
    }
}
```

`Sources/ElevateCore/Support/ActiveSummary.swift`:

```swift
import Foundation

/// Order for the "Active now" section: what expires first on top, then what is waiting.
public enum ActiveSummary {
    public static func order(_ assignments: [ActiveAssignment]) -> [ActiveAssignment] {
        let active = assignments.filter { $0.status == .active }
            .sorted { ($0.endDateTime ?? .distantFuture, $0.assignmentId ?? "") < ($1.endDateTime ?? .distantFuture, $1.assignmentId ?? "") }
        let pending = assignments.filter { $0.status == .pendingApproval }
            .sorted { ($0.startDateTime, $0.assignmentId ?? "") < ($1.startDateTime, $1.assignmentId ?? "") }
        let provisioning = assignments.filter { $0.status == .pendingProvisioning }
            .sorted { ($0.startDateTime, $0.assignmentId ?? "") < ($1.startDateTime, $1.assignmentId ?? "") }
        return active + pending + provisioning
    }
}
```

`Sources/ElevateCore/Support/PanelFilter.swift`:

```swift
import Foundation

/// The panel's search box: a substring match over everything a row shows, ignoring case and accents.
public enum PanelFilter {
    public static func isActive(_ query: String) -> Bool {
        !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    public static func matches(query: String, role: EligibleRole, tenantName: String, upn: String) -> Bool {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty else { return true }
        let fields = [role.displayName, role.detail ?? "", role.viaGroup ?? "", tenantName, upn]
        return fields.contains { $0.range(of: q, options: [.caseInsensitive, .diacriticInsensitive]) != nil }
    }
}
```

`Sources/ElevateCore/Support/ExtendWindow.swift`:

```swift
import Foundation

/// Extend is offered only near the end of an activation; earlier it would just shorten the remaining time.
public enum ExtendWindow {
    public static func canExtend(_ assignment: ActiveAssignment, now: Date, within: TimeInterval = 900) -> Bool {
        guard assignment.status == .active, let end = assignment.endDateTime else { return false }
        let left = end.timeIntervalSince(now)
        return left > 0 && left <= within
    }
}
```

- [ ] **Step 4: Run all tests** — `swift test`, expected all pass (116 + 10 new).

- [ ] **Step 5: Commit**

```bash
git add Sources/ElevateCore/Support Tests/ElevateCoreTests
git commit -m "Add PanelStatus, ActiveSummary, PanelFilter and ExtendWindow helpers"
```

---

### Task 2: AssignmentControls with Extend; RoleRow uses it; Azure scope tooltip

**Files:**
- Create: `Sources/ElevateApp/Views/AssignmentControls.swift`
- Modify: `Sources/ElevateApp/Views/RoleRow.swift`

**Interfaces:**
- Consumes: `ExtendWindow.canExtend`, `Countdown`, `model.deactivate/cancelPending/inFlight/isOnline/selectMode`, `PanelRoute.activate`.
- Produces: `struct AssignmentControls: View { let key: RoleKey; let displayName: String; let assignment: ActiveAssignment?; var viewOnlyReason: String? = nil; var allowActivate: Bool = true }` rendering exactly what `RoleRow.trailing` renders today plus Extend.

- [ ] **Step 1: Create `AssignmentControls.swift`**

Move `RoleRow.trailing` and `trailingForStatus` verbatim into the new view, replacing `role.key` with `key`, and add the Extend button inside the `.active` `HStack`, before Deactivate:

```swift
import SwiftUI
import ElevateCore

/// Trailing controls for one assignment, shared by the role rows and the "Active now" summary.
struct AssignmentControls: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let key: RoleKey
    let assignment: ActiveAssignment?
    var viewOnlyReason: String? = nil
    /// The summary never offers Activate (its rows are already active or pending).
    var allowActivate: Bool = true

    var body: some View {
        if model.inFlight.contains(key) {
            ProgressView().controlSize(.small).help("Request in progress")
        } else {
            forStatus
        }
    }

    @ViewBuilder private var forStatus: some View {
        switch assignment?.status {
        case .active:
            let start = assignment?.startDateTime ?? .distantPast
            TimelineView(.periodic(from: .now, by: 1)) { ctx in
                let lockedFor = 300 - ctx.date.timeIntervalSince(start)
                HStack(spacing: 8) {
                    Text(assignment?.endDateTime.flatMap { Countdown.remaining(until: $0, now: ctx.date) }.map(Countdown.label) ?? "")
                        .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                        .frame(width: PanelMetrics.countdownWidth, alignment: .trailing)
                    if let a = assignment, ExtendWindow.canExtend(a, now: ctx.date) {
                        Button("Extend") { open(.activate([key])) }
                            .controlSize(.small)
                            .disabled(!model.isOnline)
                            .help("Extend by activating again; the current activation is replaced")
                    }
                    Button("Deactivate") { Task { await model.deactivate(key) } }
                        .controlSize(.small)
                        .disabled(lockedFor > 0 || !model.isOnline)
                        .help(lockedFor > 0 ? "Can be deactivated in \(Int(lockedFor.rounded(.up))) s (Entra enforces 5 minutes)" : "Deactivate this role now")
                }
            }
        case .pendingApproval:
            Text("awaiting approval").font(.caption).foregroundStyle(.secondary)
            Button("Cancel") { Task { await model.cancelPending(key) } }
                .controlSize(.small).disabled(!model.isOnline).help("Withdraw this request")
        case .pendingProvisioning:
            ProgressView().controlSize(.small)
            Text("provisioning").font(.caption).foregroundStyle(.secondary)
        case .failed(let m):
            Text(m).font(.caption).foregroundStyle(.red).lineLimit(1)
        case nil:
            if let viewOnlyReason {
                Text("cannot activate").font(.caption).foregroundStyle(.secondary).help(viewOnlyReason)
            } else if allowActivate && !model.selectMode {
                Button("Activate") { open(.activate([key])) }.controlSize(.small).disabled(!model.isOnline)
            }
        }
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }
}
```

- [ ] **Step 2: RoleRow uses it and gains the tooltip**

In `RoleRow`, delete `trailing`/`trailingForStatus` and replace the `trailing` call with `AssignmentControls(key: role.key, assignment: assignment, viewOnlyReason: viewOnlyReason)`. Remove the now-unused `openWindow` environment if nothing else uses it. On the caption line change to:

```swift
                if let detail = role.detail {
                    Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(1).help(scopeTooltip ?? detail)
                }
```

with

```swift
    /// Azure captions are shortened to the scope's display name; the full ARM path is one hover away.
    private var scopeTooltip: String? {
        if case .azureResource(let scope, _) = role.key.scope { return scope }
        return nil
    }
```

- [ ] **Step 3: Build** — expected `BUILD SUCCEEDED`.

- [ ] **Step 4: Commit**

```bash
git add Sources/ElevateApp/Views
git commit -m "Extract AssignmentControls with inline Extend; Azure scope tooltip"
```

---

### Task 3: AppModel — summary order, collapsedActive, search state and visibility

**Files:**
- Modify: `Sources/ElevateApp/App/AppSettings.swift`, `Sources/ElevateApp/App/AppModel.swift`

**Interfaces (produces):**
```swift
var activeAssignmentsOrdered: [ActiveAssignment]           // filtered by search when active
var collapsedActive: Bool { get }; func toggleActive()
var searchQuery: String                                    // setting it clears selection
var isFiltering: Bool
func roles(for: TenantKey, tab: PanelTab) -> [EligibleRole] // now honours the filter
func visibleTenants(for identityId: String) -> [TenantContext]
var visibleIdentities: [Identity]
func summaryName(for key: RoleKey) -> String                // role name or scope id fallback
```

- [ ] **Step 1: Settings**

`AppSettings`: `static let collapsedActiveKey = "collapsedActive"`, stored `var collapsedActive: Bool { didSet { defaults.set(collapsedActive, forKey: Self.collapsedActiveKey) } }`, initialised with `defaults.bool(forKey: Self.collapsedActiveKey)`.

- [ ] **Step 2: AppModel**

Add near `panelTab`:

```swift
    var collapsedActive: Bool { settings.collapsedActive }
    func toggleActive() { settings.collapsedActive.toggle() }

    /// Panel search. Not persisted; changing it drops the bulk selection since rows may disappear.
    var searchQuery = "" { didSet { if searchQuery != oldValue { selection.removeAll() } } }
    var isFiltering: Bool { PanelFilter.isActive(searchQuery) }

    private func matchesFilter(_ role: EligibleRole) -> Bool {
        guard isFiltering else { return true }
        let tenantName = tenant(role.key.tenantKey)?.displayName ?? role.key.tenantId
        let upn = identity(role.key.identityId)?.upn ?? ""
        return PanelFilter.matches(query: searchQuery, role: role, tenantName: tenantName, upn: upn)
    }

    func roles(for tenantKey: TenantKey, tab: PanelTab) -> [EligibleRole] {
        let kinds = Self.kinds(for: tab)
        return roles(for: tenantKey).filter { kinds.contains($0.key.scope.kind) && matchesFilter($0) }
    }

    /// While filtering, only tenants with a matching row in the current tab; otherwise all of them.
    func visibleTenants(for identityId: String) -> [TenantContext] {
        let all = tenants(for: identityId)
        guard isFiltering else { return all }
        return all.filter { !roles(for: $0.id, tab: panelTab).isEmpty }
    }

    var visibleIdentities: [Identity] {
        guard isFiltering else { return identities }
        return identities.filter { !visibleTenants(for: $0.id).isEmpty }
    }

    /// Name for the summary row; before the eligible list has loaded only the key is known.
    func summaryName(for key: RoleKey) -> String {
        if let r = role(for: key) { return r.displayName }
        switch key.scope {
        case .entraDirectory(let id, _): return id
        case .azureResource(let scope, let id): return "\(id.components(separatedBy: "/").last ?? id) @ \(scope)"
        case .group(let gid, let access): return "\(gid) (\(access == .owner ? "owner" : "member"))"
        }
    }

    var activeAssignmentsOrdered: [ActiveAssignment] {
        let ordered = ActiveSummary.order(Array(active.values))
        guard isFiltering else { return ordered }
        return ordered.filter { a in
            if let r = role(for: a.roleKey) { return matchesFilter(r) }
            return summaryName(for: a.roleKey).range(of: searchQuery.trimmingCharacters(in: .whitespaces), options: [.caseInsensitive, .diacriticInsensitive]) != nil
        }
    }
```

Note the existing `roles(for:tab:)` is replaced, not duplicated.

- [ ] **Step 3: Build** — expected `BUILD SUCCEEDED`; `swift test` unchanged.

- [ ] **Step 4: Commit**

```bash
git add Sources/ElevateApp/App
git commit -m "Add summary ordering, collapsedActive, search state and visibility filtering to AppModel"
```

---

### Task 4: ActiveSection and PanelView (summary, search field, visible sets)

**Files:**
- Create: `Sources/ElevateApp/Views/ActiveSection.swift`
- Modify: `Sources/ElevateApp/Views/PanelView.swift`

**Interfaces:** consumes everything from Tasks 2-3.

- [ ] **Step 1: ActiveSection**

```swift
import SwiftUI
import ElevateCore

/// Pinned "Active now" summary across all accounts and tenants, above the account list.
struct ActiveSection: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let items = model.activeAssignmentsOrdered
        if !items.isEmpty {
            Section {
                if !model.collapsedActive {
                    ForEach(items) { a in ActiveRow(assignment: a) }
                }
            } header: {
                ActiveHeader(count: items.count)
            }
        }
    }
}

struct ActiveHeader: View {
    @Environment(AppModel.self) private var model
    let count: Int
    var body: some View {
        let expanded = !model.collapsedActive
        HStack(spacing: 6) {
            Button { withAnimation(.snappy) { model.toggleActive() } } label: {
                HStack(spacing: 6) {
                    Image(systemName: "chevron.right").rotationEffect(.degrees(expanded ? 90 : 0))
                        .font(.caption.weight(.semibold)).foregroundStyle(.secondary).frame(width: 12)
                    Text("Active now").font(.subheadline.weight(.semibold))
                    Text("\(count)").font(.caption).foregroundStyle(.green)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(expanded ? "Collapse active now" : "Expand active now")
            Spacer()
        }
        .padding(.horizontal, PanelMetrics.headerInset)
        .padding(.vertical, 6)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.regularMaterial)
        .overlay(alignment: .bottom) { Divider() }
    }
}

struct ActiveRow: View {
    @Environment(AppModel.self) private var model
    let assignment: ActiveAssignment

    var body: some View {
        let key = assignment.roleKey
        HStack(spacing: 8) {
            Circle().fill(assignment.status == .active ? .green : .yellow).frame(width: 8, height: 8)
            VStack(alignment: .leading, spacing: 1) {
                Text(model.summaryName(for: key)).font(.body).lineLimit(1)
                Text("\(model.tenant(key.tenantKey)?.displayName ?? key.tenantId) · \(model.identity(key.identityId)?.upn ?? key.identityId)")
                    .font(.caption2).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer()
            AssignmentControls(key: key, assignment: assignment, allowActivate: false)
        }
        .frame(minHeight: 28)
        .padding(.vertical, 3)
        .padding(.leading, PanelMetrics.roleInset)
        .padding(.trailing, PanelMetrics.trailingInset)
    }
}
```

- [ ] **Step 2: PanelView**

Inside the `LazyVStack`, before `ForEach(model.identities)`, add `ActiveSection()`. Change the loop to `ForEach(model.visibleIdentities)` and `let tenants = model.visibleTenants(for: identity.id)`. When filtering and `model.visibleIdentities.isEmpty`, show `Text("No matches").font(.caption).foregroundStyle(.secondary).padding(12)` instead of the list (keep the height measurement wrapper).

Header: add, before the select toggle,

```swift
            Toggle(isOn: $showSearch) { Image(systemName: "magnifyingglass") }
                .toggleStyle(.button)
                .help("Filter roles and groups")
                .accessibilityLabel("Search")
```

with `@State private var showSearch = false` and `@FocusState private var searchFocused: Bool`. Under the segmented control (after its `Divider()`), when `showSearch`:

```swift
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Filter roles", text: Binding(get: { model.searchQuery }, set: { model.searchQuery = $0 }))
                    .textFieldStyle(.plain)
                    .focused($searchFocused)
                    .onExitCommand { closeSearch() }
                if !model.searchQuery.isEmpty {
                    Button { model.searchQuery = "" } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.plain).foregroundStyle(.secondary).accessibilityLabel("Clear filter")
                }
            }
            .padding(.horizontal, 12).padding(.vertical, 6)
            Divider()
```

with `.onChange(of: showSearch) { _, on in if on { searchFocused = true } else { model.searchQuery = "" } }` on the outer `VStack` and `private func closeSearch() { showSearch = false }`.

- [ ] **Step 3: Build, relaunch, look** — `BUILD SUCCEEDED`; open the panel: summary shows when a role is active; search toggle shows the field.

- [ ] **Step 4: Commit**

```bash
git add Sources/ElevateApp/Views
git commit -m "Add the Active now summary and the panel search field"
```

---

### Task 5: Menu bar badges

**Files:**
- Modify: `Sources/ElevateApp/App/ElevateApp.swift` (`MenuBarLabel`)

- [ ] **Step 1: Replace `MenuBarLabel.body`**

```swift
    var body: some View {
        TimelineView(.periodic(from: .now, by: 30)) { ctx in
            let status = PanelStatus.compute(Array(model.active.values), now: ctx.date)
            HStack(spacing: 3) {
                Image(systemName: Self.symbol(for: status))
                if status.activeCount > 0 { Text("\(status.activeCount)").monospacedDigit() }
                if status.pendingApproval { Image(systemName: "clock").font(.caption2) }
            }
            .accessibilityLabel(Self.description(of: status))
        }
        .onChange(of: model.pendingExtend) { _, key in
            guard let key else { return }
            openWindow(value: PanelRoute.activate([key]))
            NSApp.activate(ignoringOtherApps: true)
            model.pendingExtend = nil
        }
    }

    static func symbol(for s: PanelStatus) -> String {
        if s.expiringSoon { return "exclamationmark.shield.fill" }
        return s.activeCount > 0 ? "checkmark.shield.fill" : "shield"
    }

    static func description(of s: PanelStatus) -> String {
        var parts = [s.activeCount == 0 ? "No active roles" : "\(s.activeCount) active"]
        if s.expiringSoon { parts.append("one expiring soon") }
        if s.pendingApproval { parts.append("one awaiting approval") }
        return parts.joined(separator: ", ")
    }
```

`model.active` is `private(set)`, readable from views. Build, relaunch, commit:

```bash
git add Sources/ElevateApp/App/ElevateApp.swift
git commit -m "Menu bar label: expiring and pending badges from PanelStatus"
```

---

### Task 6: Expired notification with Activate again

**Files:**
- Modify: `Sources/ElevateApp/Notifications/ExpiryNotifier.swift`

- [ ] **Step 1: Implement**

Add constants `static let expiredCategoryId = "PIMTRAY_EXPIRED"`, `static let activateAgainAction = "PIMTRAY_ACTIVATE_AGAIN"`, `static let expiredDelay: TimeInterval = 5`. In `init`, register both categories:

```swift
        let extend = UNNotificationAction(identifier: Self.extendAction, title: "Extend", options: [.foreground])
        let again = UNNotificationAction(identifier: Self.activateAgainAction, title: "Activate again", options: [.foreground])
        center.setNotificationCategories([
            UNNotificationCategory(identifier: Self.categoryId, actions: [extend], intentIdentifiers: []),
            UNNotificationCategory(identifier: Self.expiredCategoryId, actions: [again], intentIdentifiers: []),
        ])
```

In `reschedule`, factor the request creation into `private func add(_ center: UNUserNotificationCenter, id: String, title: String, body: String, category: String, key: RoleKey, at fireAt: Date) async` and call it twice per active assignment: the existing warning (`expiry-<id>`, `categoryId`, at `end - leadTime`) and the new one (`expired-<id>`, `expiredCategoryId`, title "<name> expired", at `end + expiredDelay`). Keep the `delay > 1` guard inside `add`.

In `didReceive`, accept `Self.activateAgainAction` alongside the existing identifiers (all route to `onExtend`).

- [ ] **Step 2: Build, relaunch, commit**

```bash
git add Sources/ElevateApp/Notifications/ExpiryNotifier.swift
git commit -m "Notify on expiry with an Activate again action"
```

---

### Task 7: Final checks and docs

- [ ] `swift test` (126 expected) and app build; relaunch.
- [ ] `macos/README.md`: add three sentences to the panel description: the Active now summary, the search field, and that Extend appears within 15 minutes of expiry and the expiry notification offers Activate again. Commit "Document the Active now summary, search and Extend".
