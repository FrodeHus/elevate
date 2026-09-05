# Elevate — activation profiles

Date: 2026-09-05. Extends `2026-09-04-pimtray-design.md` §6 (bulk activation)
and `2026-09-05-elevate-daily-panel-design.md`. Mockups approved on the
"Elevate UI" design canvas (artboards "Panel · profiles row", "Select mode ·
save as profile", "Run profile · confirm and progress").

## 1. Goal

Save a cross-account, cross-tenant, cross-tab selection of roles and groups
under a name, and run all of them with one click.

Success criteria:

- Select mode keeps its selection while switching tabs; the bulk bar shows
  "Save as profile…" beside "Activate N" (N counts every tab).
- "Save as profile…" asks for a name and shows the entries grouped by account
  and tenant with the duration that will be used; Save stores the profile.
- A **Profiles** row under the tabs shows one chip per profile (name plus a
  count such as "3 roles · 1 group"); clicking a chip opens the run sheet.
  "Manage…" opens a window to rename, reorder, edit and delete. The row is
  hidden when there are no profiles.
- The **run sheet** lists every entry grouped by account and tenant with a
  per-entry duration picker (remembered from the last run of that entry),
  one shared reason prefilled from the profile's last reason, entries that
  are already active shown as "already active · skipped", entries whose role
  is no longer eligible shown as "not eligible · skipped", approval-required
  entries flagged, then "Activate N". Progress and failures render per row as
  in bulk activation; "Done" closes.
- Running a profile updates the profile's last reason and each entry's last
  duration, and the per-role memory used elsewhere.
- Edit reopens select mode with the profile's entries selected; the bulk bar
  then offers "Update profile" (and "Activate N").
- Profiles persist in `state.json`; an old file without profiles decodes.

Out of scope: scheduled runs, keyboard shortcuts, sharing profiles.

## 2. Core (`ElevateCore`)

```swift
public struct ActivationProfile: Codable, Hashable, Sendable, Identifiable {
    public struct Entry: Codable, Hashable, Sendable { public var roleKey: RoleKey; public var lastDuration: Duration? }
    public var id: UUID
    public var name: String
    public var entries: [Entry]
    public var lastJustification: String?
}
```

- `AppState.profiles: [ActivationProfile]` with a custom `init(from:)` that
  uses `decodeIfPresent` for every array (identities, tenants, manualRoles,
  memory, profiles) so older files decode. Helpers: `profile(id:)`,
  `upsertProfile`, `removeProfile(id:)`, `moveProfile(from:to:)`.
- `ProfilePlanner.plan(profile:roles:active:memory:) -> [ProfilePlanItem]`
  (pure, tested):
  `ProfilePlanItem { roleKey; role: EligibleRole?; duration: Duration; disposition }`
  with `disposition: .activate | .alreadyActive | .pending | .notEligible`.
  Duration = entry's `lastDuration`, else memory's `lastDuration`, else the
  policy default, capped at the policy maximum (manual default when the role
  is unknown). `.alreadyActive` when `active[key]?.status == .active`;
  `.pending` when pending approval or provisioning; `.notEligible` when no
  role exists for the key.
- `ProfileSummary.caption(entries:) -> String`: "N roles" / "N groups" /
  "N roles · M groups" (roles = Entra + Azure).

## 3. App

- `AppModel`: `profiles` (from state), `saveProfile(name:keys:) -> ActivationProfile`,
  `updateProfile(id:keys:)`, `renameProfile(id:name:)`, `deleteProfile(id:)`,
  `moveProfile(from:to:)`, `plan(for profile:) -> [ProfilePlanItem]`,
  `runProfile(id:items:justification:ticket:) async` (builds
  `ActivationRequest`s for `.activate` items, calls `activate`, then records
  the reason and durations on the profile and in memory), `editingProfileId:
  UUID?` (set by Edit, cleared when select mode ends), `selectionCount`.
  `selection` is no longer cleared on tab change; it is cleared when select
  mode turns off and when the search query changes.
- `PanelRoute` gains `.saveProfile([RoleKey])`, `.runProfile(UUID)`,
  `.manageProfiles`.
- Views: `ProfilesRow` (chips + "Manage…", under the tab picker, hidden when
  empty), `SaveProfileView`, `RunProfileView`, `ManageProfilesView` (list:
  rename inline, Edit, Delete; drag to reorder with `onMove`).
- Bulk bar: "Save as profile…" (or "Update profile" when `editingProfileId`
  is set) + "Activate N"; N = `selectionCount`; noun "role" when all selected
  keys are Entra/Azure, "group" when all groups, else "item".
- `ActivationView` unchanged; `RunProfileView` mirrors its bulk table and
  progress labels, reusing `DurationPicker`.

## 4. Errors

Run failures use the coordinator's per-row outcomes; the sheet stays open
with the failure text until Done. Saving a profile with an empty name is
disabled. Deleting asks no confirmation (undo is not needed for a small
list); Manage keeps working while a run is in progress.

## 5. Testing

Core: `ActivationProfileTests` (state decode without profiles, round trip,
upsert/remove/move), `ProfilePlannerTests` (duration precedence and cap,
each disposition), `ProfileSummaryTests`. App verified by build and by the
user: save from a cross-tab selection, chip run with a skipped active entry,
edit and update, rename, reorder, delete.
