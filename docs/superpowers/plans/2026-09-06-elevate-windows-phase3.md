# Elevate for Windows — phase 3 implementation plan

> Executed on Windows 11 with the .NET 10 SDK and the Windows App SDK 1.8 workload, on branch
> `windows-phase-3` from `main`. Every task ends with `dotnet test Elevate.sln` green with warnings
> as errors, and one commit.

**Spec:** `docs/superpowers/specs/2026-09-06-elevate-windows-phase3-design.md`.

## Tasks

- [x] **1. Core port (#11).** `Coordination/{ProfilePlanner,QuickActivate,ActivationSummary}.cs`,
  `Models/ApprovalRequest.cs`, `Providers/{ApprovalProvider,GraphApprovals,EntraApprovalProvider,
  AzureApprovalProvider,GroupApprovalProvider}.cs`, `Support/{AppVersion,ErrorLog,DiagnosticsReport}.cs`;
  tests ported from the Swift suites. Commit: "Core: port the profile planner, quick activate,
  activation summary and approval types".
- [x] **2. Core deferred findings (#16).** nextLink in Entra on both platforms; shared
  `GraphTransport.CallerObjectIdAsync` and `GraphSchedule.RequestStatus`/`Settle`; `ParseDate` to
  the tests; `ct = default` on `ITokenProvider`; `ArmUrl` path encoding; relaxed JSON escaping;
  `Text.Prefix`. Commit: "Core: resolve the deferred findings from the port reviews".
- [x] **3. App model.** `AppSettings` fields; `AppModel.{Profiles,Approvals,Operations}.cs`; quick
  activate / quick run in `AppModel.Activation.cs`; approval reads and prune in `AppModel.Refresh.cs`;
  `ErrorLog` on Core; `IHotKeyCenter`, `HotKeyBinding`, `UpdateChecker`, `BuildInfo` in `Services`;
  approval providers rebuilt with the coordinator. Tests in `Elevate.App.Tests`:
  `AppModelProfileTests`, `AppModelApprovalTests`, `AppModelOperationsTests`.
- [x] **4. Profiles UI (#12).** `ProfilesRow` in `PanelView`, bulk bar buttons, `SaveProfileWindow`,
  `ManageProfilesWindow`, `RunProfileWindow`; `App.OpenSaveProfile/OpenManageProfiles/OpenRunProfile`;
  the flyout's flexible body row (fixes the clipped bulk bar).
- [x] **5. Quick activate and the global shortcut (#13).** Ctrl-click on Activate / Extend and on
  chips; `HotKeyCenter` over `RegisterHotKey` on the tray window; `HotKeyRecorder` control and the
  Settings card; `PendingProfileRun` opens the Run window.
- [x] **6. Approvals UI (#14).** `ApprovalRow` and the pinned group in `PanelView`, `DecisionWindow`,
  the tray badge, approval toasts.
- [x] **7. Diagnostics and update check (#15).** Settings rows; the update `InfoBar` in the flyout;
  the daily check at startup.
- [x] **8. Docs and handover.** README, CONTINUING, the signing / winget steps for #17.
