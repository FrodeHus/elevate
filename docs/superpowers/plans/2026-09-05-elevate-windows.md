# Elevate for Windows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **This plan must be executed on Windows 11 with the .NET 10 SDK, the Windows App SDK 1.8 workload, WiX v5 (`dotnet tool install --global wix`) and `winget` installed.** Tasks 1–7 (Core) also build on macOS/Linux with the .NET 10 SDK.

**Goal:** A Windows 11 tray app with the macOS app's functionality: multi-account Entra sign-in (own app via WAM, or Azure CLI / Azure PowerShell apps), tenant and role discovery (Entra directory + Azure resource roles), activation with remembered reason, bulk activation, deactivation, cancel, countdown, expiry toasts, manual roles, offline awareness, Settings; shipped as a signed MSI and a winget manifest.

**Architecture:** `Elevate.Core` is a straight port of the Swift `ElevateCore` (same type names, same provider/coordinator seams, same `state.json` schema, same fixtures). `Elevate.App` is WinUI 3 unpackaged with a `Shell_NotifyIcon` tray icon, a Mica flyout window, MVVM view models (plain C#, testable), MSAL.NET for all auth (WAM broker for own-app; `http://localhost` system browser for first-party ids), Windows App SDK toasts. WiX v5 builds the MSI; GitHub Actions publishes it and validates the winget manifest.

**Tech Stack:** .NET 10 / C# 14, Windows App SDK 1.8 (WinUI 3), CommunityToolkit.Mvvm, MSAL.NET 4.7x + `Microsoft.Identity.Client.Broker` + `Microsoft.Identity.Client.Extensions.Msal`, CsWin32, System.Text.Json, xUnit + FluentAssertions, WiX v5, winget.

**Spec:** `docs/superpowers/specs/2026-09-05-elevate-windows-design.md` (and, for behaviour parity, the macOS specs it references).

## Global Constraints

- `TargetFramework` `net10.0-windows10.0.22621.0` for App and Core tests; `Elevate.Core` targets `net10.0` (no Windows dependency) so its tests run anywhere.
- `Directory.Build.props`: `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<LangVersion>latest</LangVersion>`, `<EnableNETAnalyzers>true</EnableNETAnalyzers>`.
- `Elevate.Core` references no UI, MSAL or Windows API; auth is behind `ITokenProvider`, HTTP behind `IHttpClient`.
- `state.json` schema and location parity: `%LOCALAPPDATA%\Elevate\state.json`, camelCase, `signInMethod` default `ownApp`, `azureUnavailableReason` optional, generation-guarded saves, quarantine to `state.json.bak` on decode failure.
- Fixtures: copy `macos/Tests/ElevateCoreTests/Fixtures/*.json` verbatim into `windows/tests/Elevate.Core.Tests/Fixtures/` (content items, `CopyToOutputDirectory=PreserveNewest`).
- First-party client ids: Azure CLI `04b07795-8ddb-461a-bbee-02f9e1bf7b46`, Azure PowerShell `1950a258-227b-4e31-a9cf-717495945fc2`. Own-app WAM redirect `ms-appx-web://microsoft.aad.brokerplugin/{clientId}`; system-browser redirect `http://localhost`.
- Every task ends with `dotnet build` and `dotnet test` green (App tasks: `dotnet build` of the solution; the App itself is verified by running it).
- Commit after each task with the message given. Branch `windows-phase-1` from `main`.

## File structure

```
windows/
  Elevate.sln
  Directory.Build.props
  global.json                         { "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }
  src/Elevate.Core/
    Elevate.Core.csproj
    Models/{Identity,TenantContext,RoleScope,RoleKey,Roles,PimError,SignInMethod}.cs
    Support/{Iso8601Duration,GraphJson,Countdown}.cs
    Networking/{IHttpClient,HttpClientAdapter,ClaimsChallenge}.cs
    Auth/{ITokenProvider,Scopes,InteractionRetry}.cs
    Providers/{IPimProvider,GraphTransport,EntraDirectoryProvider,AzureResourceProvider,GroupProvider}.cs
    Catalogue/{RoleCatalogue,ManualRoleSource}.cs   Resources/EntraBuiltInRoles.json (embedded)
    Storage/{AppState,AppStateStore}.cs
    Discovery/TenantDiscovery.cs
    Coordination/ActivationCoordinator.cs
  src/Elevate.App/
    Elevate.App.csproj  app.manifest  Package-less (WindowsPackageType=None)
    App.xaml(.cs)  Program.cs (single-instance mutex, AppNotificationManager registration)
    Tray/{TrayIcon.cs (CsWin32 Shell_NotifyIcon), TrayMenu.cs}
    Shell/{FlyoutWindow.xaml(.cs), WindowPlacement.cs, Mica.cs}
    ViewModels/{AppModel.cs, PanelViewModel.cs, ActivationViewModel.cs, ConfigureRolesViewModel.cs, AddAccountViewModel.cs, SettingsViewModel.cs}
    Views/{PanelView.xaml, IdentityHeader.xaml, TenantHeader.xaml, RoleRow.xaml, ActivationWindow.xaml, ConfigureRolesWindow.xaml, AddAccountWindow.xaml, SettingsWindow.xaml, SetupView.xaml}
    Auth/{MsalTokenProvider.cs, FirstPartyTokenProvider.cs, CompositeTokenProvider.cs, TokenCache.cs}
    Notifications/ExpiryNotifier.cs
    Services/{NetworkMonitor.cs, AppSettings.cs, StartupRegistration.cs}
    Assets/{Elevate.ico, tray-*.ico}
  tests/Elevate.Core.Tests/  (xUnit; one class per Swift suite; Support/{StubHttpClient,FakeTokenProvider,FakeProvider,Fixtures}.cs)
  tests/Elevate.App.Tests/   (xUnit; CompositeTokenProvider routing, AppModel logic with fakes)
  installer/Elevate.wxs  installer/Package.wxs  installer/build.ps1
  winget/manifests/r/Reothor/Elevate/<version>/{Reothor.Elevate.yaml, Reothor.Elevate.installer.yaml, Reothor.Elevate.locale.en-US.yaml}
.github/workflows/windows.yml
```

---

### Task 1: Solution scaffold and shared catalogue

**Files:** `windows/Elevate.sln`, `windows/Directory.Build.props`, `windows/global.json`, `windows/src/Elevate.Core/Elevate.Core.csproj`, `windows/tests/Elevate.Core.Tests/Elevate.Core.Tests.csproj`, `windows/tests/Elevate.Core.Tests/SmokeTests.cs`, `windows/src/Elevate.Core/Resources/EntraBuiltInRoles.json` (copied from `macos/Sources/ElevateCore/Resources/`).

- [ ] Create the solution with `dotnet new sln`, `dotnet new classlib -f net10.0 -n Elevate.Core`, `dotnet new xunit -f net10.0 -n Elevate.Core.Tests`, add project reference, add `FluentAssertions`. Embed the catalogue: `<EmbeddedResource Include="Resources\EntraBuiltInRoles.json" LogicalName="Elevate.Core.Resources.EntraBuiltInRoles.json" />`.
- [ ] `SmokeTests`: `Assembly.GetManifestResourceStream("Elevate.Core.Resources.EntraBuiltInRoles.json")` is non-null and parses to ≥130 entries.
- [ ] `dotnet test windows/Elevate.sln` green. Commit: "Scaffold the Windows solution with Elevate.Core and tests".

---

### Task 2: Core models, PimError, JSON

**Files:** `Models/*.cs`, `tests/ModelsTests.cs`.

**Interfaces (port of Swift; records with `init` setters where Swift had `var`):**
```csharp
public sealed record Identity(string Id, string Upn, string DisplayName, string HomeTenantId, SignInMethod SignInMethod = SignInMethod.OwnApp);
public readonly record struct TenantKey(string IdentityId, string TenantId);
public sealed record TenantContext(string IdentityId, string TenantId, string DisplayName, TenantSource Source,
    DiscoveryMode DiscoveryMode = DiscoveryMode.Automatic, string? PrincipalObjectId = null, string? LastDiscoveryError = null, string? AzureUnavailableReason = null)
{ public TenantKey Key => new(IdentityId, TenantId); }
public enum TenantSource { Home, Discovered, Manual }   public enum DiscoveryMode { Automatic, ManualRoles }
public enum RoleScopeKind { EntraDirectory, AzureResource, Group }   public enum GroupAccess { Member, Owner }
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EntraDirectoryScope), "entraDirectory"), JsonDerivedType(typeof(AzureResourceScope), "azureResource"), JsonDerivedType(typeof(GroupScope), "group")]
public abstract record RoleScope { public abstract RoleScopeKind Kind { get; } }
public sealed record EntraDirectoryScope(string RoleDefinitionId, string DirectoryScopeId) : RoleScope { ... }
public sealed record AzureResourceScope(string Scope, string RoleDefinitionId) : RoleScope { ... }
public sealed record GroupScope(string GroupId, GroupAccess AccessId) : RoleScope { ... }
public sealed record RoleKey(string IdentityId, string TenantId, RoleScope Scope) { public TenantKey TenantKey => new(IdentityId, TenantId); }
public sealed record RolePolicy(TimeSpan DefaultDuration, TimeSpan MaximumDuration, bool RequiresJustification, bool RequiresTicket, bool RequiresMfa, bool RequiresApproval)
{ public static readonly RolePolicy ManualDefault = new(TimeSpan.FromHours(1), TimeSpan.FromHours(8), true, false, false, false); }
public enum RoleSource { Discovered, Manual }
public sealed record EligibleRole(RoleKey Key, string DisplayName, RoleSource Source, RolePolicy Policy, string? Detail = null);
public sealed record ActiveAssignment(RoleKey RoleKey, string? AssignmentId, DateTimeOffset StartDateTime, DateTimeOffset? EndDateTime, AssignmentStatus Status, string? FailureReason = null);
public enum AssignmentStatus { Active, PendingApproval, PendingProvisioning, Failed }
public sealed record TicketInfo(string Number, string System);
public sealed record ActivationRequest(RoleKey RoleKey, TimeSpan Duration, string Justification, TicketInfo? Ticket = null);
public enum SignInMethod { OwnApp, AzureCLI, AzurePowerShell }  + static class SignInMethods { DisplayName, ClientId (null for OwnApp), UsesMsal }
public sealed class PimException : Exception { public PimErrorKind Kind; public string? Detail; public int Status; public string UserMessage; }  // kinds: ConsentRequired, InteractionRequired, ClaimsChallenge, NotEligible, PolicyViolation, PendingApproval, Network, Unexpected
```
JSON: `Elevate.Core.Json.Options` = camelCase, enums as strings (`JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`), `TimeSpan` as ISO-8601 `PT..` via a converter so `state.json` matches the Swift `Duration` encoding? **Ruling:** Swift's `Duration` encodes as `[seconds, attoseconds]` — write a `DurationJsonConverter` that reads that array form and writes it back identically, so one `state.json` round-trips between platforms.

- [ ] Tests (port `ModelsTests` + `SignInMethodTests`): RoleKey tenant distinction, RoleScope polymorphic round-trip, manual policy defaults, ActiveAssignment status round-trip, identity decodes without `signInMethod` as OwnApp, Duration array round-trip. Commit: "Port core models and JSON conventions".

---

### Task 3: Support helpers

`Iso8601Duration.Parse/Format` (PT8H, PT30M, PT1H30M, P1D), `GraphJson.ParseDate` tolerant of 1–7 fractional digits, `Countdown.Remaining/Label` (HH:MM). Port `ISO8601DurationTests`, `GraphJSONDateTests`, `CountdownTests`. Commit: "Port duration, date and countdown helpers".

---

### Task 4: HTTP, claims challenge, token provider seam, test doubles

```csharp
public sealed record HttpRequestData(string Method, Uri Url, IReadOnlyDictionary<string,string> Headers, byte[]? Body);
public sealed record HttpResponseData(int Status, IReadOnlyDictionary<string,string> Headers, byte[] Body) { string? Header(string name); string BodyText; }
public interface IHttpClient { Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken ct); }
public static class ClaimsChallenge { public static string? Parse(string wwwAuthenticate); }
public interface ITokenProvider {
  Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct);
  Task SignOutAsync(Identity identity, CancellationToken ct);
  Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct);
  Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct);   // silent; throws PimException(InteractionRequired)
  Task<string> AcquireInteractivelyAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct);
}
public static class Scopes { GraphAll, GraphUserRead, ArmAll }
public static class InteractionRetry { Task<T> RunAsync<T>(ITokenProvider, Identity, string tenantId, IReadOnlyList<string> scopes, Func<Task<T>> op) } // one interactive attempt, one retry
```
Test doubles: `StubHttpClient` (route by method + URL substring, last match wins, records requests), `FakeTokenProvider`, `Fixtures.Data(name)`. Port `ClaimsChallengeTests`. Commit: "Port HTTP seam, claims challenge and token provider contract".

---

### Task 5: Graph transport and Entra provider

Port `GraphTransport` (mapper injection, `MapError` incl. 429/Retry-After, `ActiveDurationTooShort` → "PIM requires a role to stay active for 5 minutes…", policy validation, claims), `IPimProvider` (six methods incl. `CancelPendingRequestAsync`), `EntraDirectoryProvider` (eligible/active+pending/policy/activate/deactivate/cancel; identical URLs and bodies), `GroupProvider` stub. Port `EntraDirectoryProviderTests` and the transport error tests with the copied fixtures. Commit: "Port Entra directory PIM provider".

---

### Task 6: Azure resource provider

Port `AzureResourceProvider` (tenant-wide `asTarget()` lists with `nextLink`, captions from `expandedProperties`, policy by scope, role-name resolution with OData apostrophe escaping, eligibility lookup for principal + schedule GUID, PUT SelfActivate/SelfDeactivate, cancel) and `AzureResourceProviderTests`. Commit: "Port Azure resource PIM provider".

---

### Task 7: Catalogue, manual roles, state store, discovery, coordinator

Port `RoleCatalogue` (embedded resource), `ManualRoleSource` (detail = scope for Azure; name-based dedup), `AppState`/`AppStateStore` (generation ordering, quarantine), `TenantDiscovery` (ARM tenants, openid-configuration resolution with percent-encoding, `/organization`), `ActivationCoordinator` (group by tenant, sequential within, `ActivationOutcome` with `PendingApproval(ActiveAssignment)`, cancel). Port all matching test suites. After this task `Elevate.Core.Tests` should have ≥ 80 tests. Commit: "Port catalogue, persistence, discovery and activation coordinator".

---

### Task 8: App scaffold: tray icon, flyout window, view-model skeleton

- `Elevate.App.csproj`: WinUI 3, `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, `Platforms x64;arm64`, `ApplicationIcon Assets/Elevate.ico`, packages `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools`, `CommunityToolkit.Mvvm`, `Microsoft.Windows.CsWin32` (NativeMethods.txt: `Shell_NotifyIcon`, `NOTIFYICONDATAW`, `RegisterWindowMessage`, `GetCursorPos`, `SHAppBarMessage`, `SetForegroundWindow`).
- `Program.cs`: single instance via named mutex `Reothor.Elevate`; second launch just returns.
- `TrayIcon`: hidden message window (WinUI `Window` subclassed via `WindowNative` hwnd + `SetWindowSubclass`), icon from `Assets/tray-inactive.ico` / `tray-active.ico` with a badge count drawn at runtime, left-click toggles `FlyoutWindow`, right-click context menu (Open, Settings, Quit).
- `FlyoutWindow`: `AppWindow` with `OverlappedPresenter` (no title bar, not resizable, always on top while open), `MicaBackdrop` (`MicaKind.BaseAlt`), 380×auto (max 640), positioned above the taskbar near the icon via `SHAppBarMessage` + `GetCursorPos`, hides on `Activated == Deactivated`.
- `AppModel` (view model, `ObservableObject`): the same responsibilities as the Swift `AppModel` (identities, tenants, roles, active, selection, inFlight, progress, notice, isOnline, isConfigured, refresh with per-provider isolation, activate/deactivate/cancel, policy cache, generation guard, applyClientId scoping, addAccount(method) → bool). Constructed with `ITokenProvider`, `IHttpClient`, `AppStateStore`, `IExpiryNotifier`, `INetworkMonitor`, `AppSettings`.
- `AppSettings`: `%LOCALAPPDATA%\Elevate\settings.json` with `clientId`, `runAtLogin`.
- Verify: `dotnet build`, run the app: tray icon appears, clicking opens an empty flyout with the "Elevate" header and Quit. Commit: "Add the WinUI tray app shell and app model".

---

### Task 9: Authentication providers

- `TokenCache`: MSAL cache serialisation with `MsalCacheHelper` (`Microsoft.Identity.Client.Extensions.Msal`), file `%LOCALAPPDATA%\Elevate\msal.cache`, DPAPI (`WithUnprotectedFile` never used).
- `MsalTokenProvider` (own app): `PublicClientApplicationBuilder.Create(clientId).WithAuthority(AzureCloudInstance.AzurePublic, "organizations").WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)).WithParentActivityOrWindow(() => flyoutHwnd).WithRedirectUri("http://localhost")` — WAM when available, system browser fallback; per-tenant silent via `.WithTenantId(tenantId)`; claims via `.WithClaims(claims)`; `IdentitiesAsync` from `GetAccountsAsync()`; identity id = `account.HomeAccountId.Identifier` (`oid.tid`).
- `FirstPartyTokenProvider(SignInMethod)`: same builder without broker, `WithRedirectUri("http://localhost")`, `WithUseEmbeddedWebView(false)`; sign-in scopes `https://graph.microsoft.com/.default openid profile offline_access`; silent per resource `.default`.
- `CompositeTokenProvider`: route by `identity.SignInMethod`; `IdentitiesAsync` unions all providers (MSAL caches make first-party identities enumerable here, unlike macOS).
- Error mapping: `MsalUiRequiredException` → InteractionRequired; `MsalServiceException` with `AADSTS65001/65004/90094` → ConsentRequired; `MsalClientException` user-cancel → Network("Sign-in cancelled").
- `Elevate.App.Tests`: `CompositeTokenProvider` routing with fakes; `AppModel` add-account duplicate rejection, applyClientId scoping, fail-open reconciliation, consent fallback — using the Core fakes.
- Commit: "Add MSAL-based own-app and first-party token providers".

---

### Task 10: Flyout content

`PanelView`: notice `InfoBar`, offline `InfoBar`, `SetupView` (Open Settings / Continue with the Azure CLI app), grouped `ListView` (groups = tenants; group header = `TenantHeader` with the account caption, home/manual/Azure-off badges, active count, tenant menu; `IdentityHeader` rows between accounts via a composite items source), `RoleRow` (status dot, name + detail caption, countdown `TextBlock` updated by a 1 s `DispatcherQueueTimer`, Activate/Deactivate (5-minute lock with tooltip)/Cancel, `ProgressRing` in flight, checkbox in select mode), header buttons (select mode toggle, refresh), sticky bottom "Activate N roles" in select mode, footer (Add account…, Settings…, Quit). Commit: "Add the flyout panel views".

---

### Task 11: Windows: activation, bulk, configure roles, add account, settings

Port the four macOS windows as WinUI `Window`s with Mica: `ActivationWindow` (single + bulk table with per-role duration `ComboBox` in 30-minute steps, approval label, progress column, shared reason, ticket fields when required), `ConfigureRolesWindow` (Pivot: Entra roles searchable checklist with privileged badge; Azure rows scope + role name with suggestions; Groups rows), `AddAccountWindow` (radio buttons per `SignInMethod`, own-app disabled with hint, Continue with ring), `SettingsWindow` (client id, redirect URIs shown read-only with copy, run at login toggle, Save with confirmation when own-app accounts exist). Commit: "Add activation, configuration, add-account and settings windows".

---

### Task 12: Notifications, startup, polish

`ExpiryNotifier` via `AppNotificationManager` (register at startup; toast 5 minutes before expiry with an "Extend" button → `AppNotificationManager.NotificationInvoked` → open `ActivationWindow`; body = role · tenant name); `StartupRegistration` (HKCU Run key); tray badge count; keyboard access (Esc closes flyout). Commit: "Add expiry toasts and run-at-login".

---

### Task 13: Installer, winget, CI

- `installer/Elevate.wxs` (WiX v5, `Package` scope perUser, `InstallFolder` `%LOCALAPPDATA%\Programs\Elevate`, all files from `dotnet publish -c Release -r win-x64 --self-contained false` (Windows App SDK self-contained via `WindowsAppSDKSelfContained`), Start Menu shortcut, `ProgramMenuFolder`, launch-after-install checkbox, upgrade code fixed, `MajorUpgrade`), built for x64 and arm64 by `installer/build.ps1` (`dotnet publish`, `wix build`, optional `signtool sign` with Azure Trusted Signing when secrets are present).
- `winget/manifests/r/Reothor/Elevate/<version>/*.yaml`: `PackageIdentifier: Reothor.Elevate`, `InstallerType: wix`, two installers (x64/arm64) with URLs `https://github.com/FrodeHus/elevate/releases/download/windows-v<version>/Elevate-<version>-<arch>.msi` and SHA256s, `Scope: user`, silent switches `/qn`. Validate with `winget validate`.
- `.github/workflows/windows.yml`: on push/PR touching `windows/**`: `dotnet test`; on tag `windows-v*`: publish both archs, build MSIs, sign if secrets exist, create the GitHub release with the MSIs, run `winget validate` on the manifest, and open a PR against `microsoft/winget-pkgs` with `wingetcreate` when `WINGET_TOKEN` is set.
- README (`windows/README.md`): install with `winget install Reothor.Elevate`, or download the MSI; app registration notes for WAM (`ms-appx-web://microsoft.aad.brokerplugin/{clientId}`) and system browser (`http://localhost`).
- Commit: "Add the MSI installer, winget manifest and Windows CI".
