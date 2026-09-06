# Continuing the Windows app on a Windows machine

The portable half of the Windows plan is done: `Elevate.Core` (the C# port of the Swift `ElevateCore`)
and its xUnit suite build and pass on any OS with the .NET 10 SDK. What remains needs Windows 11:
the WinUI 3 tray app, MSAL/WAM sign-in, toasts, the MSI, winget and the Windows CI job.

## What exists on this branch

| Plan task | Status | Where |
|---|---|---|
| 1 Solution scaffold, embedded roles catalogue | done | `windows/Elevate.sln`, `Directory.Build.props`, `global.json` |
| 2 Models, `PimException`, JSON conventions | done | `src/Elevate.Core/Models`, `src/Elevate.Core/Json` |
| 3 Duration, Graph date, countdown helpers | done | `src/Elevate.Core/Support` |
| 4 HTTP seam, claims challenge, token provider contract, test doubles, fixtures | done | `src/Elevate.Core/Networking`, `src/Elevate.Core/Auth`, `tests/Elevate.Core.Tests/Support`, `tests/Elevate.Core.Tests/Fixtures` |
| 5 Graph transport, Entra directory provider, PIM-for-Groups provider, policy rules, token claims | done | `src/Elevate.Core/Providers`, `src/Elevate.Core/Auth/AccessTokenClaims.cs` |
| 6 Azure resource provider | done | `src/Elevate.Core/Providers/AzureResourceProvider.cs` |
| 7 Catalogue, manual roles, `AppState` + store, tenant discovery, activation coordinator, activation profiles | done | `src/Elevate.Core/Catalogue`, `Storage`, `Discovery`, `Coordination` |
| 8 App scaffold: tray icon, flyout window, view model | **next** | `src/Elevate.App` (to create) |
| 9 Authentication providers (MSAL + WAM, first-party, composite) | pending | |
| 10 Flyout content | pending | |
| 11 Activation, configure roles, add account, settings windows | pending | |
| 12 Notifications, run at login, polish | pending | |
| 13 Installer, winget, CI | pending | |

Test count at handover: 173 in `Elevate.Core.Tests`, all green with warnings as errors.

## Set up the Windows machine

1. Windows 11 23H2 or newer.
2. .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). `windows/global.json` pins `10.0.100` with `rollForward: latestFeature`, so any 10.0.x works.
3. Visual Studio 2026 (or 2022 17.14+) with the **Windows application development** workload, or the standalone Windows App SDK 1.8 templates and the Windows 11 SDK 10.0.22621. `dotnet build` of the WinUI project needs the SDK build tools that ship with that workload.
4. WiX Toolset v5: `dotnet tool install --global wix`.
5. `winget` (ships with App Installer) for `winget validate` in Task 13.
6. Git, and Claude Code with the superpowers plugin if you continue with subagent-driven development.

Verify the port before touching the app:

```powershell
cd windows
dotnet test Elevate.sln
```

Expect 173 passed. If the count differs from what the branch's last commit reports, stop and look at
the git log first.

## Resume the plan

The plan is `docs/superpowers/plans/2026-09-05-elevate-windows.md`; the spec it argues from is
`docs/superpowers/specs/2026-09-05-elevate-windows-design.md`; the UI reference is
`docs/design/elevate-windows.html` (open it in a browser; it follows the OS theme).

If you use the `superpowers:subagent-driven-development` skill, it keeps its ledger in the gitignored
directory `.superpowers/sdd/2026-09-05-elevate-windows/progress.md`. That ledger does not travel with
the branch, so recreate it before the first dispatch so the skill does not redo Tasks 1–7:

```
# SDD ledger — plan: docs/superpowers/plans/2026-09-05-elevate-windows.md
Task 1: complete (review clean)
Task 2: complete (review clean)
Task 3: complete (review clean)
Task 4: complete (review clean)
Task 5: complete (review clean)
Task 6: complete (review clean)
Task 7: complete (review clean)
```

Then start at Task 8. Work on a branch such as `windows-phase-1-app` from this branch's merge commit.

## Rulings that changed the plan text

Later tasks must build on these, not on the plan's original wording.

- **`state.json` is shared with macOS, so the wire format follows Swift's synthesised coding**, not the
  plan's `kind` discriminator. `RoleScope` serialises as a single-key object
  (`{"entraDirectory":{"roleDefinitionId":…,"directoryScopeId":…}}`), assignment status as
  `{"active":{}}` or `{"failed":{"_0":"reason"}}`, `TimeSpan` as Swift's `Duration` pair
  `[Int64 high, UInt64 low]` of the 128-bit attosecond value, `Guid` upper-case. All of this lives in
  `Elevate.Core.Json.Options`; always serialise through it. Interop is semantic, not byte-identical:
  Swift pretty-prints with `"key" : value` and escapes `/`, .NET does not.
- **`AssignmentStatus` is a record, not an enum**: `AssignmentStatus.Active`, `.PendingApproval`,
  `.PendingProvisioning`, `.Scheduled`, `AssignmentStatus.Failed(reason)`; switch on `status.Kind`.
- **`SignInMethod` is a readonly record struct** with `OwnApp`, `AzureCLI`, `AzurePowerShell` and
  `Custom(clientId)`, because the macOS app grew a custom-client-id method. `default` equals `OwnApp`.
  `UsesMsal` is true only for `OwnApp`; the first-party and custom methods carry their own client id.
- **`PimException(kind, detail, status)`** has ten kinds (the Swift set), `UserMessage` text identical
  to macOS. Map MSAL exceptions onto these kinds in Task 9 exactly as the plan says.
- **`GroupProvider` is fully ported** (the plan said stub). The Groups pivot in the flyout can be real
  from the start.
- **`ActivationProfile` and the `AppState` profile helpers are ported** so profiles survive a shared
  state file; the profile planner, quick activate, approvals, activation summary, panel status and the
  operations helpers (diagnostics, update check, error log) are **not** ported. They belong to a
  follow-up plan once the app reaches parity with the macOS phase-1/2 scope.
- **`AppStateStore` is synchronous** and lock-guarded, with generation-guarded saves and quarantine to
  `state.json.bak`. Call it from a background thread or accept the small file write on the UI thread.
- **`ActivationCoordinator.ActivateAsync` invokes `onProgress` concurrently** from several threads
  (one per tenant group). Marshal to the `DispatcherQueue` before touching UI state. Cancellation
  escapes as `OperationCanceledException` rather than being folded into outcomes.
- **`ActivationProfile` is a reference type** whose `Entries` list is shared by `with` copies. Clone the
  list before mutating a copy.
- **`Scopes.GroupAll`** exists alongside `GraphAll`, `GraphUserRead` and `ArmAll`; the group provider
  needs the three `*.AzureADGroup` scopes, which the app registration must request (see
  `docs/entra-app-registration.md`).

Deferred minor findings from the reviews are listed in the pull request description for this branch;
none block the app work.

## Task-by-task notes for 8–13

- **Task 8.** `AppModel` should mirror `macos/Sources/ElevateApp/App/AppModel.swift` and its
  `AppModel+*.swift` extensions for structure; only the phase-1/2 responsibilities are in scope now.
  The Core seams it needs (`ITokenProvider`, `IHttpClient` via `HttpClientAdapter`, `AppStateStore`,
  `ActivationCoordinator`, `TenantDiscovery`, `ManualRoleSource`, `RoleCatalogue`) all exist. Keep
  `Elevate.Core` free of Windows references; put `INetworkMonitor`, `IExpiryNotifier` and
  `AppSettings` in the app project as the plan says.
- **Task 9.** `InteractionRetry.RunAsync` already implements the one-interactive-retry rule and passes
  claims through; the MSAL provider only needs `AcquireInteractivelyAsync` to honour `claims` via
  `WithClaims`. `FakeTokenProvider` in the test project records sign-outs and interactive calls, so
  `CompositeTokenProvider` routing tests can reuse it.
- **Task 10 and 11.** Follow `docs/design/elevate-windows.html` for layout, counts on the pivots, the
  pinned "Active now" group, and the sign-in captions ("Supports activation of Azure resource roles
  only" for the first-party apps).
- **Task 12.** The tray glyph is the double chevron from the app icon
  (`macos/Sources/ElevateApp/Assets.xcassets/MenuBarIcon.imageset` has the extracted mask; regenerate
  `.ico` files from `docs/images/icon.png` at 16, 20, 24, 32 px).
- **Task 13.** The macOS release workflow (`.github/workflows/release.yml`) shows the tag → release →
  cask pattern; mirror it with `windows-v*` tags and the MSI assets. `CHANGELOG.md` gets a Windows
  section when the first tag is cut.

## Follow-up plan after Task 13

Write a new spec and plan for parity with the macOS features that postdate the Windows spec:
activation profiles (planner, run sheet), quick activate and global shortcut, approvals, per-tab
activation counts, scheduled activations, diagnostics, update check, launch at login. The Core types for
several of these can be ported from Swift in one task; the rest is app work.
