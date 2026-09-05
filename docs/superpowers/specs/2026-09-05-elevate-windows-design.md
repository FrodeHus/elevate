# Elevate for Windows — design

Date: 2026-09-05. Status: approved direction (functionality parity with the macOS app "as far as possible"), pending user review of this document.

## 1. Goal

A Windows 11 system-tray app with the same functionality as Elevate for macOS:
sign in with one or more Entra identities (own app registration via the Windows
broker, or the Azure CLI / Azure PowerShell first-party apps), list each identity's
tenants and eligible PIM roles (Entra directory roles and Azure resource roles;
PIM for Groups follows the macOS phase 3), activate with duration and remembered
reason, bulk activate, deactivate (5-minute rule), cancel pending approvals,
countdowns, expiry toasts with Extend, manual role configuration when discovery is
refused, offline awareness, and Settings for the client id. Installable with
`winget install Reothor.Elevate` from an MSI.

Non-goals for v1: Microsoft Store listing, auto-update (winget upgrade covers it),
ARM64-only optimisations (build x64 and arm64 both), localisation.

## 2. Stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10 (LTS), C# 14, `net10.0-windows10.0.22621.0`, x64 + arm64 |
| UI | WinUI 3 (Windows App SDK 1.8 stable line), unpackaged (`WindowsPackageType=None`), self-contained Windows App SDK runtime |
| Tray | `Shell_NotifyIcon` via CsWin32 P/Invoke in a small `TrayIcon` class; left click toggles the flyout, right click shows a native context menu (Open, Settings, Quit) |
| Flyout | A borderless, non-resizable `Window` positioned above the taskbar near the tray icon, Mica Alt backdrop, rounded corners, closes on deactivation (like the Wi-Fi flyout) |
| Auth (own app) | MSAL.NET `Microsoft.Identity.Client` + `Microsoft.Identity.Client.Broker` (WAM), redirect `ms-appx-web://microsoft.aad.brokerplugin/{clientId}`; fallback to system browser `http://localhost` when the broker is unavailable |
| Auth (first-party) | MSAL.NET public client with `WithRedirectUri("http://localhost")`, system browser, no broker |
| Token cache | MSAL cache serialised with `Microsoft.Identity.Client.Extensions.Msal` (DPAPI-protected file under `%LOCALAPPDATA%\Elevate`) |
| HTTP | `HttpClient` behind an `IHttpClient` seam (same shape as the Swift `HTTPClient`) |
| State | `%LOCALAPPDATA%\Elevate\state.json`, same schema as macOS (`AppState`) so the JSON is cross-readable |
| Notifications | Windows App SDK `AppNotificationManager` toasts; Extend button as a toast action that activates the app |
| Packaging | WiX Toolset v5 MSI (per-user, `%LOCALAPPDATA%\Programs\Elevate`, Start Menu shortcut, run-at-login toggle in Settings via the `Run` registry key), signed with Azure Trusted Signing; winget manifest in `microsoft/winget-pkgs` (`Reothor.Elevate`) |
| CI | GitHub Actions `windows-latest`: build, test, `dotnet publish` for x64/arm64, WiX build, sign, attach MSI to the release; a second job validates the winget manifest with `winget validate` |
| Tests | xUnit + `Microsoft.NET.Test.Sdk`; the Core test suite ports 1:1 from `ElevateCoreTests` with the same fixtures |

## 3. Solution layout (see the repository restructure)

```
windows/
  Elevate.sln
  src/Elevate.Core/            class library: Models, Auth (ITokenProvider, InteractionRetry), Providers (Graph transport, Entra, Azure), Catalogue, Storage, Discovery, Coordination
  src/Elevate.App/             WinUI 3 app: Tray, Flyout, Views (Panel, Activation, BulkActivation, ConfigureRoles, AddAccount, Settings), Notifications, Auth (MsalTokenProvider, CompositeTokenProvider)
  tests/Elevate.Core.Tests/    xUnit port of ElevateCoreTests, fixtures copied from the macOS tests
  installer/Elevate.wxs        WiX v5 package definition
  winget/Reothor.Elevate.yaml  manifest (installer, locale, version)
  Directory.Build.props        shared settings (nullable, warnings as errors, analyzers)
```

The Entra roles catalogue JSON and the Perl regeneration script move to `shared/` and are consumed by both apps.

## 4. Core port rules

- One C# type per Swift type, same names (`Identity`, `TenantContext`, `RoleScope` as a discriminated record hierarchy, `RoleKey`, `RolePolicy`, `EligibleRole`, `ActiveAssignment`, `ActivationRequest`, `PimError` as an exception hierarchy with the same cases, `SignInMethod`).
- `IPimProvider`, `ITokenProvider`, `IHttpClient` mirror the Swift protocols; `ActivationCoordinator` keeps the group-by-tenant, sequential-within-tenant, one-interactive-retry semantics.
- `GraphTransport`/ARM error mapping identical, including 429, claims challenges (`WWW-Authenticate` claims → MSAL `WithClaims`), `ActiveDurationTooShort`.
- JSON: `System.Text.Json` with the same wire models; `state.json` schema identical (camelCase, `signInMethod` default `ownApp`, `azureUnavailableReason`).
- Persistence generation counter and quarantine of an unreadable state file, as on macOS.

## 5. App behaviour parity

Everything in the macOS specs §9–§10 (phase 1), §5–§6 (phase 2), and §5–§6 (sign-in methods) applies, mapped to Windows idioms:

- Flyout ≈ menu bar panel: same hierarchy (account rows with accent, tenant headers pinned via `ListView` group headers, role rows with status dot, countdown, Activate/Deactivate/Cancel, in-flight ring, select mode with a bottom "Activate N roles" button, offline `InfoBar`).
- Windows: Activation, Bulk activation, Configure known PIM roles (three tabs), Add account, Settings — separate `Window`s with Mica.
- Setup state: flyout shows "Complete initial setup" with "Open Settings" and "Continue with the Azure CLI app".
- Notifications: toast 5 minutes before expiry with an Extend action; denied permission → `InfoBar`.
- Client-id change signs out only own-app accounts; first-party accounts survive.

## 6. Differences accepted

- MFA step-up: MSAL.NET handles claims challenges via `WithClaims`; no custom loopback listener is needed because MSAL.NET implements `http://localhost` itself.
- Keychain → DPAPI-protected MSAL cache file; no separate refresh-token store.
- Sticky headers: `ListView` grouped headers pin one level natively, which matches the macOS "tenant header carries the account caption" design.
- The 5-minute deactivation lock, bulk grouping and countdown logic are identical.

## 7. Tests

Port every `ElevateCoreTests` suite (models, duration/date parsing, claims challenge, Entra provider, Azure provider, catalogue/manual source, state store with generation ordering and quarantine, tenant discovery, coordinator, countdown, sign-in method coding, PKCE/auth-code client are NOT needed since MSAL.NET covers OAuth). Add an `Elevate.App.Tests` project for `CompositeTokenProvider` routing and the app-model logic that macOS could not test (the ViewModel is plain C#, so it is testable).

## 8. Release

- Tag `windows-v1.0.0` → CI builds, signs and publishes `Elevate-1.0.0-x64.msi` and `-arm64.msi`.
- winget: first submission via PR to `microsoft/winget-pkgs` with the manifest in `windows/winget/`; later versions via `wingetcreate update`.
- Signing: Azure Trusted Signing (recommended, low cost) or an OV certificate; unsigned MSIs trigger SmartScreen and are refused by winget moderation.
