# Elevate — activation shortcuts

Date: 2026-09-05. Extends the panel, profiles and notification specs. Approved
in chat: Option-click quick activate, optional "Start at" scheduling, one
global hot key that runs a profile (not "open the panel").

## 1. Goal

Cut the clicks for routine activations without losing the safety of the
dialog where input is genuinely needed.

Success criteria:

- **Quick activate.** Option-click on Activate (role rows and Extend) activates
  immediately with the remembered reason and last duration. Option-click on a
  profile chip runs it with remembered reason and durations, skipping the
  sheet. The dialog or sheet opens instead when: no remembered reason and the
  policy requires a justification; the policy requires a ticket; the policy
  requires approval (single role only; a profile run treats approval entries
  as normal); or a role in a profile is not loaded. MFA and authentication
  context do not block quick activate (the browser prompt explains itself).
  A notification reports the outcome ("Global Reader active for 2 h" /
  "2 activated, 1 failed: …"). Tooltips mention Option-click.
- **Scheduled activation.** The activation dialog and the run sheet get a
  "Start at" toggle with a date-time picker, off by default, limited to the
  future. Requests carry the start time; providers send it as
  `scheduleInfo.startDateTime`. The result is a **scheduled** assignment shown
  in Active now with "starts in 2 h 15 m" and a Cancel button; the menu bar
  shows the clock badge. Scheduled assignments are read back on refresh from
  the schedule endpoints, so they survive relaunch.
- **Global shortcut.** Settings gets a "Global shortcut" section: a key
  recorder, a profile picker and "Off". The hot key runs the chosen profile
  exactly like Option-clicking its chip. Registered with Carbon
  `RegisterEventHotKey`; no Accessibility permission needed.

Out of scope: a shortcut that opens the panel; repeating schedules.

## 2. Core

- `ActivationRequest.startDateTime: Date?` (nil = now). Entra, Azure and Group
  providers put `GraphJSON.encoderDateString(startDateTime ?? .now)` in
  `scheduleInfo.startDateTime`.
- `ActiveAssignment.Status.scheduled` — a future activation; `startDateTime`
  is the start, `endDateTime` the planned end.
  - Provider `activate`: when the response's `scheduleInfo.startDateTime` (or
    the request's start) is more than 60 s in the future, return `.scheduled`
    regardless of the raw status string (Graph answers `Provisioned` or
    `ScheduleCreated` for future starts).
  - Provider `activeAssignments`: also read the schedules —
    Entra `roleManagement/directory/roleAssignmentSchedules/filterByCurrentUser(on='principal')`,
    Group `…/group/assignmentSchedules/filterByCurrentUser(on='principal')`,
    ARM `providers/Microsoft.Authorization/roleAssignmentSchedules?$filter=asTarget()` —
    keeping `assignmentType == Activated` (case-insensitive) with
    `scheduleInfo.startDateTime` in the future, and emit `.scheduled` entries
    (skipped if the key already has an active or pending entry).
  - Cancel of a scheduled entry = the provider's `deactivate` (a
    `selfDeactivate` request on the schedule). If the service refuses, the
    error is shown as for any deactivate.
- `ActivationOutcome.Result.scheduled(ActiveAssignment)`;
  `ActivationCoordinator` maps `.scheduled` status to it.
- `QuickActivate` (`Coordination/QuickActivate.swift`), pure and tested:
  `enum Decision { case ready(ActivationRequest), needsDialog(String) }`,
  `static func decide(role: EligibleRole, memory: RoleMemory?, now: Date) -> Decision`
  (justification required and none remembered → needsDialog "no remembered
  reason"; ticket → "ticket required"; approval → "approval required";
  otherwise ready with `min(memory.lastDuration ?? default, maximum)`).
  `static func decide(profile items: [ProfilePlanItem], justification: String?) -> Decision`
  style helper for profiles: needsDialog when any `.activate` item's policy
  requires a ticket, when any is `.notLoaded`, or when no reason is stored and
  any item requires one; else ready (returns the requests).
- `PanelStatus`: `.scheduled` sets `pendingApproval` (the clock) — rename the
  field to `attention` is not worth the churn; document it.
- `ActiveSummary.order`: active, then scheduled by start, then pending, then
  provisioning. `ExtendWindow`: `.scheduled` never extends.
- `ProfilePlanner`: `.scheduled` → `.pending` disposition.
- `Countdown.until(_ date:, now:)` label helper "2 h 15 m" for starts (new,
  tested), distinct from the HH:MM expiry label.

## 3. App

- `AppModel.quickActivate(_ key: RoleKey) async -> Bool` (true = handled;
  false = caller opens the dialog), `quickRun(profileId:) async -> Bool`
  (same contract for the sheet). Both post a completion notification through
  a new `notifier.notify(title:body:)` (protocol `ExpiryNotifying` extended;
  `NoopNotifier` too).
- Views: `AssignmentControls` Activate and Extend buttons check
  `NSEvent.modifierFlags.contains(.option)` in their action and call
  `quickActivate` first; `ProfilesRow` chips likewise call `quickRun`. Help
  texts: "Activate… Option-click to activate with the last reason".
- `ActivationView` and `RunProfileView`: `Toggle("Start at")` + `DatePicker`
  (`.dateAndTime`, `in: Date.now...`); the chosen start goes into every
  request. The result label shows "Scheduled" (calendar glyph) for
  `.scheduled` outcomes.
- `AssignmentControls` `.scheduled` case: caption "starts in <Countdown.until>"
  and a Cancel button calling `model.deactivate(key)`. `ActiveRow` dot: blue
  for scheduled. `AppModel.activate` stores `.scheduled` outcomes in `active`.
- `ExpiryNotifier.reschedule` ignores `.scheduled` (no expiry notification
  until it becomes active on a later refresh).
- Global hot key: `App/HotKeyCenter.swift` (`@MainActor final class`, Carbon
  `RegisterEventHotKey` + `InstallEventHandler`, one registered key,
  `register(binding:)`, `unregister()`, `onFire: () -> Void`).
  `HotKeyBinding: Codable, Equatable { keyCode: UInt32; modifiers: UInt32;
  display: String }` stored in `AppSettings` (`hotKey` JSON in UserDefaults)
  with `hotKeyProfileId: UUID?`. `AppModel` re-registers on change and on
  launch; firing calls `quickRun(profileId:)`, and when that returns false
  opens the run sheet instead. Settings section "Global shortcut": a
  `HotKeyRecorder` view (click to record; `NSEvent.addLocalMonitorForEvents`
  captures the next key with modifiers; Escape cancels; requires at least one
  of ⌘⌃⌥), a `Picker` of profiles, and "Clear".

## 4. Errors

Quick activate failures surface as a notification with the server message and
as the usual red row/progress state. A hot key registration failure (key in
use) shows inline in Settings. Scheduling in the past is prevented by the
picker range; a service refusal shows in the dialog as any failure.

## 5. Testing

Core: `QuickActivateTests` (each needsDialog reason, ready duration cap,
profile variant), `CountdownTests` (until label), provider tests for
`startDateTime` in bodies and `.scheduled` on future start (Entra, Azure,
Group activate) and for schedule reads (`entra-schedules.json`,
`arm-schedules.json`, `group-schedules.json` fixtures), `ActiveSummary`/
`PanelStatus`/`ExtendWindow`/`ProfilePlanner` cases for `.scheduled`.
App: build + manual: Option-click a role, Option-click a chip, schedule a
role 10 minutes ahead and see "starts in", press the hot key.
