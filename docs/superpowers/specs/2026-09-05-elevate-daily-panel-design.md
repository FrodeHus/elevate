# Elevate — daily-use panel

Date: 2026-09-05. Extends `2026-09-04-pimtray-design.md` (panel) and
`2026-09-05-elevate-phase3-groups-design.md` (tabs). Approved in chat: a pinned
"Active now" section (collapsible), monochrome menu bar badges, inline Extend
within 15 minutes of expiry, no Renew.

## 1. Goal

Make the panel serve the thing it is opened for most: seeing what is active,
when it expires, and acting on it fast.

Success criteria:

- A pinned **Active now** section at the top of the list shows every active,
  pending-approval and pending-provisioning assignment across all accounts,
  tenants and both tabs, sorted by soonest expiry, each with its countdown
  and Deactivate / Cancel control. It collapses like accounts and tenants and
  the collapsed state survives relaunch. It is absent when nothing is active.
- The **menu bar label** shows: outline shield when nothing is active; filled
  shield with the count when something is; a warning shield when any
  assignment expires within 5 minutes; a clock glyph beside the count when any
  request awaits approval. All monochrome template rendering.
- **Extend** appears inline, before Deactivate, on an active row (list and
  summary) once it is within 15 minutes of expiry, when the policy's maximum
  duration allows. It opens the existing activation dialog with the remembered
  reason and duration.
- When an assignment **expires**, a notification "X expired" offers
  **Activate again**, which opens the activation dialog for that role.
- A **search field** in the panel header filters rows by role or group name,
  caption, tenant name and account address; tenants and accounts with no
  matching rows are hidden while filtering; the summary is filtered too.
- Azure role rows keep their short caption and show the **full scope path**
  as a tooltip.

Out of scope: Renew, approvals for others, profiles, scheduled activation.

## 2. Core additions (`ElevateCore`)

Pure functions, unit-tested:

- `PanelStatus` (`Support/PanelStatus.swift`):
  `public struct PanelStatus: Equatable, Sendable { activeCount: Int; expiringSoon: Bool; pendingApproval: Bool }` and
  `public static func compute(_ assignments: [ActiveAssignment], now: Date, soonWithin: TimeInterval = 300) -> PanelStatus`.
  `activeCount` counts `.active`; `expiringSoon` is true when any `.active`
  assignment has `endDateTime` within `soonWithin` of `now` (and after it);
  `pendingApproval` when any `.pendingApproval`.
- `ActiveSummary` (`Support/ActiveSummary.swift`):
  `public static func order(_ assignments: [ActiveAssignment]) -> [ActiveAssignment]`:
  active ones by `endDateTime` ascending (nil last), then pending-approval by
  `startDateTime`, then pending-provisioning.
- `PanelFilter` (`Support/PanelFilter.swift`):
  `public static func matches(query: String, role: EligibleRole, tenantName: String, upn: String) -> Bool`:
  case- and diacritic-insensitive `localizedStandardContains` over
  `displayName`, `detail`, `viaGroup`, `tenantName`, `upn`; an empty or
  whitespace query matches everything.
- `ExtendWindow` (`Support/ExtendWindow.swift`):
  `public static func canExtend(_ assignment: ActiveAssignment, policy: RolePolicy, now: Date, within: TimeInterval = 900) -> Bool`:
  status `.active`, `endDateTime` present, `endDateTime - now <= within`,
  and `endDateTime > now`. (The maximum-duration check is implicit: the
  activation dialog caps the duration by policy.)
- `Countdown.label` unchanged.

## 3. App changes

### 3.1 Active now section

- `AppModel.activeAssignmentsOrdered: [ActiveAssignment]` =
  `ActiveSummary.order(Array(active.values))`.
- `AppModel.collapsedActive: Bool` persisted in `AppSettings`
  (`UserDefaults` key `collapsedActive`), toggled by `toggleActive()`.
- `Views/ActiveSection.swift`: a `Section` pinned like the others whose
  header is an accent-free row "Active now" with the count, a chevron button
  (same style as tenant headers) and nothing else; body is one `ActiveRow`
  per assignment: role name, caption "<tenant> · <upn>" (secondary), then the
  same trailing control as `RoleRow` for that status (countdown + Extend +
  Deactivate, "awaiting approval" + Cancel, provisioning spinner). Rows use
  `model.role(for:)` for the name and fall back to the scope's id when the
  eligible list has not loaded yet.
- Rendered inside the existing `LazyVStack(pinnedViews:)` before the first
  account, in both tabs, only when `activeAssignmentsOrdered` is non-empty
  (after filtering, §3.5).
- The trailing controls are extracted from `RoleRow` into
  `Views/AssignmentControls.swift` (`AssignmentControls(role:assignment:)`)
  so `RoleRow` and `ActiveRow` share one implementation, including the
  5-minute Deactivate lock and the new Extend button.

### 3.2 Menu bar label

- `MenuBarLabel` recomputes `PanelStatus.compute(Array(model.active.values), now:)`
  inside a `TimelineView(.periodic(from: .now, by: 30))` so the expiring
  state flips without a refresh.
- Symbols: none active → `shield`; active → `checkmark.shield.fill`;
  expiring soon → `exclamationmark.shield.fill`; the count text follows when
  `activeCount > 0`; a trailing `clock` image when `pendingApproval`.
  Accessibility label describes the state ("3 active, one expiring soon,
  one awaiting approval").

### 3.3 Extend

- `AssignmentControls` shows an "Extend" button (controlSize small, before
  Deactivate) when `ExtendWindow.canExtend(assignment, policy: role.policy, now:)`.
  Action: `openWindow(value: PanelRoute.activate([key]))`, the path the
  notification already uses; `AppModel.activate` deactivates then
  re-activates ("Extend works" comment already there).
- Help text: "Extend by activating again; the current activation is replaced".

### 3.4 Expired notification with Activate again

- `ExpiryNotifier` gains a second category `PIMTRAY_EXPIRED` with action
  `PIMTRAY_ACTIVATE_AGAIN` titled "Activate again" (foreground). In
  `reschedule`, for each active assignment with an end time in the future,
  schedule a second request `expired-<assignmentId>` at `endDateTime + 5 s`
  with title "<name> expired" and body "Tenant <tenant>". Both categories'
  actions and the default tap route through `onExtend` (the handler opens the
  activation dialog either way; naming stays `onExtend` for minimal churn).
- `AppModel.rescheduleNotifications` is unchanged; the notifier decides.
- After the expiry time passes, the row is still shown as active until the
  next refresh; the refresh is already triggered by the 60 s timer. No change.

### 3.5 Search

- `AppModel.searchQuery: String` (not persisted); `AppModel.isFiltering`
  when the trimmed query is non-empty.
- `roles(for:tab:)` applies `PanelFilter.matches` with the tenant display
  name and identity UPN when filtering; a new
  `visibleTenants(for identityId:)` returns tenants that have at least one
  matching row for the current tab; `visibleIdentities` returns identities
  with at least one visible tenant. `PanelView` iterates the visible sets
  while filtering and the full sets otherwise (so collapsed/empty tenants
  still show when not filtering).
- The summary filters assignments whose role matches (unknown role names
  match on the scope id string).
- Header: a magnifying-glass toggle button next to the select toggle; when on,
  a `TextField("Filter roles", text:)` row appears under the segmented control
  with an "xmark.circle.fill" clear button; Escape clears and closes it.
  Closing the field clears the query.

### 3.6 Azure scope tooltip

- `RoleRow` adds `.help(scope)` on the caption text for `.azureResource`
  keys, where `scope` is the raw ARM scope path from the key. No change to the
  caption itself.

## 4. Error handling

No new network paths. Extend and Activate again reuse the activation dialog
and its error display. Notification scheduling failures stay silent as today.

## 5. Testing

`ElevateCoreTests`: `PanelStatusTests` (counts, expiring boundary at exactly
300 s, pending flag), `ActiveSummaryTests` (ordering incl. nil end dates and
pending groups), `PanelFilterTests` (empty query, case and diacritics, each
field, no match), `ExtendWindowTests` (inside/outside window, past end,
non-active status).

App layer has no test target; verify by build, relaunch and manual check:
summary appears with an active role, collapses and remembers, menu bar shows
the warning shield within 5 minutes of expiry and the clock while a request is
pending, Extend appears within 15 minutes, the expired notification fires with
Activate again, the search field filters and hides empty accounts, the Azure
caption tooltip shows the full scope.

## 6. User action

None.
