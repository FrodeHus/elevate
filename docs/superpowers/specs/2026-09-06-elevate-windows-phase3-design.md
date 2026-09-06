# Elevate for Windows — phase 3: parity and release hardening

Date: 2026-09-06. Follows `2026-09-05-elevate-windows-design.md` (the Windows app through plan
Task 13) and brings the Windows app level with the macOS features that postdate it: activation
profiles (`2026-09-05-elevate-profiles-design.md`), quick activate and the global shortcut
(`2026-09-05-elevate-shortcuts-design.md`), approvals (`2026-09-05-elevate-approvals-design.md`)
and the operations set — diagnostics and the update check (`2026-09-06-elevate-operations-design.md`).
Epic #18; issues #11–#17.

## 1. Goal

The same behaviour as the macOS app for the four features above, on the same Core seams, with the
deferred Core findings closed and the release pipeline ready for signing. Success criteria are the
macOS specs' criteria, read with the Windows substitutions in §3.

## 2. Core (issue #11, #16)

`Elevate.Core` gains straight ports, one C# type per Swift type: `ProfilePlanner` /
`ProfilePlanItem`, `QuickActivate` / `QuickActivateDecision`, `ActivationSummary`,
`ApprovalRequest` / `ApprovalAction` / `ApprovalDiff`, `IApprovalProvider` with the Entra, Azure
and group providers over the shared `GraphApprovals`, `AppVersion`, `ErrorLog` / `DiagnosticsError`
and `DiagnosticsReport`. The Swift test suites port against the approval fixtures already staged.
`Elevate.Core` keeps no Windows reference.

The deferred findings are fixed as one task: `@odata.nextLink` in `EntraDirectoryProvider` (both
platforms), the shared caller object-id and request-status helpers, `GraphJson.ParseDate` moved to
the tests, `ITokenProvider` defaulting its cancellation token like `IPimProvider`, `ArmUrl`
percent-encoding the path, non-ASCII JSON left unescaped, grapheme-safe truncation.

## 3. Windows substitutions

| macOS | Windows |
|---|---|
| Option-click on Activate / Extend / a profile chip | **Ctrl-click** (the modifier is read from the keyboard state at click time) |
| Carbon `RegisterEventHotKey` | `RegisterHotKey` on the tray's hidden window; `WM_HOTKEY` arrives in its window procedure. The binding is a virtual key plus `MOD_*` flags, recorded in Settings by a control that captures the next key press; at least one of Ctrl, Alt or Win is required |
| `UserDefaults` keys | Fields in `%LOCALAPPDATA%\Elevate\settings.json`: `collapsedApprovals`, `lastApprovalJustification`, `seenApprovalIds`, `hotKey` (`{modifiers,key,display}`), `hotKeyProfileId`, `lastUpdateCheck`, `dismissedUpdateVersion` |
| Sheets opened by `PanelRoute` | Secondary windows through `App.Open…` (one per kind, like the existing ones): Save profile, Manage profiles, Run profile, Decision |
| Menu-bar glyph badge | A count badge in the tray icon for pending approvals, drawn by `TrayIconRenderer` next to the active count |
| `UNUserNotificationCenter` | `AppNotificationManager` toasts through the existing `IExpiryNotifier.NotifyAsync` |
| `NSPasteboard` | `Clipboard.SetContent` |
| GitHub `releases/latest` | The releases list filtered to `windows-v*` tags, because the repository also tags macOS releases; drafts and pre-releases are skipped |
| `BuildInfo` | The assembly's informational version; signing state read from the executable's Authenticode signature ("Unsigned" for the current releases) |

## 4. App model

`AppModel` gains the partials the macOS extensions have: `Profiles` (save, update, rename, delete,
move, begin editing, plan, run), `Approvals` (per-tenant-and-kind lists read opportunistically on
every refresh, decide with one interactive retry, announce once per new request, prune the seen
set after a full sweep, drop with the tenant or account) and `Operations` (apply the hot key,
diagnostics text, check for updates, dismiss). `Activation` gains `QuickActivateAsync` and
`QuickRunAsync`, reporting through `ActivationSummary` as a toast. `ErrorLog` becomes the Core
ring buffer. The hot key is behind an `IHotKeyCenter` seam so the model tests run without a
window; the app's implementation lives in the tray.

## 5. Views

- **Flyout**: a profiles row of chips under the pivots with "Manage…"; a pinned "Approvals" group
  above "Active now" with Approve / Deny per request (portal pointer for extend / renew), spinner
  while a decision is in flight, the last error under the row; an update `InfoBar` with Open and
  Dismiss; the bulk bar offers "Save as profile…" or "Update <name>" beside "Activate N". The body
  row is the flexible one, so the bulk bar and footer stay visible when the list is tall.
- **Windows**: Save profile (name, entries grouped by account and tenant with the resolved
  duration), Manage profiles (rename inline, move up / down, Run, Edit, Delete), Run profile
  (plan rows with a duration picker per activatable entry, skipped / not eligible / loading
  captions, reason, ticket, "Start at", per-row outcome), Decision (details, justification
  prefilled from the last one, Approve or Deny; Deny needs a reason).
- **Settings**: a "Global shortcut" card with the recorder, Clear, the profile picker and the
  registration error; "Check for updates" with the one-line result on the version card; "Copy
  diagnostics".

## 6. Release (issue #17)

Signing and winget submission need an Azure Artifact Signing account, repository secrets and a
submission to `microsoft/winget-pkgs`. None of that is code; the README and CONTINUING notes carry
the steps. The workflow keeps the signing step conditional on the secrets being present.

## 7. Non-goals

A persisted error log, notification actions on approval toasts, and an approvals count in the
pivot labels stay out, as on macOS.
