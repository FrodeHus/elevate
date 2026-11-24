# Now Panel (macOS) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the macOS menu bar panel as a sidebar (pinned profiles, accounts and tenants) beside a main view that shows Home, one tenant, the role list or one profile.

**Architecture:** All new behaviour is decided in `AppModel` extensions as plain values (`PanelSelection`, `PanelContent`, `TenantMark`, a `ProfileRunSession` per profile) and covered by `ElevateAppTests`; the SwiftUI views only render those values. The existing row, section, menu and sheet views are reused: the list body moves out of `PanelView` into `RoleListView`, and the run sheet's body moves into a `ProfileRunContent` view shared by the window and the panel.

**Tech Stack:** Swift 6, SwiftUI, Observation, Swift Testing. App tests run through XcodeGen + `xcodebuild` (`swift test` builds only `ElevateCore`).

**Spec:** `docs/superpowers/specs/2026-11-24-elevate-now-panel-design.md`. The Windows port has its own plan, `2026-11-24-now-panel-windows.md`, and ships in the same release.

## Global Constraints

- **Frame:** panel width 516 (sidebar 136 + main 380); panel height at most 720.
- **Copy, verbatim:** `All tenants`, `Profiles`, `Tenants`, `All N`, `Add account…`, `Sign in`, `Approvals`, `Active now`, `Browse all roles`, `Nothing active`, `You hold no elevated access right now.`, `Eligible`, `Filter roles`, `Filter roles in <tenant>`, `N more in other tenants`, `Group membership`, `Group ownership`, `Access packages…`, `Activate N`, tab titles `All`, `Entra`, `Azure`, `Groups`.
- **Nothing activates on a plain click** that asks first today. Option-click keeps its silent behaviour.
- **No new sheets or windows.** Activation, approval, save-profile, access-package, Settings, add-account and setup stay as they are.
- **Colors and materials are the system's.** Do not copy hex values from the mockups.
- **Swift Testing** (`import Testing`, `@Test`, `#expect`) with the helpers in `macos/Tests/ElevateAppTests/Support/TestModel.swift` (`makeModel(state:)`, `cleanup(_:)`, `Sample.*`).
- **Never commit `macos/Elevate.xcodeproj`.**
- **Branch:** work on `now-panel`, cut from `main`. Every commit keeps the app building and all tests green.

Run app tests with (from `macos/`, after `xcodegen generate`):

```bash
xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build CODE_SIGNING_ALLOWED=NO test -only-testing:ElevateAppTests/<SuiteName>
```

If the signing flag differs from what `.github/workflows/macos.yml` passes, use the workflow's.

## Review Focus

1. **The scoped tenant disappears** (removed, account signed out, or account now needs sign-in) while it is the stored selection: the panel shows All tenants, never an empty tenant view. Test in Task 2.
2. **The shown profile is deleted** (from the Profiles window, or a managed profile is withdrawn) while its view is up or its run is drafted: the selection clears and the session is dropped. Test in Task 4.
3. **The panel closes mid-run** (MFA opens the browser): the run finishes, the outcome notification is posted, and the next open lands on that profile's results. Test in Task 4.
4. **A filter typed on a tenant view matches nothing there:** the list says `No matches` and, when other tenants match, offers `N more in other tenants`; with no match anywhere only `No matches`. Test in Task 2.
5. **Many accounts and tenants with long names** (30 tenants, a 60-character tenant name, a 50-character address): the sidebar scrolls under the fixed Profiles section, names truncate with the full text in the tooltip, and the panel stays 516 wide. Manual check in Task 6.

---

### Task 1: The All tab

**Files:**
- Modify: `macos/Sources/ElevateApp/App/PanelTab.swift`
- Modify: `macos/Sources/ElevateApp/App/AppSettings.swift:191`
- Modify: `macos/Sources/ElevateApp/App/AppModel+Panel.swift` (`kinds(for:)`, `navigableRoles`)
- Modify: `macos/Sources/ElevateApp/Views/TenantSection.swift` (`TenantRoles`), `macos/Sources/ElevateApp/Views/RoleRow.swift`
- Test: `macos/Tests/ElevateAppTests/AppModelPanelTests.swift`

**Interfaces:**
- Produces: `PanelTab.all` (first case, title `All`); `AppModel.kinds(for: .all)` returns all three kinds; `AppModel.listRows(for tenantKey: TenantKey) -> [RoleKey]`, the role rows of one tenant in display order for the current tab; `AppModel.groupCaption(_ key: RoleKey) -> String?`.

- [ ] **Step 1: Write the failing tests** in `AppModelPanelTests`, using `modelWithOneOfEachKind()`:

```swift
@Test func allTabShowsEveryKindInOrder() async {
    let model = await modelWithOneOfEachKind()
    model.panelTab = .all
    #expect(model.roles(for: Sample.tenantKey, tab: .all).count == 3)
    #expect(model.listRows(for: Sample.tenantKey) == [Sample.entraKey, Sample.azureKey, Sample.groupKey])
    #expect(model.navigableRoles == [Sample.entraKey, Sample.azureKey, Sample.groupKey])
    cleanup(model)
}

@Test func tabDefaultsToAllWhenNothingIsStoredAndKeepsAStoredTab() {
    let fresh = makeSettings()
    #expect(fresh.panelTab == .all)
    fresh.panelTab = .roles
    #expect(PanelTab(rawValue: "roles") == .roles)   // the stored raw values do not change
}

@Test func groupCaptionNamesMembershipAndOwnership() async {
    let model = await modelWithOneOfEachKind()
    #expect(model.groupCaption(Sample.groupKey) == "Group membership")
    #expect(model.groupCaption(Sample.entraKey) == nil)
    cleanup(model)
}
```

Add an ownership case with a key built from `.group(_, .owner)` expecting `"Group ownership"`.

- [ ] **Step 2: Run and see them fail** (`-only-testing:ElevateAppTests/AppModelPanelTests`). Expected: compile errors for `.all`, `listRows`, `groupCaption`.

- [ ] **Step 3: Implement.** Add `case all` first in `PanelTab` with title `All`; existing raw values stay. `kinds(for: .all)` returns the three kinds. In `AppSettings.init` the fallback becomes `.all`. `listRows(for:)`: on `.all`, Entra role keys, then the flattened Azure tree (same `ScopeTree.flatten` call `navigableRoles` uses), then group keys; on a single tab, what `navigableRoles` computes per tenant today. `navigableRoles` calls `listRows(for:)`.

- [ ] **Step 4: Render it.** In `TenantRoles`, the `.all` tab draws the Entra rows, then the Azure scope tree, then the group rows, each exactly as its own tab draws them. `RoleRow` shows `groupCaption` as its caption only when `model.panelTab == .all`.

- [ ] **Step 5: Run the suite and the full app test target; all pass.**

- [ ] **Step 6: Commit** `macOS: add an All tab that lists every kind of role`.

---

### Task 2: Panel selection

**Files:**
- Create: `macos/Sources/ElevateApp/App/PanelSelection.swift`
- Create: `macos/Sources/ElevateApp/App/AppModel+Selection.swift`
- Modify: `macos/Sources/ElevateApp/App/AppSettings.swift` (new key), `macos/Sources/ElevateApp/App/AppModel.swift` (two stored properties), `macos/Sources/ElevateApp/App/AppModel+Panel.swift` (`visibleTenants`, `visibleIdentities`)
- Test: `macos/Tests/ElevateAppTests/AppModelSelectionStateTests.swift`

**Interfaces:**
- Consumes: Task 1's `listRows(for:)`.
- Produces:

```swift
enum PanelSelection: Hashable { case allTenants, tenant(TenantKey), profile(UUID) }
enum PanelContent: Hashable { case home, roleList, tenant(TenantKey), profile(UUID) }

// AppSettings
static let panelTenantKey = "panelTenant"
var panelTenant: TenantKey?            // JSON-encoded in UserDefaults; nil = all tenants

// AppModel (stored)
var selectedProfile: UUID?             // never persisted
var browsing = false                   // never persisted

// AppModel+Selection
var scopeTenant: TenantKey? { get set }        // settings.panelTenant, nil unless that tenant is tracked and its account does not need sign-in
var panelSelection: PanelSelection { get }
func select(_ selection: PanelSelection)
var panelContent: PanelContent { get }
var otherTenantMatchCount: Int { get }
func searchAllTenants()                        // scopeTenant = nil, query kept
func handleEscape()
```

Rules the tests pin:
- `panelSelection` is `.profile` when `selectedProfile` names an existing profile, else `.tenant` when `scopeTenant` is non-nil, else `.allTenants`.
- `select(.allTenants)` and `select(.tenant)` clear `selectedProfile` and `highlightedRole`, and leave `searchQuery`, `selectMode` and `selection` alone. `select(.profile)` leaves `scopeTenant` alone.
- `panelContent`: `.profile(id)` for a profile selection; `.tenant(key)` for a tenant selection; otherwise `.roleList` when `browsing || isFiltering || selectMode || homeIsEmpty`, else `.home`. Until Task 3 defines `homeIsEmpty`, declare it here returning `false`.
- With `scopeTenant` set, `visibleTenants(for:)` returns only that tenant and `visibleIdentities` only its account.
- `otherTenantMatchCount` is 0 unless `scopeTenant != nil && isFiltering`; then it is the number of roles in every other tracked tenant that match the query and the current tab's kinds.
- `handleEscape()`: a non-empty query is cleared and nothing else changes; otherwise a profile selection returns to the tenant or all-tenants selection underneath it; otherwise `browsing = false`, `selectMode = false` and `scopeTenant = nil`.

- [ ] **Step 1: Write the failing tests** in a new `@MainActor struct AppModelSelectionStateTests`. Build a model with two identities (`id-1`, `id-2`), one tenant each (`Sample.tenantKey`, `TenantKey(identityId: "id-2", tenantId: "tenant-2")`), one Entra role in each (`"Global Reader"`, `"Billing Admin"`). Tests, one behaviour each:

```swift
@Test func defaultsToAllTenantsAndHome()                      // panelSelection == .allTenants, panelContent == .home
@Test func selectingATenantPersistsAndScopesTheList()         // settings.panelTenant == key; visibleTenants(for: "id-2") == []; panelContent == .tenant(key)
@Test func aRemovedTenantFallsBackToAllTenants()              // set scopeTenant, remove the tenant from state: scopeTenant == nil, panelSelection == .allTenants
@Test func anAccountNeedingSignInFallsBackToAllTenants()      // signInNeeded.insert("id-1"): scopeTenant == nil
@Test func typingBrowsingOrSelectModeShowTheRoleList()        // each of searchQuery = "g", browsing = true, selectMode = true gives .roleList
@Test func selectingATenantKeepsQueryAndSelection()           // selectMode on, selection = [Sample.entraKey], searchQuery "glob": unchanged after select(.tenant)
@Test func otherTenantMatchesAreCountedOnlyWhenScopedAndFiltering() // scope tenant-1, query "billing": roles(for: tenantKey, tab: .all) == [] and otherTenantMatchCount == 1; query "zzz": 0; scope nil: 0
@Test func searchAllTenantsKeepsTheQuery()                    // after searchAllTenants(): scopeTenant == nil, searchQuery == "billing"
@Test func escapeClearsTheFilterThenReturnsHome()             // query set + tenant scope: first call clears query only; second gives .allTenants and .home
@Test func escapeOnAProfileReturnsToTheTenantUnderneath()     // scope tenant, selectedProfile = saved profile id: handleEscape() gives .tenant(key)
@Test func aMissingProfileIsNotASelection()                   // selectedProfile = UUID(): panelSelection == .allTenants
```

- [ ] **Step 2: Run and see them fail** (`-only-testing:ElevateAppTests/AppModelSelectionStateTests`).

- [ ] **Step 3: Implement** the types and members above. `panelTenant` is encoded like `seenApprovalIds` (JSON `Data` under its key). The search field's `.onExitCommand` in `PanelView` calls `model.handleEscape()`.

- [ ] **Step 4: Run the suite and the full target; all pass.**

- [ ] **Step 5: Commit** `macOS: panel selection state for all tenants, one tenant and a profile`.

---

### Task 3: Home and sidebar data

**Files:**
- Create: `macos/Sources/ElevateApp/App/AppModel+Home.swift`
- Modify: `macos/Sources/ElevateApp/App/AppModel+Panel.swift:141-152` (`activeAssignmentsOrdered`, `activeRowsOrdered`), `macos/Sources/ElevateApp/App/AppModel+Approvals.swift`, `macos/Sources/ElevateApp/Views/AssignmentControls.swift:57`
- Test: `macos/Tests/ElevateAppTests/AppModelHomeTests.swift`; update `AppModelPanelTests.activeAssignmentsOrderedShowsOnlyTheCurrentTabsKinds`

**Interfaces:**
- Produces:

```swift
func activeAssignments(in tenant: TenantKey?) -> [ActiveAssignment]   // every kind, ActiveSummary order; nil = all tenants
func activeRows(in tenant: TenantKey?) -> [ActiveAssignment]          // plus the just-deactivated rows, as activeRowsOrdered does
func approvals(in tenant: TenantKey?) -> [ApprovalRequest]            // approvalsOrdered, filtered by request.tenantKey
func canExtendNow(_ key: RoleKey) -> Bool                             // the exact condition AssignmentControls uses to show Extend
var heroAssignment: ActiveAssignment? { get }
var homeIsEmpty: Bool { get }                                         // replaces Task 2's stub

enum TenantMark: Equatable { case active(Int), error, none }
func sidebarMark(for key: TenantKey) -> TenantMark
var totalActiveCount: Int { get }
```

Rules:
- `activeAssignmentsOrdered` and `activeRowsOrdered` become the `nil` cases and no longer depend on `panelTab`.
- `heroAssignment` is the first element of `activeAssignments(in: nil)` when `canExtendNow` is true for it, else nil.
- `homeIsEmpty` is true when `approvals(in: nil)` and `activeRows(in: nil)` are both empty.
- `sidebarMark`: `.active(n)` when the tenant has `n > 0` entries in `active` (any status, any kind); else `.error` when `tenantErrors[key] != nil`; else `.none`.
- `totalActiveCount == activeAssignments(in: nil).count`.

- [ ] **Step 1: Write the failing tests** in `@MainActor struct AppModelHomeTests`:

```swift
@Test func activeNowListsEveryKindWhateverTheTab()            // one of each kind active, panelTab = .azure: activeAssignmentsOrdered.count == 3
@Test func activeAssignmentsCanBeNarrowedToOneTenant()        // two tenants, one assignment each: activeAssignments(in: key).map(\.roleKey) == [that tenant's key]
@Test func approvalsCanBeNarrowedToOneTenant()
@Test func heroIsTheSoonestExpiryInsideTheExtendWindow()      // ends in 10 min: heroAssignment?.roleKey == key
@Test func noHeroOutsideTheExtendWindow()                     // ends in 60 min: nil
@Test func noHeroWhenTheSoonestRowIsPending()                 // soonest is .pendingApproval: nil
@Test func homeIsEmptyWithNothingActiveAndNoApprovals()       // true; and panelContent == .roleList
@Test func sidebarMarkPrefersActiveCountOverError()           // 2 active + tenantErrors set: .active(2); only error: .error; neither: .none
```

For the hero tests build the role with a policy that `ExtendWindow.canExtend` accepts; read `ExtendWindowTests` in `ElevateCoreTests` for a policy that does. Update the existing panel test to expect all three keys on every tab and rename it `activeAssignmentsOrderedShowsEveryKind`.

- [ ] **Step 2: Run and see them fail.**

- [ ] **Step 3: Implement.** Move the Extend condition from `AssignmentControls.swift:57` into `canExtendNow` and call it from the view.

- [ ] **Step 4: Run the full target; all pass.**

- [ ] **Step 5: Commit** `macOS: home, hero and sidebar data in the app model`.

---

### Task 4: Profile run session

**Files:**
- Create: `macos/Sources/ElevateApp/App/ProfileRunSession.swift`
- Create: `macos/Sources/ElevateApp/App/AppModel+ProfileRun.swift`
- Modify: `macos/Sources/ElevateApp/App/AppModel.swift` (stored properties), `macos/Sources/ElevateApp/App/AppModel+Activation.swift:215` (`notifyOutcome` loses `private`), `macos/Sources/ElevateApp/App/AppModel+Profiles.swift` (`deleteProfile`), `macos/Sources/ElevateApp/App/ElevateApp.swift:20` (close hook)
- Test: `macos/Tests/ElevateAppTests/AppModelProfileRunTests.swift`

**Interfaces:**
- Consumes: Task 2's `selectedProfile`, `select(_:)`.
- Produces:

```swift
@MainActor @Observable final class ProfileRunSession {
    let profileId: UUID
    var items: [ProfilePlanItem]
    var excluded: Set<RoleKey> = []
    var justification: String
    var ticketNumber = "", ticketSystem = ""
    var scheduleStart = false
    var startAt: Date
    var running = false, finished = false, resultsSeen = false
    var rowResults: [RoleKey: ActivationOutcome.Result] = [:]
    var toActivate: [ProfilePlanItem] { get }
    var needsTicket: Bool { get }
    var justificationRequired: Bool { get }
    var canSubmit: Bool { get }
}

// AppModel (stored)
var runSessions: [UUID: ProfileRunSession] = [:]
var panelIsOpen = false

// AppModel+ProfileRun
func runSession(for id: UUID) -> ProfileRunSession     // existing one, else a fresh plan
func resetRunSession(_ id: UUID)                       // what RunProfileView.load() does today
func submitRun(_ id: UUID) async                       // what RunProfileView.submit() does today
func showProfile(_ id: UUID)                           // select(.profile(id)); a finished session is reset first
func markRunSeen(_ id: UUID)
func panelClosed()
// panelOpened() already exists; it gains the reopen rule below
```

The computed properties and the bodies of `resetRunSession` and `submitRun` are the code now in `RunProfileView.swift` (`toActivate`, `needsTicket`, `justificationRequired`, `canSubmit`, `load()`, `submit()`), moved, with view state replaced by session fields.

Rules:
- Leaving a profile (`selectedProfile` changes away from `id`): its session is removed unless `running`.
- `submitRun` posts `notifyOutcome(title: profile.name, outcomes:, attempted:)` when it ends and `panelIsOpen == false`. With the panel open it posts nothing.
- `panelOpened()` sets `panelIsOpen = true`, `browsing = false`, then: if `selectedProfile`'s session exists and is finished with `resultsSeen`, remove it and clear `selectedProfile`; else if `selectedProfile`'s session exists, keep it; else select the first session that is `running`, or `finished && !resultsSeen`, if there is one; else clear `selectedProfile`.
- `deleteProfile(id:)` and a managed profile disappearing remove that profile's session and clear `selectedProfile` when it names it.

- [ ] **Step 1: Write the failing tests** in `@MainActor struct AppModelProfileRunTests`. Save a profile over `Sample.entraKey` with `model.saveProfile(name: "Ops", keys: [Sample.entraKey])`. Read `AppModelProfileTests` for how existing tests stub an activation that succeeds.

```swift
@Test func aSessionPlansTheProfileAndPrefillsTheReason()      // items.count == 1; justification == profile.lastJustification ?? ""
@Test func uncheckingAnEntryRemovesItFromToActivate()
@Test func leavingAnUnstartedDraftDropsIt()                   // showProfile(id); select(.allTenants): runSessions[id] == nil
@Test func aDraftSurvivesThePanelClosing()                    // showProfile(id); edit justification; panelClosed(); panelOpened(): selection == .profile(id), justification kept
@Test func aRunningSessionSurvivesLeavingAndReopens()         // running = true; select(.allTenants); panelClosed(); panelOpened(): selection == .profile(id)
@Test func unseenResultsReopenOnTheProfile()                  // finished = true, resultsSeen = false, other selection: panelOpened() selects the profile
@Test func seenResultsDoNotReopen()                           // finished, markRunSeen(id), panelClosed(), panelOpened(): .allTenants and runSessions[id] == nil
@Test func aRunThatEndsWithThePanelClosedNotifies()           // panelClosed(); await submitRun(id): the recording notifier got one notification titled "Ops"
@Test func aRunThatEndsWithThePanelOpenDoesNotNotify()
@Test func deletingTheShownProfileClearsSelectionAndSession() // Review Focus 2
@Test func showingAFinishedProfileAgainStartsAFreshPlan()     // finished = true; showProfile(id): finished == false, excluded.isEmpty
```

If `TestModel.swift` has no notifier that records, add a `RecordingNotifier: ExpiryNotifying` there and a `notifier:` parameter on `makeModel`.

- [ ] **Step 2: Run and see them fail.**

- [ ] **Step 3: Implement.** `ElevateApp.swift` calls `model.panelClosed()` from the panel's `.onDisappear`, beside the existing `.onAppear { model.panelOpened() }`.

- [ ] **Step 4: Run the full target; all pass.**

- [ ] **Step 5: Commit** `macOS: keep a profile run in the app model so it survives the panel closing`.

---

### Task 5: Shared run-profile content

**Files:**
- Create: `macos/Sources/ElevateApp/Views/ProfileRunContent.swift`
- Modify: `macos/Sources/ElevateApp/Views/RunProfileView.swift`

**Interfaces:**
- Consumes: Task 4's `ProfileRunSession`, `runSession(for:)`, `submitRun(_:)`, `resetRunSession(_:)`.
- Produces: `struct ProfileRunContent: View { let profileId: UUID; let compact: Bool; var onDone: (() -> Void)? }`. `compact: false` is the 560-wide window layout of today; `compact: true` fits the 380-wide main view (durations and status under the role name instead of in fixed-width columns). `onDone` non-nil draws today's Cancel and Done buttons; nil draws only `Activate N`.

- [ ] **Step 1: Move the body.** `ProfileRunContent` renders rows, Start at, Reason, ticket fields, the caption and the buttons from the session, with no `@State` of its own except focus. `RunProfileView` becomes the window wrapper: `ProfileRunContent(profileId:, compact: false, onDone: { dismiss() })` with the navigation title, the `.frame(width: 560)` and the `runRequests` re-plan hook, which now calls `model.resetRunSession(profileId)` when the session is not running.

- [ ] **Step 2: Build the app and run the full test target; all pass.**

- [ ] **Step 3: Check the window by hand.** Launch the app (see `macos/README.md`), right-click a profile chip, Run…: the window looks and behaves as before, including a second Run… on the same profile re-planning it.

- [ ] **Step 4: Commit** `macOS: split the run sheet into a content view the panel can host`.

---

### Task 6: Sidebar and panel frame

**Files:**
- Create: `macos/Sources/ElevateApp/Views/PanelSidebar.swift`
- Modify: `macos/Sources/ElevateApp/Views/PanelView.swift` (`PanelMetrics`, body), `macos/Sources/ElevateApp/Views/ProfilesRow.swift` (delete `ProfilesRow` and `ProfileChip`; keep `AllProfilesPopover`, `ProfileMenuItems`, `ProfileActions` and the delete-confirmation modifiers), `macos/Sources/ElevateApp/Views/IdentitySection.swift` (account menu items become reusable)

**Interfaces:**
- Consumes: `pinnedProfiles`, `profiles`, `panelSelection`, `select(_:)`, `showProfile(_:)`, `sidebarMark(for:)`, `totalActiveCount`, `signInNeeded`, `collapsedIdentities`, `toggleIdentity(_:)`, `quickRun(profileId:)`.
- Produces: `PanelMetrics.width = 516`, `PanelMetrics.sidebarWidth = 136`, `PanelMetrics.mainWidth = 380`, `PanelMetrics.maxHeight = 720`; `struct PanelSidebar: View`; `struct IdentityMenuItems: View { let identity: Identity }` extracted from `IdentityHeader`'s menu so the sidebar and the list header show the same items; `ProfileActions.run(_:model:openWindow:silentlyIfPossible:inPanel:)` where `inPanel: true` calls `model.showProfile(id)` instead of opening the window, including as the fallback of a silent run.

Layout, top to bottom (spec 3.2):
- **Profiles**, fixed: heading `Profiles` with the `All N` button (opens `AllProfilesPopover`); one button per `pinnedProfiles` entry (star icon for a one-entry profile, bolt otherwise), `.help` with name, `ProfileSummary.caption` and the shortcut when it is the hot-key profile; click `ProfileActions.run(..., silentlyIfPossible: NSEvent.modifierFlags.contains(.option), inPanel: true)`; `.contextMenu { ProfileMenuItems }`. Hidden when `profiles.isEmpty`; only `All N` when nothing is pinned. A pinned profile shown in the main view is highlighted; an unpinned one highlights `All N`.
- **Tenants**, in a `ScrollView`: `All tenants` with `totalActiveCount`; then per identity a label (address, tap toggles collapse) with a `Menu { IdentityMenuItems }`, or a `Sign in` button when `signInNeeded` contains it (its tenant rows dimmed and disabled); tenant rows with name and `sidebarMark` (green count or red `exclamationmark.triangle`), `.help` with the pills' text and the error.
- `Add account…`, fixed at the bottom, opens `PanelRoute.addAccount`.
- With no accounts or when not configured, the sidebar shows only `Add account…`.
- Every row is a `Button` with an accessibility label that includes its state ("Contoso, 2 active").

- [ ] **Step 1: Build `PanelSidebar`** as above.
- [ ] **Step 2: Reframe `PanelView`:** an `HStack(spacing: 0)` of `PanelSidebar().frame(width: PanelMetrics.sidebarWidth)`, a `Divider`, and the existing column at `PanelMetrics.mainWidth`. Remove `ProfilesRow()` and the footer's `Add account…`. The list's height cap becomes `PanelMetrics.maxHeight` minus the measured header, filter, banner and footer heights, so the whole panel never exceeds 720. All `ProfileActions.run` call sites reached from the panel (`AllProfilesPopover`, `ProfileMenuItems`) pass `inPanel: true`; `ManageProfilesView` and the hot-key path pass `false`.
- [ ] **Step 3: Build, run the full test target; all pass.**
- [ ] **Step 4: Check by hand** with sample data: selecting All tenants and a tenant changes the list's scope; Option-click on a profile runs silently; right-click shows the menu; a signed-out account shows `Sign in`. Then Review Focus 5: add tenants until the sidebar scrolls (the Add tenant dialog accepts any tenant id) and confirm the Profiles section and `Add account…` stay put, long names truncate, and the panel is 516 wide.
- [ ] **Step 5: Commit** `macOS: sidebar with pinned profiles, accounts and tenants`.

---

### Task 7: Role list and tenant view

**Files:**
- Create: `macos/Sources/ElevateApp/Views/RoleListView.swift`, `macos/Sources/ElevateApp/Views/PanelHeader.swift`
- Modify: `macos/Sources/ElevateApp/Views/PanelView.swift`, `macos/Sources/ElevateApp/Views/ActiveSection.swift`, `macos/Sources/ElevateApp/Views/ApprovalsSection.swift`

**Interfaces:**
- Consumes: `panelContent`, `scopeTenant`, `otherTenantMatchCount`, `searchAllTenants()`, `activeRows(in:)`, `approvals(in:)`, `listRows(for:)`.
- Produces: `struct RoleListView: View { let tenant: TenantKey? }`; `struct PanelHeader: View` (title per spec 3.1, select toggle, refresh); `ActiveSection(tenant: TenantKey? = nil, showsHero: Bool = false)`; `ApprovalsSection(tenant: TenantKey? = nil)`.

- [ ] **Step 1: Move the list.** `RoleListView(tenant: nil)` is the `ScrollViewReader`/`LazyVStack` now in `PanelView` minus `ApprovalsSection` and `ActiveSection`, under the segmented tab control (moved from the top of `PanelView`). `RoleListView(tenant: key)` shows, in one scroll view: `ApprovalsSection(tenant: key)`, `ActiveSection(tenant: key)`, the tab control, a section header `Eligible · N` and `TenantRoles(tenant:)` with no account or tenant header; when `otherTenantMatchCount > 0` a last row `N more in other tenants` calling `searchAllTenants()`; `No matches` when the tenant has no rows for the query.
- [ ] **Step 2: Header.** `PanelHeader` shows `Elevate`, the branding caption and the `offline` pill for `.home` and `.roleList`; for `.tenant` the tenant name, `home` tag, the account address, an `Access packages…` button when the tenant offers them (the condition `TenantHeader` uses for its box icon) and a `Menu { TenantMenuItems }`. The filter placeholder is `Filter roles in <tenant>` on a tenant view.
- [ ] **Step 3: Switch on `model.panelContent`** in `PanelView`: `.roleList` → `RoleListView(tenant: nil)`, `.tenant(k)` → `RoleListView(tenant: k)`; `.home` and `.profile` keep showing `RoleListView(tenant: nil)` until Tasks 8 and 9. Setup, startup-error and no-accounts views are checked first, as today. The select-mode footer stays below either list.
- [ ] **Step 4: Build, run the full test target; all pass.**
- [ ] **Step 5: Check by hand:** each tab on both scopes; Azure scope tree and select mode on a tenant view; a filter on a tenant view with matches elsewhere shows the row and it works (Review Focus 4); Escape clears, then goes home.
- [ ] **Step 6: Commit** `macOS: role list view with a one-tenant scope`.

---

### Task 8: Home and the empty state

**Files:**
- Create: `macos/Sources/ElevateApp/Views/HomeView.swift`
- Modify: `macos/Sources/ElevateApp/Views/ActiveSection.swift` (hero card), `macos/Sources/ElevateApp/Views/PanelView.swift`, `macos/Sources/ElevateApp/Views/RoleListView.swift`

**Interfaces:**
- Consumes: `heroAssignment`, `homeIsEmpty`, `approvals(in:)`, `activeRows(in:)`, `browsing`.
- Produces: `struct HomeView: View`; `struct ActiveHeroCard: View { let assignment: ActiveAssignment }`.

- [ ] **Step 1: `HomeView`:** `ApprovalsSection()`, `ActiveSection(showsHero: true)`, then a `Browse all roles` button with the caption `N in M tenants` (eligible roles and tracked tenants) that sets `model.browsing = true`.
- [ ] **Step 2: Hero card.** With `showsHero`, the row for `model.heroAssignment` is drawn as `ActiveHeroCard`: name, tenant and account, the time left in a larger font, an orange outline, `Extend` as `.borderedProminent` and `Deactivate` beside it. Both buttons do what `AssignmentControls` does for that row (reuse its actions, including Option-click on Extend).
- [ ] **Step 3: Empty state.** When `homeIsEmpty` and the selection is all tenants with no filter, `RoleListView(tenant: nil)` puts above the tab control a card with `Nothing active` and `You hold no elevated access right now.`, and, when `model.profiles.isEmpty`, a last row offering `Select roles` (sets `selectMode = true`).
- [ ] **Step 4: `PanelView` shows `HomeView` for `.home`.**
- [ ] **Step 5: Build, run the full test target; all pass.**
- [ ] **Step 6: Check by hand:** nothing active (card plus list); one role expiring in under 15 minutes (hero); the same role with over 15 minutes (plain row); an approval waiting.
- [ ] **Step 7: Commit** `macOS: home view with approvals, active roles and the expiring-soon card`.

---

### Task 9: Profile view

**Files:**
- Create: `macos/Sources/ElevateApp/Views/ProfilePanelView.swift`
- Modify: `macos/Sources/ElevateApp/Views/PanelView.swift`, `macos/Sources/ElevateApp/Views/PanelHeader.swift`

**Interfaces:**
- Consumes: `ProfileRunContent`, `runSession(for:)`, `markRunSeen(_:)`, `setPinned(id:_:)`, `ProfileMenuItems`, `handleEscape()`.
- Produces: `struct ProfilePanelView: View { let profileId: UUID }`.

- [ ] **Step 1: Build it.** Header (in `PanelHeader` for `.profile`): bolt or star, profile name, `N roles · M tenants` (`ProfileSummary.caption` for the first part), the shortcut when bound, a pin star toggling `setPinned`, a `Menu { ProfileMenuItems }`, and refresh. No filter field and no select toggle on this view. Body: `ProfileRunContent(profileId:, compact: true, onDone: nil)`. `.onExitCommand { model.handleEscape() }`. When the session is `finished`, call `model.markRunSeen(profileId)` on appear and when `finished` turns true while visible.
- [ ] **Step 2: `PanelView` shows it for `.profile(id)`** and hides the filter field there.
- [ ] **Step 3: Build, run the full test target; all pass.**
- [ ] **Step 4: Check by hand:** click a sidebar profile: view shows, Reason focused, Return submits when valid; run a profile whose role needs MFA: the panel closes, the notification arrives, reopening shows the results (Review Focus 3); reopen again: Home; Run from the All list on an unpinned profile highlights `All N`; the global shortcut still opens the window when input is needed.
- [ ] **Step 5: Commit** `macOS: show and run a profile in the panel's main view`.

---

### Task 10: Guides and design canvas

**Files:**
- Modify: `docs/getting-started.md` (section 5), `docs/activating-roles.md`, `docs/profiles-and-shortcuts.md`, `docs/approvals.md`, `docs/access-packages.md`, `macos/README.md` (panel section), `docs/design/README.md`
- Replace: the panel screenshots those guides reference under `docs/images/`

- [ ] **Step 1: Rewrite the panel descriptions** to the new layout: the sidebar, All tenants and tenant views, the All tab, where profiles are and that a click shows the profile in the panel, Escape, `N more in other tenants`. Keep every statement about sheets, notifications and the menu bar icon.
- [ ] **Step 2: Retake each referenced panel screenshot** from the running app with the same fictional sample data the current pictures use, at the same scale; keep file names so links hold.
- [ ] **Step 3: Index the canvas.** Its artboard sources and `canvas.json` are already in `docs/design/now-panel/`. Add a row to `docs/design/README.md` naming that folder and the live link https://claude.ai/artifact/QLsnkjNRKK6j4bQSXukrbQ.
- [ ] **Step 4: Run** `python3 scripts/check-site.py` if any changed file is referenced by `site/`; expected: no broken references.
- [ ] **Step 5: Commit** `Docs: describe the Now panel`.
