# Continuing the Windows app

The Windows plan (`docs/superpowers/plans/2026-09-05-elevate-windows.md`) is implemented through
Task 13: `Elevate.Core` (portable), `Elevate.App.Model` (the testable app half), `Elevate.App`
(WinUI 3 tray app), the MSI, the winget manifest generator and the Windows workflow. This file is
the handover for whoever continues: what exists, what changed from the plan's wording, the gotchas
that cost time, and what the follow-up plan should cover.

## What exists

| Plan task | Status | Where |
|---|---|---|
| 1–7 Core port | done | `src/Elevate.Core`, `tests/Elevate.Core.Tests` (198 tests) |
| 8 App scaffold: tray icon, flyout window, app model | done | `src/Elevate.App` (`Tray`, `Shell`), `src/Elevate.App.Model/ViewModels` |
| 9 Authentication providers | done | `src/Elevate.App.Model/Auth`, `tests/Elevate.App.Tests` |
| 10 Flyout content | done | `src/Elevate.App/Views/PanelView.xaml`, `PanelItems.cs`, `Widgets.cs` |
| 11 Activation, configure roles, add account, settings, tenant windows | done | `src/Elevate.App/Views/*Window.xaml`, `Shell/DialogWindows.cs` |
| 12 Notifications, run at login, polish | done | `src/Elevate.App/Notifications/ExpiryNotifier.cs`, `Services/StartupRegistration.cs` |
| 13 Installer, winget, CI | done | `installer/`, `winget/`, `.github/workflows/windows.yml`, `README.md` |

Test count: 198 in `Elevate.Core.Tests` and 30 in `Elevate.App.Tests`, all green with warnings as
errors. `dotnet test Elevate.sln` runs both on Windows; the Core suite alone runs anywhere.

## Set up the Windows machine

1. Windows 11 23H2 or newer (the app targets build 22000 and up).
2. .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). `global.json` pins `10.0.100` with `rollForward: latestFeature`.
3. The Windows 11 SDK 10.0.22621 (ships with the Visual Studio *Windows application development* workload, or standalone). `dotnet build Elevate.sln` builds the WinUI project without Visual Studio; the XAML compiler and CsWin32 come from NuGet.
4. WiX v5: `dotnet tool install --global wix --version 5.0.2`. **Not v6 or v7**: those require accepting the Open Source Maintenance Fee EULA, which is a decision for the maintainers, and `installer/build.ps1` pins 5.0.2 for the UI extension too.
5. `winget` (ships with App Installer) for `winget validate`.

Verify before touching anything:

```powershell
cd windows
dotnet test Elevate.sln
```

## Run and inspect the app

```powershell
dotnet build src/Elevate.App/Elevate.App.csproj
src\Elevate.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Elevate.exe --flyout
src\Elevate.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Elevate.exe --show settings
```

- `--flyout` opens the flyout at once; `--show <settings|add-account|configure|activation|bulk|add-tenant|discover>` opens one window. Both exist for screenshots and smoke tests and are harmless in production.
- A second launch does not start twice: it broadcasts `Reothor.Elevate.Open` and the running instance opens its flyout.
- The tray icon starts in the taskbar's overflow (the `^` chevron) like every new app; drag it out or open it from there.
- Shell failures (unhandled exceptions, toast failures) go to `%LOCALAPPDATA%\Elevate\elevate.log`; user-visible errors also accumulate in `AppModel.ErrorLog` for a future diagnostics report.
- To exercise the flyout without signing in, seed `%LOCALAPPDATA%\Elevate\state.json` with an own-app identity, tenants in `manualRoles` mode and `manualRoles` entries (the golden fixture `tests/Elevate.Core.Tests/Fixtures/state-macos.json` shows the shape). Own-app identities survive bootstrap without a client id; first-party ones are reconciled against the MSAL cache and dropped.

## Rulings that changed the plan text

The Core rulings from the previous handover still hold (Swift-compatible `state.json` coding,
`AssignmentStatus` and `SignInMethod` as records, the ten `PimException` kinds, a fully ported
`GroupProvider`, a synchronous lock-guarded `AppStateStore`, concurrent `onProgress`). The app
work added these:

- **`Elevate.App.Model` is a separate class library** (net10.0-windows, no WinUI) holding `AppModel`, `AppSettings`, the network monitor, the notifier seam, `StartupRegistration` and every token provider. A WinUI executable cannot be referenced by a plain xUnit project, and this split is what makes `Elevate.App.Tests` possible. Namespaces stay `Elevate.App.*` as the plan laid them out.
- **`AppModel` is one partial class in four files** (`AppModel.cs`, `.Accounts.cs`, `.Activation.cs`, `.Refresh.cs`, `.Panel.cs`) mirroring the Swift extensions. It is single-threaded on the UI thread: it captures the constructing thread's `SynchronizationContext` and marshals coordinator progress and network changes through `Post`. Views redraw on the coarse `Changed` event rather than fine-grained bindings; the flyout reconciles its row objects by key so the list keeps its scroll position.
- **Windows has no loopback provider.** MSAL.NET handles `http://localhost` itself, so `SignInMethod.Custom` and the two first-party methods all go through `FirstPartyTokenProvider` (no broker, resource `.default` scopes) and the own app through `MsalTokenProvider` (WAM with the browser fallback). `CompositeTokenProvider.IdentitiesAsync` unions every provider distinct by id, so first-party accounts are reconciled against their caches at bootstrap too, failing open when a cache cannot be read. One `InteractiveGate` serialises interactive sign-ins across providers.
- **Sign-in dialogs are parented to `App.InteractionAnchor`**: the window that asked (every secondary window registers itself while active), else the flyout, else the tray's hidden window.
- **Toasts are timed in-process.** `AppNotificationManager` cannot schedule, so `ExpiryNotifier` keeps its own timer; it registers before the model exists and handles toast launches through `AppInstance.GetActivatedEventArgs()`. `RescheduleNotificationsAsync` passes "Tenant · upn" as the tenant name so the toast body reads like the design.
- **Windows App SDK 1.8** (1.8.260804001) as the spec says, self-contained in the MSI; NuGet also has 2.x, which was not evaluated. .NET itself is framework-dependent: the winget manifest depends on `Microsoft.DotNet.Runtime.10` (the app needs only `Microsoft.NETCore.App`, not the desktop runtime).
- **The winget manifest is generated, not committed.** `winget/templates` plus `winget/New-Manifest.ps1` produce `winget/manifests/r/Reothor/Elevate/<version>/` from the release hashes; CI validates it and submits it with `wingetcreate` when `WINGET_TOKEN` is set. `windows/winget/manifests/` is gitignored.
- **Diagnostics, update check, profiles, approvals and quick activate are not in this phase**, as the previous handover said. `ErrorLog` and the Settings version row are the hooks for the first two.

## Gotchas

- **Direct `dotnet build` of the WinUI project defaults `Platform` to AnyCPU**, which WinUI refuses; the csproj coerces it to x64 and derives `RuntimeIdentifier` from it. The solution's `Any CPU` configuration maps the app to x64 too.
- **CsWin32**: list constants by the enum that declares them (`NOTIFY_ICON_DATA_FLAGS`, not `NIF_ICON`); `POINT` maps to `System.Drawing.Point`; `HWND_BROADCAST` is `HWND.HWND_BROADCAST`; friendly overloads appear only when the matching `SafeHandle` type is generated (listing `DestroyMenu` changes `CreatePopupMenu`'s return type), so `TrayIcon` uses the raw `HMENU` overloads with `fixed` strings. Generated types are internal: anything exposing `HWND`/`RECT`/`HICON` must be internal too.
- **x:Bind cannot live in `App.xaml`**; DataTemplates with `x:Bind` belong in the window's or control's own resources with `x:DataType`. Functions in `x:Bind` must be on the `DataType`, not on the page, so the row view models expose brushes and weights as properties.
- **Grouped `ListView`**: the group object must itself be the `ObservableCollection` of its rows for the `CollectionViewSource` to track item changes; a wrapper implementing `INotifyCollectionChanged` renders once and never updates.
- **`SelectorBarItem` with custom content** needs explicit `Padding` or the text clips.
- **Flyout activation**: `Activated` may report `Deactivated` right after `Show` when the shell refuses focus to a new window, so `FlyoutWindow` ignores deactivation for 500 ms after showing, and a tray click within 300 ms of a hide counts as "close" rather than "reopen".
- **`AppModel` continuations after `await` resume on the UI thread only when a `SynchronizationContext` is set**: `Program.Main` installs a `DispatcherQueueSynchronizationContext` before creating `App`. Tests run under xUnit's own context.
- **PowerShell**: `$Args` is the automatic `$args`; a script parameter with that name silently receives nothing (this hid the flyout during screenshots for a while).
- **WiX 5 `Files Include="…\**"` harvests the publish folder**; there is no `File` id for the exe, so the shortcut and the launch-after-install action use `[INSTALLFOLDER]Elevate.exe`. `WixUI_Minimal` already defines `ARPNOMODIFY`.
- **Warnings as errors** is inherited from `Directory.Build.props`; the app project scopes `NoWarn` to the analyzer and CsWinRT warnings the generated code raises.

## Follow-up plan

Write a spec and plan for parity with the macOS features that postdate the Windows spec:
activation profiles (planner, run sheet, save/manage), quick activate with a modifier click and a
global shortcut (`RegisterHotKey`), approvals (the three approval providers and the pinned section),
per-pivot activation counts on the tenant headers, diagnostics (copy report from `ErrorLog`), the
daily update check against the GitHub releases API, and the Core deferred findings from the
previous review (`@odata.nextLink` in `EntraDirectoryProvider`, the duplicated
`RequestPrincipalIdAsync`, `GraphJson.ParseDate`, `ct` placement, `ArmUrl` encoding). The Swift
`ProfilePlanner`, `QuickActivate`, `ActivationSummary` and approval types port in one Core task;
the rest is app work on the seams above.
