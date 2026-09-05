# Elevate Activation Profiles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Save a cross-tab, cross-account selection of roles and groups as a named profile, show profiles as chips under the tabs, and run one with a confirmation sheet.

**Architecture:** `ElevateCore` gains the `ActivationProfile` model on `AppState` (with a tolerant decoder), a pure `ProfilePlanner` that turns a profile into per-entry plan items (duration precedence, skip dispositions) and a `ProfileSummary` caption helper. `AppModel` keeps the selection across tabs, owns save/update/rename/delete/move/run, and exposes `editingProfileId`. Three routed windows (`SaveProfileView`, `RunProfileView`, `ManageProfilesView`) and a `ProfilesRow` in the panel complete it.

**Tech Stack:** Swift 6.2, SwiftUI macOS 26, Swift Testing. XcodeGen project in `macos/`.

**Spec:** `docs/superpowers/specs/2026-09-05-elevate-profiles-design.md`

## Global Constraints

- Paths relative to `macos/`. Tests: `swift test`. Build: `xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build 2>&1 | grep -E "error:|BUILD"`. Relaunch: `pkill -x Elevate; sleep 1; open build/Build/Products/Debug/Elevate.app`.
- `ElevateCore` imports only Foundation. Swift 6 strict concurrency; no `@unchecked Sendable` in `AppModel`. Never commit `Elevate.xcodeproj`. Swift Testing.
- `RoleKey`, `RoleScope` (`.entraDirectory`, `.azureResource`, `.group`), `RoleScopeKind`, `EligibleRole`, `RolePolicy` (`defaultDuration`, `maximumDuration`, `requiresApproval`, `.manualDefault`), `ActiveAssignment.Status`, `RoleMemory { roleKey, justification, lastDuration }`, `ActivationRequest(roleKey:duration:justification:ticket:authenticationContext:)`, `TicketInfo(number:system:)` exist.
- Selection: kept across tab switches; cleared when select mode turns off and when the search query changes.
- Branch `profiles` from `main`; commit after every task with the given message.

## File structure

```
Sources/ElevateCore/Models/ActivationProfile.swift     new: model + ProfileSummary
Sources/ElevateCore/Storage/AppState.swift             + profiles, tolerant init(from:), helpers
Sources/ElevateCore/Coordination/ProfilePlanner.swift  new: pure planning
Sources/ElevateApp/App/AppModel.swift                  profile operations, editingProfileId, selection rules
Sources/ElevateApp/App/PanelRoute.swift                + saveProfile/runProfile/manageProfiles
Sources/ElevateApp/Views/RouteWindow.swift             routes
Sources/ElevateApp/Views/ProfilesRow.swift             new: chips under the tabs
Sources/ElevateApp/Views/SaveProfileView.swift         new
Sources/ElevateApp/Views/RunProfileView.swift          new
Sources/ElevateApp/Views/ManageProfilesView.swift      new
Sources/ElevateApp/Views/PanelView.swift               profiles row, bulk bar buttons
Tests/ElevateCoreTests/{ActivationProfileTests,ProfilePlannerTests}.swift
```

---

### Task 1: Core — ActivationProfile, AppState.profiles, ProfileSummary

**Files:**
- Create: `Sources/ElevateCore/Models/ActivationProfile.swift`
- Modify: `Sources/ElevateCore/Storage/AppState.swift`
- Test: `Tests/ElevateCoreTests/ActivationProfileTests.swift`

**Interfaces (produces):**
```swift
public struct ActivationProfile: Codable, Hashable, Sendable, Identifiable {
    public struct Entry: Codable, Hashable, Sendable { public var roleKey: RoleKey; public var lastDuration: Duration?; public init(roleKey:lastDuration:) }
    public var id: UUID; public var name: String; public var entries: [Entry]; public var lastJustification: String?
    public init(id: UUID = UUID(), name: String, entries: [Entry], lastJustification: String? = nil)
}
public enum ProfileSummary { public static func caption(entries: [ActivationProfile.Entry]) -> String }
// AppState
public var profiles: [ActivationProfile]
public func profile(id: UUID) -> ActivationProfile?
public mutating func upsertProfile(_ p: ActivationProfile)
public mutating func removeProfile(id: UUID)
public mutating func moveProfile(fromOffsets: IndexSet, toOffset: Int)
```

- [ ] **Step 1: Failing tests**

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct ActivationProfileTests {
    func key(_ n: String, kind: RoleScopeKind = .entraDirectory) -> RoleKey {
        switch kind {
        case .entraDirectory: RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: n, directoryScopeId: "/"))
        case .azureResource: RoleKey(identityId: "i", tenantId: "t", scope: .azureResource(scope: "/subscriptions/s", roleDefinitionId: n))
        case .group: RoleKey(identityId: "i", tenantId: "t", scope: .group(groupId: n, accessId: .member))
        }
    }

    @Test func stateWithoutProfilesDecodes() throws {
        let json = #"{"identities":[],"tenants":[],"manualRoles":[],"memory":[]}"#
        let s = try JSONDecoder().decode(AppState.self, from: Data(json.utf8))
        #expect(s.profiles.isEmpty)
        let minimal = try JSONDecoder().decode(AppState.self, from: Data("{}".utf8))
        #expect(minimal.identities.isEmpty && minimal.profiles.isEmpty)
    }

    @Test func profilesRoundTripAndHelpers() throws {
        var s = AppState()
        let p = ActivationProfile(name: "Ops", entries: [.init(roleKey: key("a"), lastDuration: .seconds(3600))], lastJustification: "INC")
        s.upsertProfile(p)
        s.upsertProfile(ActivationProfile(name: "Second", entries: []))
        let decoded = try JSONDecoder().decode(AppState.self, from: JSONEncoder().encode(s))
        #expect(decoded.profiles.count == 2)
        #expect(decoded.profile(id: p.id)?.entries.first?.lastDuration == .seconds(3600))
        var renamed = p; renamed.name = "Ops 2"
        s.upsertProfile(renamed)
        #expect(s.profiles.count == 2 && s.profile(id: p.id)?.name == "Ops 2")
        s.moveProfile(fromOffsets: IndexSet(integer: 1), toOffset: 0)
        #expect(s.profiles.first?.name == "Second")
        s.removeProfile(id: p.id)
        #expect(s.profiles.count == 1)
    }

    @Test func summaryCaption() {
        #expect(ProfileSummary.caption(entries: [.init(roleKey: key("a"), lastDuration: nil)]) == "1 role")
        #expect(ProfileSummary.caption(entries: [.init(roleKey: key("a"), lastDuration: nil), .init(roleKey: key("b", kind: .azureResource), lastDuration: nil)]) == "2 roles")
        #expect(ProfileSummary.caption(entries: [.init(roleKey: key("g", kind: .group), lastDuration: nil)]) == "1 group")
        #expect(ProfileSummary.caption(entries: [.init(roleKey: key("a"), lastDuration: nil), .init(roleKey: key("g", kind: .group), lastDuration: nil), .init(roleKey: key("h", kind: .group), lastDuration: nil)]) == "1 role · 2 groups")
        #expect(ProfileSummary.caption(entries: []) == "empty")
    }
}
```

- [ ] **Step 2: Run** `swift test --filter ActivationProfileTests` — expected compile failure.

- [ ] **Step 3: Implement**

`Sources/ElevateCore/Models/ActivationProfile.swift`:

```swift
import Foundation

/// A named set of roles and groups activated together, across accounts and tenants.
public struct ActivationProfile: Codable, Hashable, Sendable, Identifiable {
    public struct Entry: Codable, Hashable, Sendable {
        public var roleKey: RoleKey
        /// Duration used on the last run of this entry; nil until the profile has run.
        public var lastDuration: Duration?
        public init(roleKey: RoleKey, lastDuration: Duration? = nil) {
            self.roleKey = roleKey
            self.lastDuration = lastDuration
        }
    }

    public var id: UUID
    public var name: String
    public var entries: [Entry]
    /// Reason entered on the last run; prefilled next time.
    public var lastJustification: String?

    public init(id: UUID = UUID(), name: String, entries: [Entry], lastJustification: String? = nil) {
        self.id = id
        self.name = name
        self.entries = entries
        self.lastJustification = lastJustification
    }
}

public enum ProfileSummary {
    /// "3 roles · 1 group" style caption for a chip. Entra and Azure count as roles.
    public static func caption(entries: [ActivationProfile.Entry]) -> String {
        let groups = entries.filter { $0.roleKey.scope.kind == .group }.count
        let roles = entries.count - groups
        var parts: [String] = []
        if roles > 0 { parts.append("\(roles) role\(roles == 1 ? "" : "s")") }
        if groups > 0 { parts.append("\(groups) group\(groups == 1 ? "" : "s")") }
        return parts.isEmpty ? "empty" : parts.joined(separator: " · ")
    }
}
```

`AppState`: add `public var profiles: [ActivationProfile] = []`, a `CodingKeys` enum listing the five keys, and

```swift
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        identities = try c.decodeIfPresent([Identity].self, forKey: .identities) ?? []
        tenants = try c.decodeIfPresent([TenantContext].self, forKey: .tenants) ?? []
        manualRoles = try c.decodeIfPresent([ManualRole].self, forKey: .manualRoles) ?? []
        memory = try c.decodeIfPresent([RoleMemory].self, forKey: .memory) ?? []
        profiles = try c.decodeIfPresent([ActivationProfile].self, forKey: .profiles) ?? []
    }

    public func profile(id: UUID) -> ActivationProfile? { profiles.first { $0.id == id } }
    public mutating func upsertProfile(_ p: ActivationProfile) {
        if let i = profiles.firstIndex(where: { $0.id == p.id }) { profiles[i] = p } else { profiles.append(p) }
    }
    public mutating func removeProfile(id: UUID) { profiles.removeAll { $0.id == id } }
    public mutating func moveProfile(fromOffsets: IndexSet, toOffset: Int) { profiles.move(fromOffsets: fromOffsets, toOffset: toOffset) }
```

Encoding stays synthesized (the custom decoder does not remove it as long as `CodingKeys` covers all stored properties). `removeTenant` should also drop entries of that tenant from every profile: `for i in profiles.indices { profiles[i].entries.removeAll { $0.roleKey.tenantKey == key } }`. `removeIdentity` (exists) likewise by `identityId`.

- [ ] **Step 4: Run** `swift test` — all pass.

- [ ] **Step 5: Commit** `git commit -m "Add ActivationProfile to AppState with a tolerant decoder"`.

---

### Task 2: Core — ProfilePlanner

**Files:**
- Create: `Sources/ElevateCore/Coordination/ProfilePlanner.swift`
- Test: `Tests/ElevateCoreTests/ProfilePlannerTests.swift`

**Interfaces (produces):**
```swift
public struct ProfilePlanItem: Hashable, Sendable, Identifiable {
    public enum Disposition: Hashable, Sendable { case activate, alreadyActive, pending, notEligible }
    public let roleKey: RoleKey; public let role: EligibleRole?; public var duration: Duration; public let disposition: Disposition
    public var id: RoleKey { roleKey }
}
public enum ProfilePlanner {
    public static func plan(_ profile: ActivationProfile, roles: [RoleKey: EligibleRole], active: [RoleKey: ActiveAssignment], memory: [RoleKey: RoleMemory]) -> [ProfilePlanItem]
}
```

- [ ] **Step 1: Failing tests**

```swift
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

    @Test func durationPrecedenceAndCap() {
        let profile = ActivationProfile(name: "p", entries: [
            .init(roleKey: k1, lastDuration: .seconds(8 * 3600)),   // capped to the 4 h maximum
            .init(roleKey: k2, lastDuration: nil),                   // falls to memory
            .init(roleKey: k3, lastDuration: nil),                   // falls to policy default
        ])
        let roles = [k1: role(k1), k2: role(k2), k3: role(k3)]
        let memory = [k2: RoleMemory(roleKey: k2, justification: "x", lastDuration: .seconds(1800))]
        let items = ProfilePlanner.plan(profile, roles: roles, active: [:], memory: memory)
        #expect(items.map(\.duration) == [.seconds(4 * 3600), .seconds(1800), .seconds(3600)])
        #expect(items.allSatisfy { $0.disposition == .activate })
        #expect(items.map(\.roleKey) == [k1, k2, k3])   // profile order preserved
    }

    @Test func dispositions() {
        let profile = ActivationProfile(name: "p", entries: [k1, k2, k3, k4].map { .init(roleKey: $0) })
        let roles = [k1: role(k1), k2: role(k2), k3: role(k3)]
        let active = [k1: assignment(k1, .active), k2: assignment(k2, .pendingApproval), k3: assignment(k3, .pendingProvisioning)]
        let items = ProfilePlanner.plan(profile, roles: roles, active: active, memory: [:])
        #expect(items.map(\.disposition) == [.alreadyActive, .pending, .pending, .notEligible])
        #expect(items[3].role == nil)
        #expect(items[3].duration == RolePolicy.manualDefault.defaultDuration)
    }
}
```

- [ ] **Step 2: Run** — compile failure expected.

- [ ] **Step 3: Implement**

```swift
import Foundation

/// One line of a profile run: what will happen to an entry and with which duration.
public struct ProfilePlanItem: Hashable, Sendable, Identifiable {
    public enum Disposition: Hashable, Sendable { case activate, alreadyActive, pending, notEligible }
    public let roleKey: RoleKey
    public let role: EligibleRole?
    public var duration: Duration
    public let disposition: Disposition
    public var id: RoleKey { roleKey }
    public init(roleKey: RoleKey, role: EligibleRole?, duration: Duration, disposition: Disposition) {
        self.roleKey = roleKey; self.role = role; self.duration = duration; self.disposition = disposition
    }
}

public enum ProfilePlanner {
    /// Duration: the entry's last run, else the role's remembered duration, else the policy default; never above the maximum.
    public static func plan(_ profile: ActivationProfile, roles: [RoleKey: EligibleRole],
                            active: [RoleKey: ActiveAssignment], memory: [RoleKey: RoleMemory]) -> [ProfilePlanItem] {
        profile.entries.map { entry in
            let role = roles[entry.roleKey]
            let policy = role?.policy ?? .manualDefault
            let wanted = entry.lastDuration ?? memory[entry.roleKey]?.lastDuration ?? policy.defaultDuration
            let duration = min(wanted, policy.maximumDuration)
            let disposition: ProfilePlanItem.Disposition
            if role == nil { disposition = .notEligible }
            else if let a = active[entry.roleKey] {
                switch a.status {
                case .active: disposition = .alreadyActive
                case .pendingApproval, .pendingProvisioning: disposition = .pending
                case .failed: disposition = .activate
                }
            } else { disposition = .activate }
            return ProfilePlanItem(roleKey: entry.roleKey, role: role, duration: duration, disposition: disposition)
        }
    }
}
```

- [ ] **Step 4: Run** `swift test` — all pass. **Step 5: Commit** `git commit -m "Add ProfilePlanner"`.

---

### Task 3: AppModel and routes

**Files:**
- Modify: `Sources/ElevateApp/App/AppModel.swift`, `Sources/ElevateApp/App/PanelRoute.swift`, `Sources/ElevateApp/Views/RouteWindow.swift`

**Interfaces (produces):**
```swift
var profiles: [ActivationProfile]
var editingProfileId: UUID?
var selectionCount: Int
var selectionNoun: String                        // "role" | "group" | "item"
func saveProfile(name: String, keys: [RoleKey]) -> ActivationProfile
func updateProfile(id: UUID, keys: [RoleKey])
func renameProfile(id: UUID, name: String)
func deleteProfile(id: UUID)
func moveProfile(fromOffsets: IndexSet, toOffset: Int)
func beginEditing(profileId: UUID)               // selects its keys, enters select mode
func plan(for profileId: UUID) -> [ProfilePlanItem]
func runProfile(id: UUID, items: [ProfilePlanItem], justification: String, ticket: TicketInfo?) async
// PanelRoute: case saveProfile([RoleKey]), runProfile(UUID), manageProfiles
```

- [ ] **Step 1: Selection rules**

Change `panelTab`'s setter to no longer clear `selection`. Keep clearing on `selectMode = false` and on `searchQuery` change; when `selectMode` turns off also set `editingProfileId = nil`. Add `var selectionCount: Int { selection.count }` and

```swift
    var selectionNoun: String {
        let kinds = Set(selection.map(\.scope.kind))
        if kinds.isEmpty || kinds == [.group] { return kinds.isEmpty ? "role" : "group" }
        return kinds.contains(.group) ? "item" : "role"
    }
```

- [ ] **Step 2: Profile operations** (new `// MARK: Profiles` section)

```swift
    var profiles: [ActivationProfile] { state.profiles }
    var editingProfileId: UUID?

    private func orderedKeys(_ keys: [RoleKey]) -> [RoleKey] {
        // Stable, readable order: by account, then tenant, then kind, then name.
        keys.sorted { a, b in
            let ia = identity(a.identityId)?.upn ?? "", ib = identity(b.identityId)?.upn ?? ""
            if ia != ib { return ia < ib }
            let ta = tenant(a.tenantKey)?.displayName ?? "", tb = tenant(b.tenantKey)?.displayName ?? ""
            if ta != tb { return ta < tb }
            if a.scope.kind != b.scope.kind { return a.scope.kind.rawValue < b.scope.kind.rawValue }
            return (role(for: a)?.displayName ?? "") < (role(for: b)?.displayName ?? "")
        }
    }

    @discardableResult
    func saveProfile(name: String, keys: [RoleKey]) -> ActivationProfile {
        let entries = orderedKeys(keys).map { ActivationProfile.Entry(roleKey: $0, lastDuration: remembered(for: $0)?.lastDuration) }
        let profile = ActivationProfile(name: name.trimmingCharacters(in: .whitespacesAndNewlines), entries: entries)
        state.upsertProfile(profile); persist()
        return profile
    }

    func updateProfile(id: UUID, keys: [RoleKey]) {
        guard var p = state.profile(id: id) else { return }
        let old = Dictionary(uniqueKeysWithValues: p.entries.map { ($0.roleKey, $0) })
        p.entries = orderedKeys(keys).map { old[$0] ?? ActivationProfile.Entry(roleKey: $0, lastDuration: remembered(for: $0)?.lastDuration) }
        state.upsertProfile(p); persist()
    }

    func renameProfile(id: UUID, name: String) {
        guard var p = state.profile(id: id) else { return }
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        p.name = trimmed; state.upsertProfile(p); persist()
    }

    func deleteProfile(id: UUID) { state.removeProfile(id: id); persist() }
    func moveProfile(fromOffsets: IndexSet, toOffset: Int) { state.moveProfile(fromOffsets: fromOffsets, toOffset: toOffset); persist() }

    /// Edit = reopen the selection. The bulk bar offers "Update profile" while `editingProfileId` is set.
    func beginEditing(profileId: UUID) {
        guard let p = state.profile(id: profileId) else { return }
        selectMode = true
        selection = Set(p.entries.map(\.roleKey))
        editingProfileId = profileId
    }

    func plan(for profileId: UUID) -> [ProfilePlanItem] {
        guard let p = state.profile(id: profileId) else { return [] }
        var rolesByKey: [RoleKey: EligibleRole] = [:]
        for list in roles.values { for r in list { rolesByKey[r.key] = r } }
        let memoryByKey = Dictionary(uniqueKeysWithValues: state.memory.map { ($0.roleKey, $0) })
        return ProfilePlanner.plan(p, roles: rolesByKey, active: active, memory: memoryByKey)
    }

    /// Activates the plan's `.activate` items, then remembers the reason and each duration on the profile.
    func runProfile(id: UUID, items: [ProfilePlanItem], justification: String, ticket: TicketInfo?) async {
        let requests = items.filter { $0.disposition == .activate }.map {
            ActivationRequest(roleKey: $0.roleKey, duration: $0.duration, justification: justification, ticket: ticket,
                              authenticationContext: $0.role?.policy.authenticationContext)
        }
        guard !requests.isEmpty else { return }
        await activate(requests)
        guard var p = state.profile(id: id) else { return }
        p.lastJustification = justification
        for item in items { if let i = p.entries.firstIndex(where: { $0.roleKey == item.roleKey }) { p.entries[i].lastDuration = item.duration } }
        state.upsertProfile(p); persist()
    }
```

`activate(_:)` already records per-role memory and clears select mode.

- [ ] **Step 3: Routes**

`PanelRoute`: add `case saveProfile([RoleKey])`, `case runProfile(UUID)`, `case manageProfiles`. `RouteWindow`: `case .saveProfile(let keys): SaveProfileView(keys: keys)`, `case .runProfile(let id): RunProfileView(profileId: id)`, `case .manageProfiles: ManageProfilesView()`. Create the three views as placeholders in this task so the build passes:

```swift
struct SaveProfileView: View { let keys: [RoleKey]; var body: some View { Text("Save profile").padding() } }
struct RunProfileView: View { let profileId: UUID; var body: some View { Text("Run profile").padding() } }
struct ManageProfilesView: View { var body: some View { Text("Profiles").padding() } }
```

each in its own file under `Views/` (Task 4 fills them in).

- [ ] **Step 4: Build** — `BUILD SUCCEEDED`; `swift test` unchanged. **Step 5: Commit** `git commit -m "Profile operations, cross-tab selection and profile routes in AppModel"`.

---

### Task 4: Views — ProfilesRow, SaveProfileView, RunProfileView, ManageProfilesView, bulk bar

**Files:**
- Create: `Sources/ElevateApp/Views/ProfilesRow.swift`
- Replace placeholders: `Views/SaveProfileView.swift`, `Views/RunProfileView.swift`, `Views/ManageProfilesView.swift`
- Modify: `Sources/ElevateApp/Views/PanelView.swift`

**Interfaces:** consumes everything from Task 3, `DurationPicker(duration:maximum:)` from `ActivationView.swift`, `model.progress[key]` (`ActivationOutcome.Result`), `model.inFlight`.

- [ ] **Step 1: ProfilesRow**

```swift
import SwiftUI
import ElevateCore

/// One chip per saved profile under the tab picker; hidden when there are none.
struct ProfilesRow: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        if !model.profiles.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text("PROFILES").font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                    Spacer()
                    Button("Manage…") { open(.manageProfiles) }.buttonStyle(.plain).font(.caption).foregroundStyle(Color.accentColor)
                }
                FlowLayout(spacing: 6) {
                    ForEach(model.profiles) { p in
                        Button { open(.runProfile(p.id)) } label: {
                            HStack(spacing: 5) {
                                Image(systemName: "bolt.fill").font(.caption2).foregroundStyle(Color.accentColor)
                                Text(p.name).font(.caption.weight(.medium))
                                Text(ProfileSummary.caption(entries: p.entries)).font(.caption2).foregroundStyle(.secondary)
                            }
                            .padding(.horizontal, 9).padding(.vertical, 4)
                            .background(.background, in: Capsule())
                            .overlay(Capsule().strokeBorder(.quaternary))
                        }
                        .buttonStyle(.plain)
                        .help("Run \(p.name)")
                    }
                }
            }
            .padding(.horizontal, 12).padding(.vertical, 8)
            Divider()
        }
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }
}

/// Minimal wrapping layout for chips.
struct FlowLayout: Layout {
    var spacing: CGFloat = 6
    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? 380
        var x: CGFloat = 0, y: CGFloat = 0, rowHeight: CGFloat = 0
        for s in subviews {
            let size = s.sizeThatFits(.unspecified)
            if x + size.width > width, x > 0 { x = 0; y += rowHeight + spacing; rowHeight = 0 }
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
        return CGSize(width: width, height: y + rowHeight)
    }
    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, rowHeight: CGFloat = 0
        for s in subviews {
            let size = s.sizeThatFits(.unspecified)
            if x + size.width > bounds.maxX, x > bounds.minX { x = bounds.minX; y += rowHeight + spacing; rowHeight = 0 }
            s.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
    }
}
```

In `PanelView`, place `ProfilesRow()` directly after the tab picker's `Divider()` (before the search row).

- [ ] **Step 2: Bulk bar**

Replace the select-mode bar with:

```swift
            if model.selectMode {
                Divider()
                HStack(spacing: 8) {
                    if let editing = model.editingProfileId {
                        Button("Update profile") {
                            model.updateProfile(id: editing, keys: Array(model.selection))
                            model.selectMode = false
                        }
                        .disabled(model.selection.isEmpty)
                    } else {
                        Button("Save as profile…") { open(.saveProfile(Array(model.selection))) }
                            .disabled(model.selection.isEmpty)
                    }
                    Button {
                        open(.activate(Array(model.selection).sorted { "\($0)" < "\($1)" }))
                    } label: {
                        Text("Activate \(model.selectionCount) \(model.selectionNoun)\(model.selectionCount == 1 ? "" : "s")").frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(model.selection.isEmpty)
                }
                .padding(10)
            }
```

with a private `open(_:)` helper as in `ProfilesRow` (PanelView already has `openWindow`).

- [ ] **Step 3: SaveProfileView**

```swift
import SwiftUI
import ElevateCore

struct SaveProfileView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let keys: [RoleKey]
    @State private var name = ""

    private var grouped: [(TenantKey, [RoleKey])] {
        var order: [TenantKey] = []; var map: [TenantKey: [RoleKey]] = [:]
        for k in keys { if map[k.tenantKey] == nil { order.append(k.tenantKey) }; map[k.tenantKey, default: []].append(k) }
        return order.map { ($0, map[$0]!) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Save as profile").font(.title3.weight(.semibold))
            TextField("Profile name", text: $name).textFieldStyle(.roundedBorder)
            VStack(alignment: .leading, spacing: 4) {
                ForEach(grouped, id: \.0) { tk, tkeys in
                    Text("\(model.identity(tk.identityId)?.upn ?? tk.identityId) · \(model.tenant(tk)?.displayName ?? tk.tenantId)")
                        .font(.caption.weight(.semibold)).foregroundStyle(.secondary).padding(.top, 4)
                    ForEach(tkeys, id: \.self) { k in
                        HStack {
                            Text(model.summaryName(for: k))
                            Spacer()
                            if let d = model.remembered(for: k)?.lastDuration { Text("last \(Countdown.label(d))").font(.caption).foregroundStyle(.secondary) }
                        }
                    }
                }
            }
            .padding(8).background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Save") { model.saveProfile(name: name, keys: keys); model.selectMode = false; dismiss() }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                    .disabled(name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(16).frame(width: 420)
    }
}
```

- [ ] **Step 4: RunProfileView**

```swift
import SwiftUI
import ElevateCore

struct RunProfileView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let profileId: UUID
    @State private var items: [ProfilePlanItem] = []
    @State private var justification = ""
    @State private var ticketNumber = ""
    @State private var ticketSystem = ""
    @State private var running = false
    @State private var finished = false

    private var profile: ActivationProfile? { model.profiles.first { $0.id == profileId } }
    private var toActivate: [ProfilePlanItem] { items.filter { $0.disposition == .activate } }
    private var needsTicket: Bool { toActivate.contains { $0.role?.policy.requiresTicket == true } }
    private var justificationRequired: Bool { toActivate.contains { $0.role?.policy.requiresJustification == true } }
    private var canSubmit: Bool {
        !running && !finished && !toActivate.isEmpty
            && (!justificationRequired || !justification.trimmingCharacters(in: .whitespaces).isEmpty)
            && (!needsTicket || !ticketNumber.trimmingCharacters(in: .whitespaces).isEmpty)
    }
    private var groupedTenantKeys: [TenantKey] {
        var seen: [TenantKey] = []
        for i in items where !seen.contains(i.roleKey.tenantKey) { seen.append(i.roleKey.tenantKey) }
        return seen
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 6) {
                Image(systemName: "bolt.fill").foregroundStyle(Color.accentColor)
                Text(finished ? "Ran \"\(profile?.name ?? "profile")\"" : "Run \"\(profile?.name ?? "profile")\"").font(.title3.weight(.semibold))
            }
            VStack(alignment: .leading, spacing: 4) {
                ForEach(groupedTenantKeys, id: \.self) { tk in
                    Text("\(model.identity(tk.identityId)?.upn ?? tk.identityId) · \(model.tenant(tk)?.displayName ?? tk.tenantId)")
                        .font(.caption.weight(.semibold)).foregroundStyle(.secondary).padding(.top, 4)
                    ForEach($items) { $item in
                        if item.roleKey.tenantKey == tk { row($item) }
                    }
                }
            }
            if !finished {
                TextField("Reason", text: $justification, axis: .vertical).lineLimit(2...4)
                if needsTicket {
                    HStack { TextField("Ticket number", text: $ticketNumber); TextField("Ticket system", text: $ticketSystem) }
                }
            }
            HStack(alignment: .firstTextBaseline) {
                Text("Entries already active are skipped. Approval-required entries are requested and shown as pending.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Spacer()
                if finished {
                    Button("Done") { dismiss() }.keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                } else {
                    Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction).disabled(running)
                    Button("Activate \(toActivate.count)") { Task { await submit() } }
                        .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent).disabled(!canSubmit)
                }
            }
        }
        .padding(16).frame(width: 560)
        .onAppear(perform: load)
    }

    @ViewBuilder private func row(_ item: Binding<ProfilePlanItem>) -> some View {
        let it = item.wrappedValue
        HStack(spacing: 8) {
            Text(it.role?.displayName ?? model.summaryName(for: it.roleKey)).opacity(it.disposition == .activate ? 1 : 0.6)
            Spacer()
            switch it.disposition {
            case .activate:
                if !finished {
                    DurationPicker(duration: item.duration, maximum: it.role?.policy.maximumDuration ?? RolePolicy.manualDefault.maximumDuration).frame(width: 150)
                } else {
                    Text(Countdown.label(it.duration)).font(.caption).foregroundStyle(.secondary).frame(width: 150, alignment: .trailing)
                }
                statusLabel(for: it).frame(width: 130, alignment: .trailing)
            case .alreadyActive: Text("already active · skipped").font(.caption).foregroundStyle(.secondary)
            case .pending: Text("pending · skipped").font(.caption).foregroundStyle(.secondary)
            case .notEligible: Text("not eligible · skipped").font(.caption).foregroundStyle(.orange)
            }
        }
    }

    @ViewBuilder private func statusLabel(for it: ProfilePlanItem) -> some View {
        switch model.progress[it.roleKey] {
        case .activated: Label("Active", systemImage: "checkmark.circle.fill").foregroundStyle(.green).font(.caption)
        case .pendingApproval: Label("Pending", systemImage: "clock").foregroundStyle(.yellow).font(.caption)
        case .failed(let e): Text(e.userMessage).foregroundStyle(.red).font(.caption).lineLimit(1).help(e.userMessage)
        case nil:
            if running { ProgressView().controlSize(.small) }
            else if it.role?.policy.requiresApproval == true { Label("approval", systemImage: "person.badge.clock").font(.caption) }
            else if it.role?.policy.requiresMFA == true { Text("MFA").font(.caption).foregroundStyle(.secondary) }
        }
    }

    private func load() {
        items = model.plan(for: profileId)
        justification = profile?.lastJustification ?? ""
        model.clearProgress(items.map(\.roleKey))
    }

    private func submit() async {
        running = true
        let ticket = needsTicket && !ticketNumber.isEmpty ? TicketInfo(number: ticketNumber, system: ticketSystem) : nil
        await model.runProfile(id: profileId, items: items, justification: justification, ticket: ticket)
        running = false
        finished = true
    }
}
```

- [ ] **Step 5: ManageProfilesView**

```swift
import SwiftUI
import ElevateCore

struct ManageProfilesView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openWindow) private var openWindow
    @State private var names: [UUID: String] = [:]

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Profiles").font(.title3.weight(.semibold))
            List {
                ForEach(model.profiles) { p in
                    HStack(spacing: 8) {
                        Image(systemName: "line.3.horizontal").foregroundStyle(.tertiary)
                        TextField("Name", text: Binding(get: { names[p.id] ?? p.name }, set: { names[p.id] = $0 }), onCommit: { commit(p.id) })
                            .textFieldStyle(.plain)
                        Text(ProfileSummary.caption(entries: p.entries)).font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        Button("Run") { open(.runProfile(p.id)) }.controlSize(.small)
                        Button("Edit") { model.beginEditing(profileId: p.id); dismiss() }.controlSize(.small)
                            .help("Reopens the selection in the panel; use \"Update profile\" when done")
                        Button(role: .destructive) { model.deleteProfile(id: p.id) } label: { Image(systemName: "trash") }
                            .controlSize(.small).accessibilityLabel("Delete \(p.name)")
                    }
                }
                .onMove { from, to in model.moveProfile(fromOffsets: from, toOffset: to) }
            }
            .frame(minHeight: 160)
            if model.profiles.isEmpty {
                Text("No profiles yet. Select roles in the panel and choose \"Save as profile…\".").font(.caption).foregroundStyle(.secondary)
            }
            HStack { Spacer(); Button("Done") { commitAll(); dismiss() }.keyboardShortcut(.defaultAction) }
        }
        .padding(16).frame(width: 520)
    }

    private func commit(_ id: UUID) { if let n = names[id] { model.renameProfile(id: id, name: n) } }
    private func commitAll() { for id in names.keys { commit(id) } }
    private func open(_ route: PanelRoute) { openWindow(value: route); NSApp.activate(ignoringOtherApps: true) }
}
```

- [ ] **Step 6: Build, relaunch, commit** `git commit -m "Add the Profiles row, save/run/manage profile windows and the Update profile flow"`.

---

### Task 5: Final checks and docs

- [ ] `swift test` (expect 126 + 5) and app build; relaunch.
- [ ] `macos/README.md`: a "**Profiles.**" paragraph: select across tabs and accounts, "Save as profile…", chips under the tabs run with a confirmation sheet that skips already-active entries and remembers durations and the reason; "Manage…" to rename, reorder, edit, delete. Commit "Document activation profiles".
