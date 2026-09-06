# Continuing the Windows app

Two plans are implemented: the first phase (`docs/superpowers/plans/2026-09-05-elevate-windows.md`,
Tasks 1–13) and the parity phase (`docs/superpowers/plans/2026-09-06-elevate-windows-phase3.md`,
epic #18): `Elevate.Core` (portable), `Elevate.App.Model` (the testable app half), `Elevate.App`
(WinUI 3 tray app), the MSI, the winget manifest generator and the Windows workflow. This file is
the handover for whoever continues: what exists, what changed from the plans' wording, the gotchas
that cost time, and what is left.

## What exists

| Area | Where |
|---|---|
| Core port, including the phase-3 planner, quick activate, summary and approval providers | `src/Elevate.Core`, `tests/Elevate.Core.Tests` (272 tests) |
| App model: accounts, refresh, activation, panel, profiles, approvals, operations | `src/Elevate.App.Model/ViewModels/AppModel.*.cs`, `tests/Elevate.App.Tests` (53 tests) |
| Settings, network monitor, notifier seam, hot-key seam, update checker, build info | `src/Elevate.App.Model/Services` |
| Tray icon, hot key, flyout, window chrome | `src/Elevate.App/Tray`, `Shell` |
| Flyout content: pinned Approvals and Active now, profiles row, bulk bar, update banner | `src/Elevate.App/Views/PanelView.xaml`, `PanelItems.cs`, `Widgets.cs` |
| Windows: activation, configure roles, add account, settings, tenants, save / manage / run profile, decision | `src/Elevate.App/Views/*Window.xaml` |
| Expiry and approval toasts, run at login | `src/Elevate.App/Notifications`, `Services/StartupRegistration.cs` |
| Installer, winget, CI | `installer/`, `winget/`, `.github/workflows/windows.yml`, `README.md` |

`dotnet test Elevate.sln` runs both suites on Windows with warnings as errors; the Core suite
alone runs anywhere.

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

- `--flyout` opens the flyout at once; `--show <settings|add-account|configure|activation|bulk|add-tenant|discover|save-profile|manage-profiles|run-profile|decision>` opens one window. Both exist for screenshots and smoke tests and are harmless in production.
- A second launch does not start twice: it broadcasts `Reothor.Elevate.Open` and the running instance opens its flyout. **The installed app counts**: quit it (tray menu) before running a dev build, or the dev build just opens the installed one's flyout.
- The tray icon starts in the taskbar's overflow (the `^` chevron) like every new app; drag it out or open it from there.
- Shell failures (unhandled exceptions, toast failures) go to `%LOCALAPPDATA%\Elevate\elevate.log`; user-visible errors also accumulate in `AppModel.ErrorLog`, which Settings → Copy diagnostics renders.
- To exercise the flyout without signing in, back up `%LOCALAPPDATA%\Elevate\state.json` and `settings.json`, then seed `state.json` with an own-app identity, tenants in `manualRoles` mode, `manualRoles` entries and profiles (the golden fixture `tests/Elevate.Core.Tests/Fixtures/state-macos.json` shows the shape) and clear `clientId` in `settings.json`. Own-app identities survive bootstrap only without a client id; with one they are reconciled against the MSAL cache and dropped, and first-party ones always are. Durations in `state.json` are Swift's `[high, low]` 128-bit attosecond pair, not seconds.
- Approvals cannot be seeded: they are session-only and read from Graph/ARM on every refresh.

## Rulings that changed the plan text

The Core rulings from the previous handovers still hold (Swift-compatible `state.json` coding,
`AssignmentStatus` and `SignInMethod` as records, the ten `PimException` kinds, a fully ported
`GroupProvider`, a synchronous lock-guarded `AppStateStore`, concurrent `onProgress`). The app
work added these:

- **`Elevate.App.Model` is a separate class library** (net10.0-windows, no WinUI) holding `AppModel`, `AppSettings`, the network monitor, the notifier and hot-key seams, `StartupRegistration`, `UpdateChecker`, `BuildInfo` and every token provider. A WinUI executable cannot be referenced by a plain xUnit project, and this split is what makes `Elevate.App.Tests` possible.
- **`AppModel` is one partial class in eight files** mirroring the Swift extensions (`Accounts`, `Activation`, `Refresh`, `Panel`, `Profiles`, `Approvals`, `Operations`). It is single-threaded on the UI thread: it captures the constructing thread's `SynchronizationContext` and marshals coordinator progress, network changes and the hot key through `Post`. Views redraw on the coarse `Changed` event; the flyout reconciles its row objects by key so the list keeps its scroll position.
- **Windows has no loopback provider.** MSAL.NET handles `http://localhost` itself, so `SignInMethod.Custom` and the two first-party methods all go through `FirstPartyTokenProvider` and the own app through `MsalTokenProvider` (WAM with the browser fallback). One `InteractiveGate` serialises interactive sign-ins across providers.
- **Sign-in dialogs are parented to `App.InteractionAnchor`**: the window that asked, else the flyout, else the tray's hidden window.
- **Toasts are timed in-process.** `AppNotificationManager` cannot schedule, so `ExpiryNotifier` keeps its own timer. Approval and quick-activate toasts go through the same `NotifyAsync`.
- **Option-click is Ctrl-click.** `PanelView` reads the Control key state at click time (`InputKeyboardSource.GetKeyStateForCurrentThread`) for Activate, Extend and the profile chips.
- **The global shortcut is `RegisterHotKey` on the tray's hidden window**, which receives `WM_HOTKEY`; `HotKeyCenter` implements the model's `IHotKeyCenter` seam. A binding is a virtual key plus `MOD_*` flags, recorded by `HotKeyRecorder` (a button that captures the next key press; Ctrl, Alt or Win required) and stored in `settings.json`.
- **The update check reads the releases list**, not `releases/latest`, and keeps the first published `v*` release that carries an MSI: both platforms share one tag, but a macOS-only hotfix without an MSI must not offer itself to Windows users.
- **`BuildInfo`** takes the version from the assembly's informational version (`installer/build.ps1` sets it from the tag) and the signing state from the executable's Authenticode signature; "Unsigned" for the current releases.
- **Windows App SDK 1.8** (1.8.260804001), self-contained in the MSI; .NET itself is framework-dependent (`Microsoft.DotNet.Runtime.10`).
- **The winget manifest is generated, not committed, and not submitted.** Releases are unsigned; winget moderation needs signed installers, so submission waits for Azure Artifact Signing (issue #17).

## Gotchas

- **Direct `dotnet build` of the WinUI project defaults `Platform` to AnyCPU**, which WinUI refuses; the csproj coerces it to x64 and derives `RuntimeIdentifier` from it.
- **CsWin32**: list constants by the enum that declares them (`NOTIFY_ICON_DATA_FLAGS`, `HOT_KEY_MODIFIERS`); `POINT` maps to `System.Drawing.Point`; `HWND_BROADCAST` is `HWND.HWND_BROADCAST`; friendly overloads appear only when the matching `SafeHandle` type is generated, so `TrayIcon` uses the raw `HMENU` overloads with `fixed` strings. Generated types are internal: anything exposing `HWND`/`RECT`/`HICON` must be internal too.
- **x:Bind cannot live in `App.xaml`**; DataTemplates with `x:Bind` belong in the window's or control's own resources with `x:DataType`. Functions in `x:Bind` must be on the `DataType`, so the row view models expose brushes and weights as properties.
- **`ItemsRepeater` sets no `DataContext` on x:Bind templates.** A click handler on a chip cannot read `DataContext`; resolve the item with `ItemsRepeater.GetElementIndex(element)` (the profiles row does this). A `ListView` sets it.
- **Grouped `ListView`**: the group object must itself be the `ObservableCollection` of its rows for the `CollectionViewSource` to track item changes.
- **`SelectorBarItem` with custom content** needs explicit `Padding` or the text clips.
- **The flyout's body row is the `*` row and `FlyoutWindow`'s root has `MaxHeight="640"`**; with every row `Auto` the bulk bar and footer were clipped off the bottom once the list grew. Keep the window's `MaxHeight` and the `*` row together.
- **Auto-height dialogs clamp to the work area** (`DialogWindows.Configure`); a tall window (Settings) puts its content in a `ScrollViewer` so the buttons stay reachable.
- **Flyout activation**: `Activated` may report `Deactivated` right after `Show` when the shell refuses focus to a new window, so `FlyoutWindow` ignores deactivation for 500 ms after showing, and a tray click within 300 ms of a hide counts as "close" rather than "reopen".
- **`AppModel` continuations after `await` resume on the UI thread only when a `SynchronizationContext` is set**: `Program.Main` installs a `DispatcherQueueSynchronizationContext` before creating `App`. Tests run under xUnit's own context.
- **`StubHttpClient` routes by substring**: the Entra and group approval endpoints both contain `filterByCurrentUser(on='approver')`; route on `directory/roleAssignmentScheduleRequests/…` or `privilegedAccess/group/…` to tell them apart.
- **PowerShell**: `$Args` is the automatic `$args`; a script parameter with that name silently receives nothing.
- **WiX 5 `Files Include="…\**"` harvests the publish folder**; there is no `File` id for the exe, so the shortcut and the launch-after-install action use `[INSTALLFOLDER]Elevate.exe`.
- **Warnings as errors** is inherited from `Directory.Build.props`; the app project scopes `NoWarn` to the analyzer and CsWinRT warnings the generated code raises. `X509Certificate.CreateFromSignedFile` is obsolete but the only loader that reads an Authenticode signature off a PE file; `BuildInfo` suppresses `SYSLIB0057` around that one call.

## What is left

- **Issue #17, code signing and winget submission**: needs an Azure Artifact Signing account (paid subscription, organization identity validation, certificate profile, a signer app registration), the `AZURE_*` repository secrets, then `wingetcreate submit` of the `winget-manifest` artifact. `docs/releasing.md` describes the steps; none of it is code. Releases are shared with macOS since 1.2.0: one `v*` tag, one release, `.github/workflows/release.yml`.
- The Windows design canvas in `docs/design` predates the profiles row, the Approvals group and the new windows; refresh it when the next visual change lands.
