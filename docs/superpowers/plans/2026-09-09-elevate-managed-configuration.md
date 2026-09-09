# Managed Configuration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let organizations push Elevate's client id, update-check switch, allowed sign-in methods, allowed and pinned tenants, and published profiles through Intune, Jamf, Group Policy or a file, on macOS, Windows and the CLI, with docs and an enterprise kit an administrator can roll out from.

**Architecture:** A shared `ManagedConfiguration` model in both Cores (Swift `ElevateCore`, C# `Elevate.Core`) is loaded from a platform source (forced UserDefaults keys, the policy registry keys, `/etc/elevate/managed.json`). Each app's settings object resolves managed → user → default; the models filter methods and tenants through pure `ManagedPolicy` helpers and resolve managed profiles into read-only `ActivationProfile` values at runtime. Templates live in `enterprise/`, docs in `docs/enterprise/`, both validated by a script in CI and zipped into the release.

**Tech Stack:** Swift 6.2 / Swift Testing / XcodeGen (macOS), .NET 10 / xUnit / FluentAssertions (Core, CLI, Windows), GitHub Actions, Python 3 for the kit validator.

**Spec:** `docs/superpowers/specs/2026-09-09-elevate-managed-configuration-design.md`

## Global Constraints

- Key names, exactly: `ClientId`, `DisableUpdateCheck`, `AllowedSignInMethods`, `AllowedTenants`, `PinnedTenants`, `ManagedProfiles`, `ManagedProfilesUrl`. Sign-in method values: `ownApp`, `azureCLI`, `azurePowerShell`, `custom`.
- Caption copy, exactly: "Managed by your organization"; refusals say "… not permitted by your organization" (methods, tenants) or "… published by your organization" (profiles).
- Invalid values are ignored per key with a warning; never reject a whole payload.
- Managed profiles are never written to `state.json`; `TenantContext.Source` gains no new case.
- Registry key: `SOFTWARE\Policies\Reothor\Elevate` under HKLM then HKCU, machine wins per key. Lists are subkeys with values named `1`, `2`, …; `REG_MULTI_SZ` accepted too.
- `managed.json` must be root-owned and not world-writable, else ignored with a warning.
- `ManagedProfilesUrl` must be `https`; fetched body capped at 1 MB; cache file `managed-profiles.json` next to `state.json`.
- Swift: `.swiftLanguageMode(.v6)`, strict concurrency; Swift Testing (`@Test`, `#expect`), no XCTest. Run Core tests with `cd macos && swift test`; app tests with `cd macos && xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -destination 'platform=macOS' -derivedDataPath build test -only-testing:ElevateAppTests 2>&1 | tail -30`.
- C#: run `dotnet test windows/tests/Elevate.Core.Tests/Elevate.Core.Tests.csproj` and `dotnet test cli/Elevate.Cli.sln`. `windows/Elevate.sln` does not build on macOS (WinUI); the Windows app is verified by CI on the PR.
- Commit after every task; message style like the existing log (imperative, no attribution lines).

---

### Task 1: Swift Core — managed configuration model and sources

**Files:**
- Create: `macos/Sources/ElevateCore/Managed/ManagedConfiguration.swift`
- Create: `macos/Sources/ElevateCore/Managed/ManagedSources.swift`
- Modify: `macos/Sources/ElevateCore/Models/SignInMethod.swift` (add `SignInMethodKind`, `kind`)
- Test: `macos/Tests/ElevateCoreTests/ManagedConfigurationTests.swift`

**Interfaces:**
- Produces:
  ```swift
  public enum SignInMethodKind: String, CaseIterable, Hashable, Sendable { case ownApp, azureCLI, azurePowerShell, custom }
  extension SignInMethod { public var kind: SignInMethodKind }
  public enum ManagedKey: String, CaseIterable, Hashable, Sendable { case clientId = "ClientId", disableUpdateCheck = "DisableUpdateCheck", allowedSignInMethods = "AllowedSignInMethods", allowedTenants = "AllowedTenants", pinnedTenants = "PinnedTenants", managedProfiles = "ManagedProfiles", managedProfilesUrl = "ManagedProfilesUrl" }
  public protocol ManagedConfigurationSource: Sendable {
      func string(_ key: ManagedKey) -> String?
      func bool(_ key: ManagedKey) -> Bool?
      func list(_ key: ManagedKey) -> [String]?
      var origin: String { get }
  }
  public struct DictionaryManagedSource: ManagedConfigurationSource { public init(_ values: [String: any Sendable], origin: String = "test") }
  public struct ManagedPreferences: ManagedConfigurationSource { public init(defaults: UserDefaults = .standard) }  // origin "managed preferences"
  public struct ManagedConfiguration: Hashable, Sendable {
      public var clientId: String?               // lower-cased GUID
      public var disableUpdateCheck: Bool
      public var allowedSignInMethods: Set<SignInMethodKind>?
      public var allowedTenants: [String]?
      public var pinnedTenants: [String]
      public var managedProfilesDocument: String?   // raw JSON text; parsed by Task 11
      public var managedProfilesUrl: URL?
      public var keysInEffect: [ManagedKey]
      public var warnings: [String]
      public var origin: String?
      public init()
      public static let none: ManagedConfiguration
      public var isEmpty: Bool
      public static func load(from source: any ManagedConfigurationSource) -> ManagedConfiguration
  }
  ```

- [ ] **Step 1: Write the failing tests**

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct ManagedConfigurationTests {
    @Test func emptySourceIsEmpty() {
        let c = ManagedConfiguration.load(from: DictionaryManagedSource([:]))
        #expect(c.isEmpty); #expect(c.clientId == nil); #expect(c.disableUpdateCheck == false)
        #expect(c.allowedSignInMethods == nil); #expect(c.allowedTenants == nil); #expect(c.pinnedTenants.isEmpty)
    }
    @Test func clientIdIsValidatedAndLowercased() {
        let c = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": " 11111111-2222-3333-4444-555555555555 "]))
        #expect(c.clientId == "11111111-2222-3333-4444-555555555555")
        #expect(c.keysInEffect == [.clientId])
        let bad = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": "not-a-guid"]))
        #expect(bad.clientId == nil); #expect(bad.keysInEffect.isEmpty)
        #expect(bad.warnings.contains { $0.hasPrefix("ClientId:") })
        let zero = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": "00000000-0000-0000-0000-000000000000"]))
        #expect(zero.clientId == nil)
    }
    @Test func disableUpdateCheckAcceptsBoolAndStrings() {
        #expect(ManagedConfiguration.load(from: DictionaryManagedSource(["DisableUpdateCheck": true])).disableUpdateCheck)
        #expect(ManagedConfiguration.load(from: DictionaryManagedSource(["DisableUpdateCheck": "true"])).disableUpdateCheck)
        #expect(ManagedConfiguration.load(from: DictionaryManagedSource(["DisableUpdateCheck": 1])).disableUpdateCheck)
        let off = ManagedConfiguration.load(from: DictionaryManagedSource(["DisableUpdateCheck": false]))
        #expect(!off.disableUpdateCheck); #expect(off.keysInEffect == [.disableUpdateCheck])
    }
    @Test func allowedMethodsParseCaseInsensitivelyAndDropUnknown() {
        let c = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedSignInMethods": ["OwnApp", "azurecli", "saml"]]))
        #expect(c.allowedSignInMethods == [.ownApp, .azureCLI])
        #expect(c.warnings == ["AllowedSignInMethods: unknown method 'saml' ignored"])
        let csv = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedSignInMethods": "ownApp, custom"]))
        #expect(csv.allowedSignInMethods == [.ownApp, .custom])
        let empty = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedSignInMethods": []]))
        #expect(empty.allowedSignInMethods == nil); #expect(!empty.keysInEffect.contains(.allowedSignInMethods))
        let allUnknown = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedSignInMethods": ["saml"]]))
        #expect(allUnknown.allowedSignInMethods == nil)
    }
    @Test func tenantListsKeepEntriesTrimmedAndDeduplicated() {
        let c = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedTenants": [" contoso.com ", "contoso.com", ""], "PinnedTenants": ["Fabrikam.com"]]))
        #expect(c.allowedTenants == ["contoso.com"]); #expect(c.pinnedTenants == ["fabrikam.com"])
        #expect(c.keysInEffect == [.allowedTenants, .pinnedTenants])
    }
    @Test func profilesUrlMustBeHttps() {
        let ok = ManagedConfiguration.load(from: DictionaryManagedSource(["ManagedProfilesUrl": "https://example.com/p.json"]))
        #expect(ok.managedProfilesUrl?.absoluteString == "https://example.com/p.json")
        let http = ManagedConfiguration.load(from: DictionaryManagedSource(["ManagedProfilesUrl": "http://example.com/p.json"]))
        #expect(http.managedProfilesUrl == nil); #expect(http.warnings == ["ManagedProfilesUrl: only https URLs are accepted"])
    }
    @Test func profilesDocumentIsKeptRaw() {
        let c = ManagedConfiguration.load(from: DictionaryManagedSource(["ManagedProfiles": "{\"version\":1,\"profiles\":[]}"]))
        #expect(c.managedProfilesDocument == "{\"version\":1,\"profiles\":[]}"); #expect(c.keysInEffect == [.managedProfiles])
        let blank = ManagedConfiguration.load(from: DictionaryManagedSource(["ManagedProfiles": "  "]))
        #expect(blank.managedProfilesDocument == nil)
    }
    @Test func originIsRecorded() {
        let c = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": "11111111-2222-3333-4444-555555555555"], origin: "unit"))
        #expect(c.origin == "unit")
        #expect(ManagedConfiguration.load(from: DictionaryManagedSource([:], origin: "unit")).origin == nil)
    }
    @Test func managedPreferencesReadsOnlyForcedKeys() {
        let suite = "elevate-managed-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set("11111111-2222-3333-4444-555555555555", forKey: "ClientId")
        // A value the user wrote is not forced, so the source must not return it.
        #expect(ManagedPreferences(defaults: defaults).string(.clientId) == nil)
        #expect(ManagedPreferences(defaults: defaults).origin == "managed preferences")
    }
    @Test func signInMethodKinds() {
        #expect(SignInMethod.ownApp.kind == .ownApp); #expect(SignInMethod.custom(clientId: "x").kind == .custom)
        #expect(SignInMethodKind(rawValue: "azurePowerShell") == .azurePowerShell)
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd macos && swift test --filter ManagedConfigurationTests 2>&1 | tail -5` — expected: compile errors (types missing).

- [ ] **Step 3: Implement**

`SignInMethod.swift`: add the enum and `public var kind: SignInMethodKind { switch self { case .ownApp: .ownApp; case .azureCLI: .azureCLI; case .azurePowerShell: .azurePowerShell; case .custom: .custom } }`.

`ManagedSources.swift`:

```swift
public struct DictionaryManagedSource: ManagedConfigurationSource {
    private let values: [String: any Sendable]
    public let origin: String
    public init(_ values: [String: any Sendable], origin: String = "test") { self.values = values; self.origin = origin }
    public func string(_ key: ManagedKey) -> String? {
        switch values[key.rawValue] { case let s as String: s; case let n as NSNumber: n.stringValue; default: nil }
    }
    public func bool(_ key: ManagedKey) -> Bool? { ManagedValue.bool(values[key.rawValue]) }
    public func list(_ key: ManagedKey) -> [String]? { ManagedValue.list(values[key.rawValue]) }
}

/// Shared coercions: a bool from Bool/NSNumber/"true"/"1"; a list from [String] or a comma-separated string.
enum ManagedValue {
    static func bool(_ value: Any?) -> Bool? { … }
    static func list(_ value: Any?) -> [String]? { … }
}

public struct ManagedPreferences: ManagedConfigurationSource {
    private let defaults: UserDefaults
    public let origin = "managed preferences"
    public init(defaults: UserDefaults = .standard) { self.defaults = defaults }
    private func forced(_ key: ManagedKey) -> Any? {
        guard defaults.objectIsForced(forKey: key.rawValue) else { return nil }
        return defaults.object(forKey: key.rawValue)
    }
    public func string(_ key: ManagedKey) -> String? { … }
    public func bool(_ key: ManagedKey) -> Bool? { ManagedValue.bool(forced(key)) }
    public func list(_ key: ManagedKey) -> [String]? { ManagedValue.list(forced(key)) }
}
```

`UserDefaults` is not `Sendable`; mark the struct `@unchecked Sendable` with a comment (UserDefaults is thread-safe per Apple's docs).

`ManagedConfiguration.swift`: `load(from:)` reads each key, validates as the tests require (GUID via `UUID(uuidString:)`, not the zero GUID; methods via `SignInMethodKind(rawValue:)` after matching case-insensitively against `allCases`; tenant entries trimmed, lower-cased, de-duplicated preserving order, blanks dropped; URL scheme must be `https`), appends to `keysInEffect` in `ManagedKey.allCases` order, and sets `origin = source.origin` only when something is in effect.

- [ ] **Step 4: Run the tests** — `cd macos && swift test 2>&1 | tail -5`; expected: all pass, total count grows by 10.

- [ ] **Step 5: Commit** — `git add macos && git commit -m "Core: managed configuration model, dictionary and managed-preferences sources"`

---

### Task 2: C# Core — managed configuration model and sources

**Files:**
- Create: `windows/src/Elevate.Core/Managed/ManagedKey.cs`, `ManagedConfiguration.cs`, `IManagedConfigurationSource.cs`, `DictionaryManagedSource.cs`, `JsonFileManagedSource.cs`, `RegistryManagedSource.cs`, `ManagedConfigurationSources.cs`
- Test: `windows/tests/Elevate.Core.Tests/Managed/ManagedConfigurationTests.cs`, `JsonFileManagedSourceTests.cs`, `RegistryManagedSourceTests.cs`

**Interfaces:**
- Produces (namespace `Elevate.Core.Managed`):
  ```csharp
  public enum ManagedKey { ClientId, DisableUpdateCheck, AllowedSignInMethods, AllowedTenants, PinnedTenants, ManagedProfiles, ManagedProfilesUrl }
  public static class ManagedKeys { public static string Name(this ManagedKey key); public static IReadOnlyList<ManagedKey> All { get; } }
  public interface IManagedConfigurationSource { string? String(ManagedKey key); bool? Bool(ManagedKey key); IReadOnlyList<string>? List(ManagedKey key); string Origin { get; } }
  public sealed class DictionaryManagedSource(IReadOnlyDictionary<string, object?> values, string origin = "test") : IManagedConfigurationSource
  public sealed class JsonFileManagedSource : IManagedConfigurationSource
  {   public const string DefaultPath = "/etc/elevate/managed.json";
      public JsonFileManagedSource(string path, Func<string, bool>? isTrusted = null);   // isTrusted defaults to the root-owned, not world-writable check
      public string? Warning { get; }   // set when the file exists but was ignored or failed to parse
  }
  /// Reads HKLM then HKCU; abstracted over IRegistryView so tests run on macOS.
  public interface IRegistryView { object? Value(string subKey, string name); IReadOnlyList<string>? ListValues(string subKey); }
  [SupportedOSPlatform("windows")] public sealed class WindowsRegistryView(RegistryHive hive) : IRegistryView
  public sealed class RegistryManagedSource(IRegistryView machine, IRegistryView user) : IManagedConfigurationSource   // Origin "HKLM policy" or "HKCU policy" per first hit; overall Origin "Windows policy"
  public static class ManagedConfigurationSources { public static IManagedConfigurationSource Default(); public const string RegistryPath = @"SOFTWARE\Policies\Reothor\Elevate"; }
  public sealed record ManagedConfiguration
  {   public string? ClientId { get; init; } public bool DisableUpdateCheck { get; init; }
      public IReadOnlySet<SignInMethodKind>? AllowedSignInMethods { get; init; }
      public IReadOnlyList<string>? AllowedTenants { get; init; } public IReadOnlyList<string> PinnedTenants { get; init; }
      public string? ManagedProfilesDocument { get; init; } public Uri? ManagedProfilesUrl { get; init; }
      public IReadOnlyList<ManagedKey> KeysInEffect { get; init; } public IReadOnlyList<string> Warnings { get; init; }
      public string? Origin { get; init; }
      public static ManagedConfiguration None { get; } public bool IsEmpty { get; }
      public static ManagedConfiguration Load(IManagedConfigurationSource source);
  }
  ```

- [ ] **Step 1: Write the failing tests** — port every case of Task 1's `ManagedConfigurationTests` to xUnit (`[Fact]`, FluentAssertions), plus:

```csharp
public class JsonFileManagedSourceTests
{
    [Fact] public void ReadsKeysFromJsonObject()
    {   var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{"ClientId":"11111111-2222-3333-4444-555555555555","DisableUpdateCheck":true,"AllowedTenants":["contoso.com"],"ManagedProfiles":{"version":1,"profiles":[]}}""");
        var source = new JsonFileManagedSource(path, _ => true);
        source.String(ManagedKey.ClientId).Should().Be("11111111-2222-3333-4444-555555555555");
        source.Bool(ManagedKey.DisableUpdateCheck).Should().BeTrue();
        source.List(ManagedKey.AllowedTenants).Should().Equal("contoso.com");
        source.String(ManagedKey.ManagedProfiles).Should().Be("""{"version":1,"profiles":[]}""");   // object re-serialised as text
        source.Origin.Should().Be(path);
    }
    [Fact] public void UntrustedFileIsIgnoredWithWarning() { … isTrusted: _ => false … every getter null; Warning contains "not owned by root or is writable by others" }
    [Fact] public void MissingFileIsEmptyWithoutWarning() { … }
    [Fact] public void MalformedJsonIsIgnoredWithWarning() { … }
}
public class RegistryManagedSourceTests
{
    private sealed class FakeRegistry : IRegistryView { public Dictionary<string, object?> Values = []; public Dictionary<string, List<string>> Lists = []; … }
    [Fact] public void MachineWinsPerKey() { machine ClientId A, user ClientId B and user DisableUpdateCheck 1 → String(ClientId)==A, Bool(DisableUpdateCheck)==true }
    [Fact] public void ListsComeFromSubKeyValuesInNumericOrder() { Lists[@"SOFTWARE\Policies\Reothor\Elevate\AllowedTenants"] = ["10:c","2:b","1:a"] as name:value → ["a","b","c"] }
    [Fact] public void MultiStringIsAcceptedForLists() { Values["AllowedTenants"] = new[]{"a","b"} → ["a","b"] }
    [Fact] public void DwordAndStringBooleans() { 1 → true, 0 → false, "true" → true }
    [Fact] public void MultiStringProfilesJoinLines() { Values["ManagedProfiles"] = new[]{"{","}"} → "{\n}" }
}
```

- [ ] **Step 2: Run** `dotnet test windows/tests/Elevate.Core.Tests/Elevate.Core.Tests.csproj 2>&1 | tail -5` — expected: build failure.

- [ ] **Step 3: Implement.** Validation identical to Task 1. `JsonFileManagedSource`: default `isTrusted` uses `File.GetUnixFileMode` (no `OtherWrite`) and `new FileInfo(path).GetAccessControl` is not available on Unix, so use `Mono.Unix`-free approach: `System.IO.File.GetUnixFileMode(path)` for the write bit and, for ownership, `stat` via `Environment.IsPrivilegedProcess`-independent P/Invoke is overkill — use `File.GetUnixFileMode` plus a directory check that `/etc/elevate` itself is not world-writable; document that ownership is enforced by the directory being root's. On Windows the file source is never the default. `RegistryManagedSource`: `WindowsRegistryView` wraps `RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(...)`, guarded with `OperatingSystem.IsWindows()` in `ManagedConfigurationSources.Default()`, which returns `new RegistryManagedSource(new WindowsRegistryView(RegistryHive.LocalMachine), new WindowsRegistryView(RegistryHive.CurrentUser))` on Windows and `new JsonFileManagedSource(JsonFileManagedSource.DefaultPath)` elsewhere. Add `<PackageReference Include="Microsoft.Win32.Registry" Version="5.0.0" />` only if `Microsoft.Win32.RegistryKey` is not resolvable on `net10.0` (it is part of the shared framework on net10.0; try without first).

- [ ] **Step 4: Run** both Core test projects; expected: pass.

- [ ] **Step 5: Commit** — `git commit -m "Core (C#): managed configuration model, registry and managed.json sources"`

---

### Task 3: Swift Core — diagnostics section for managed configuration

**Files:**
- Modify: `macos/Sources/ElevateCore/Support/DiagnosticsReport.swift`
- Test: `macos/Tests/ElevateCoreTests/DiagnosticsReportTests.swift`

**Interfaces:**
- Produces: `public struct DiagnosticsManaged: Sendable { public let origin: String; public let keys: [String]; public let warnings: [String]; public init(origin:keys:warnings:) }` and `DiagnosticsInput.init(..., hotKey: String?, managed: DiagnosticsManaged? = nil, errors: [DiagnosticsError])`, plus `DiagnosticsInput.managed`.

- [ ] **Step 1: Failing tests** — add to the existing suite:

```swift
@Test func rendersManagedSection() {
    let input = DiagnosticsInput(appVersion: "1", build: "1", signing: "s", os: "o", accounts: [], tenants: [], profiles: [], hotKey: nil,
                                 managed: DiagnosticsManaged(origin: "managed preferences", keys: ["ClientId", "PinnedTenants"], warnings: ["PinnedTenants: could not resolve 'nowhere.example'"]), errors: [])
    let text = DiagnosticsReport.render(input, now: Date(timeIntervalSince1970: 0))
    #expect(text.contains("Managed configuration:\n  Source: managed preferences\n  Keys: ClientId, PinnedTenants\n  Warnings:\n    PinnedTenants: could not resolve 'nowhere.example'"))
    #expect(!text.contains("11111111"))
}
@Test func rendersNoneWithoutManagedConfiguration() { … #expect(text.contains("Managed configuration:\n  None")) }
```

- [ ] **Step 2: Run** `swift test --filter DiagnosticsReportTests` — expected: compile failure.
- [ ] **Step 3: Implement** — section after "Hot key:", blank line before "Recent errors:". `Keys:` is one line; `Warnings:` only when non-empty.
- [ ] **Step 4: Run** all Core tests; pass.
- [ ] **Step 5: Commit** — `git commit -m "Core: diagnostics report lists managed configuration keys"`

---

### Task 4: C# Core — diagnostics section

**Files:**
- Modify: `windows/src/Elevate.Core/Support/DiagnosticsReport.cs`
- Test: `windows/tests/Elevate.Core.Tests/Support/DiagnosticsReportTests.cs` (existing file; add cases)

**Interfaces:** `public sealed record DiagnosticsManaged(string Origin, IReadOnlyList<string> Keys, IReadOnlyList<string> Warnings);` and `DiagnosticsInput` gains `DiagnosticsManaged? Managed = null` as the last optional parameter (keep every existing call site compiling). Output byte-identical to Task 3 (there is a golden test comparing the two ports, `DiagnosticsReportTests` — extend the golden text in both).

- [ ] Steps 1–5 as Task 3, C# flavour. Commit: `Core (C#): diagnostics report lists managed configuration keys`.

---

### Task 5: macOS app — stage 1 (client id, update check, Settings, Setup, Diagnostics)

**Files:**
- Modify: `macos/Sources/ElevateApp/App/AppSettings.swift`
- Modify: `macos/Sources/ElevateApp/App/AppModel.swift` (`applyClientId`), `AppModel+Operations.swift` (`checkForUpdates`, `diagnosticsText`)
- Modify: `macos/Sources/ElevateApp/Views/SettingsView.swift`
- Create: `macos/Sources/ElevateApp/Views/ManagedSection.swift`
- Modify: `macos/Tests/ElevateAppTests/Support/TestModel.swift` (`makeSettings(managed:)`)
- Test: `macos/Tests/ElevateAppTests/AppModelManagedTests.swift`

**Interfaces:**
- Consumes: Task 1, Task 3.
- Produces:
  ```swift
  // AppSettings
  init(defaults: UserDefaults = .standard, managed: ManagedConfiguration? = nil)   // nil → ManagedConfiguration.load(from: ManagedPreferences(defaults: .standard))
  let managed: ManagedConfiguration
  var storedClientId: String            // the user's value, persisted under clientIdKey (didSet as before)
  var clientId: String { get set }      // managed.clientId ?? storedClientId; set ignored when isClientIdManaged
  var isClientIdManaged: Bool
  var updateCheckDisabled: Bool
  // AppModel
  var managed: ManagedConfiguration { settings.managed }
  ```
  `makeSettings(managed: ManagedConfiguration = .none) -> AppSettings` and `makeModel(..., managed: ManagedConfiguration = .none, ...)` in TestModel.

- [ ] **Step 1: Failing tests**

```swift
@Suite @MainActor struct AppModelManagedTests {
    static let id = "11111111-2222-3333-4444-555555555555"
    @Test func managedClientIdConfiguresTheApp() async {
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": Self.id]))
        let model = await makeModel(managed: managed, ownAppViaLoopback: true)
        defer { cleanup(model) }
        #expect(model.settings.isClientIdManaged); #expect(model.settings.clientId == Self.id); #expect(model.isConfigured)
        model.settings.clientId = "22222222-2222-3333-4444-555555555555"
        #expect(model.settings.clientId == Self.id)
        #expect(throws: PIMError.self) { try model.applyClientId("22222222-2222-3333-4444-555555555555") }
    }
    @Test func userClientIdStillWorksWithoutManagement() async { … settings.clientId = id → storedClientId == id, isConfigured }
    @Test func disabledUpdateCheckNeverCallsGitHub() async {
        let http = StubHTTPClient()   // see AppModelUpdatesTests for how it records requests
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["DisableUpdateCheck": true]))
        let model = await makeModel(http: http, online: true, managed: managed)
        defer { cleanup(model) }
        await model.checkForUpdates(force: true)
        #expect(http.requests.isEmpty); #expect(model.updateAvailable == nil); #expect(model.updateCheckMessage == nil)
    }
    @Test func diagnosticsListsManagedKeys() async {
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": Self.id, "DisableUpdateCheck": true], origin: "unit"))
        let model = await makeModel(managed: managed)
        defer { cleanup(model) }
        let text = model.diagnosticsText()
        #expect(text.contains("Source: unit")); #expect(text.contains("Keys: ClientId, DisableUpdateCheck")); #expect(!text.contains(Self.id))
    }
}
```

Read `AppModelUpdatesTests.swift` first for the StubHTTPClient API and copy its setup.

- [ ] **Step 2: Run** the app tests (command in Global Constraints) — expected: compile failure.

- [ ] **Step 3: Implement.** `AppSettings`: rename the stored property to `storedClientId` (its `didSet` and the legacy migration stay); add the computed `clientId`, `isClientIdManaged`, `updateCheckDisabled`, `managed`. `applyClientId`: first line `guard !settings.isClientIdManaged else { throw PIMError.unexpected(status: 0, body: "The client ID is managed by your organization") }`. `checkForUpdates`: `guard !settings.updateCheckDisabled else { return }` before the throttle. `diagnosticsText`: `managed: settings.managed.isEmpty ? nil : DiagnosticsManaged(origin: settings.managed.origin ?? "unknown", keys: settings.managed.keysInEffect.map(\.rawValue), warnings: settings.managed.warnings)`.

`SettingsView`: in "Entra app registration", when `model.settings.isClientIdManaged` show `TextField` with `.disabled(true)` bound to a constant of the managed value and, instead of the hint captions, `Label("Managed by your organization", systemImage: "building.2").font(.caption).foregroundStyle(.secondary)`; skip the `onSubmit`/focus save. In "General" → Updates: when `updateCheckDisabled`, replace the button with `Text("Updates are managed by your organization").font(.caption).foregroundStyle(.secondary)`. Append `ManagedSection()` after the shortcut section.

`ManagedSection.swift`:

```swift
struct ManagedSection: View {
    @Environment(AppModel.self) private var model
    var body: some View {
        let managed = model.managed
        if !managed.isEmpty {
            Section {
                if let id = managed.clientId { LabeledContent("Client ID") { Text(id).font(.caption.monospaced()).textSelection(.enabled) } }
                if managed.disableUpdateCheck { LabeledContent("Update check") { Text("Disabled") } }
                ManagedListRows()        // filled in by Task 9 (methods, tenants) and Task 13 (profiles); empty view for now
                ForEach(managed.warnings, id: \.self) { Text($0).font(.caption).foregroundStyle(.orange) }
                if let origin = managed.origin { Text("Source: \(origin)").font(.caption2).foregroundStyle(.secondary) }
            } header: { Label("Managed by your organization", systemImage: "building.2") }
        }
    }
}
struct ManagedListRows: View { @Environment(AppModel.self) private var model; var body: some View { EmptyView() } }
```

`SetupView`: no change needed (gated on `isConfigured`), but verify by reading `PanelView.swift:99`.

- [ ] **Step 4: Run** app tests and Core tests; expected: pass. Build the app: `cd macos && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build build 2>&1 | tail -3`.

- [ ] **Step 5: Commit** — `git commit -m "macOS: managed client id and update check, Settings captions, diagnostics"`

---

### Task 6: CLI — stage 1 (`CliSettings`, `config` sources, `config managed`)

**Files:**
- Modify: `cli/src/Elevate.Cli/Infrastructure/CliSettings.cs`, `cli/src/Elevate.Cli/Commands/ConfigCommands.cs`, `cli/src/Elevate.Cli/Commands/CommandContext.cs` (construct settings with the default source), the update mention after `status` (find with `grep -rn LatestKnownVersion cli/src`)
- Test: `cli/tests/Elevate.Cli.Tests/ManagedConfigTests.cs`; extend `Support/TestSession.cs` with a `ManagedConfiguration` parameter (read it first: it builds `CliSettings`/`ElevateSession` for tests)

**Interfaces:**
- Consumes: Task 2, Task 4.
- Produces:
  ```csharp
  public CliSettings(string directory, ManagedConfiguration? managed = null)   // null → Load(ManagedConfigurationSources.Default())
  public ManagedConfiguration Managed { get; }
  public bool IsClientIdManaged { get; }
  public string ClientId { get; set; }          // getter resolves; setter throws InvalidOperationException("client-id is managed by your organization.") when managed
  public bool UpdateCheckDisabled { get; }
  public enum SettingSource { Managed, User, Default }
  public SettingSource ClientIdSource { get; }
  ```
  Commands: `elevate config` table gains a third column "Source"; `--json` adds `sources`; `config get` writes the stderr note; new `config managed [--file <path>]` (`--json` → `{ origin, keys, warnings }`).

- [ ] **Step 1: Failing tests**

```csharp
public class ManagedConfigTests
{
    private static ManagedConfiguration Managed(params (string, object?)[] pairs) =>
        ManagedConfiguration.Load(new DictionaryManagedSource(pairs.ToDictionary(p => p.Item1, p => p.Item2), "unit"));

    [Fact] public void ManagedClientIdWinsAndCannotBeSet()
    {   using var dir = new TempDirectory();
        var settings = new CliSettings(dir.Path, Managed(("ClientId", "11111111-2222-3333-4444-555555555555")));
        settings.ClientId.Should().Be("11111111-2222-3333-4444-555555555555");
        settings.IsClientIdManaged.Should().BeTrue(); settings.ClientIdSource.Should().Be(SettingSource.Managed); settings.IsConfigured.Should().BeTrue();
        var act = () => settings.ClientId = "22222222-2222-3333-4444-555555555555";
        act.Should().Throw<InvalidOperationException>();
    }
    [Fact] public async Task ConfigSetClientIdIsRefusedWhenManaged()   // run the command tree through TestSession; exit code ExitCodes.Usage; stderr contains "managed by your organization"
    [Fact] public async Task ConfigTableShowsSources()                 // "client-id" row has "managed", "custom-client-id" has "default"; --json sources.clientId == "managed"
    [Fact] public async Task ConfigManagedListsKeysAndWarnings()       // `config managed --json` → keys ["ClientId"], origin "unit"
    [Fact] public async Task ConfigManagedFileDryRun()                 // write a managed.json to temp, run `config managed --file <path>`; keys from that file, origin == path
    [Fact] public void UpdateCheckDisabledSkipsTheMention()            // whatever helper decides to print the update hint returns false when Managed.DisableUpdateCheck
}
```

Look at `SettingsAndDirectoryTests.cs` and `CommandTreeTests.cs` for how existing tests invoke commands and capture output; reuse those helpers.

- [ ] **Step 2: Run** `dotnet test cli/Elevate.Cli.sln 2>&1 | tail -5` — compile failure.
- [ ] **Step 3: Implement** as specified. `config managed --file` constructs `new JsonFileManagedSource(path, _ => true)` (a dry run does not check ownership, and says so in a stderr note).
- [ ] **Step 4: Run** CLI tests; pass. Also `dotnet run --project cli/src/Elevate.Cli -- config managed --file /nonexistent` prints "No managed configuration" and exits 0.
- [ ] **Step 5: Commit** — `git commit -m "CLI: managed client id and update check, config sources, config managed"`

---

### Task 7: Swift Core — policy helpers and tenant resolver

**Files:**
- Create: `macos/Sources/ElevateCore/Managed/ManagedPolicy.swift`, `macos/Sources/ElevateCore/Managed/ManagedTenantResolver.swift`
- Test: `macos/Tests/ElevateCoreTests/ManagedPolicyTests.swift`, `ManagedTenantResolverTests.swift`

**Interfaces:**
```swift
public enum ManagedPolicy {
    public static func isAllowed(_ method: SignInMethod, by config: ManagedConfiguration) -> Bool
    public static func isTenantAllowed(_ tenantId: String, allowedIds: Set<String>?) -> Bool   // case-insensitive; nil → true
}
public struct ManagedTenantResolution: Sendable, Hashable { public var ids: [String: String]; public var unresolved: [String] }  // entry (as given) → lower-cased id
public actor ManagedTenantResolver {
    public init(http: any HTTPClient)
    public func resolve(_ entries: [String]) async -> ManagedTenantResolution    // GUIDs pass through; domains via login.microsoftonline.com/<domain>/v2.0/.well-known/openid-configuration; cached per entry
}
```

- [ ] **Step 1: Failing tests** — `isAllowed`: nil set allows everything; `[.ownApp]` refuses `.azureCLI` and `.custom(clientId:)`; `[.custom]` allows any custom id. `isTenantAllowed`: nil → true; case-insensitive match; miss → false. Resolver: GUID passes without a request; a domain hits the OIDC URL once even when asked twice (StubHTTPClient counts requests; see `TenantDiscoveryTests.swift` for the stub's issuer JSON); a 404 lands in `unresolved`; mixed input keeps order in `ids` keys.
- [ ] **Step 2–5:** run (fail), implement (reuse the issuer regex from `TenantDiscovery.resolveTenantId`; do not duplicate the HTTP call — extract a `static func tenantId(fromIssuerAt:http:)` helper in `TenantDiscovery` and call it from both), run (pass), commit `Core: managed policy helpers and tenant resolver`.

---

### Task 8: C# Core — policy helpers and tenant resolver

**Files:** `windows/src/Elevate.Core/Managed/ManagedPolicy.cs`, `ManagedTenantResolver.cs`; tests `windows/tests/Elevate.Core.Tests/Managed/ManagedPolicyTests.cs`, `ManagedTenantResolverTests.cs`.

**Interfaces:** `public static class ManagedPolicy { public static bool IsAllowed(SignInMethod method, ManagedConfiguration config); public static bool IsTenantAllowed(string tenantId, IReadOnlySet<string>? allowedIds); }`, `public sealed record ManagedTenantResolution(IReadOnlyDictionary<string, string> Ids, IReadOnlyList<string> Unresolved);`, `public sealed class ManagedTenantResolver(IHttpClient http) { public Task<ManagedTenantResolution> ResolveAsync(IEnumerable<string> entries, CancellationToken ct = default); }` sharing the issuer lookup with `TenantDiscovery.ResolveTenantIdAsync` (extract a static helper).

- [ ] Steps as Task 7 (port the same cases; the stub HTTP client is `FakeHttpClient` in the Core tests). Commit `Core (C#): managed policy helpers and tenant resolver`.

---

### Task 9: macOS app — stage 2 (sign-in methods, allowed and pinned tenants, Settings rows)

**Files:**
- Modify: `macos/Sources/ElevateApp/App/AppModel.swift` (properties, bootstrap), `AppModel+Accounts.swift` (`availableMethods`, `isAvailable`, `addAccount` notices, `addTenant`, `trackTenants`, `removeTenant`, pinned tracking), `Views/AddAccountView.swift`, `Views/IdentitySection.swift` (caption + disabled Sign in), `Views/TenantSheets.swift` (`DiscoverTenantsView` greyed rows), `Views/TenantSection.swift` or wherever "Remove tenant" lives (`grep -rn "Remove tenant" macos/Sources`), `Views/ManagedSection.swift` (`ManagedListRows`)
- Test: `macos/Tests/ElevateAppTests/AppModelManagedTests.swift` (extend)

**Interfaces:**
- Consumes: Tasks 1, 5, 7.
- Produces on `AppModel`:
  ```swift
  private(set) var allowedTenantIds: Set<String>?      // nil = unrestricted or not yet resolved
  private(set) var pinnedTenantIds: [String]
  private(set) var managedTenantWarnings: [String]     // unresolved entries
  func isPinnedTenant(_ key: TenantKey) -> Bool
  func isMethodAllowed(_ method: SignInMethod) -> Bool
  func resolveManagedTenants() async                  // runs ManagedTenantResolver over allowedTenants + pinnedTenants, fills the three properties, then applies: removes disallowed non-home tenants, tracks pinned tenants for every identity
  func trackPinnedTenants(identityId: String) async   // called from addAccount after the home tenant, and by resolveManagedTenants
  ```
  The model owns a `ManagedTenantResolver` built from `http`; `bootstrap()` awaits `resolveManagedTenants()` before `refreshAll()`.

- [ ] **Step 1: Failing tests** (use `Sample` from TestModel; the StubHTTPClient answers the OIDC URL for `fabrikam.com` with issuer `https://login.microsoftonline.com/aaaaaaaa-0000-0000-0000-000000000002/v2.0`; Graph `/organization` for a display name, else the fallback name is the entry):

```swift
@Test func disallowedMethodsAreHiddenAndRefused() async {
    let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedSignInMethods": ["ownApp"]]))
    let model = await makeModel(managed: managed)
    defer { cleanup(model) }
    #expect(model.availableMethods == [.ownApp])
    #expect(!model.isAvailable(.azureCLI)); #expect(!model.isAvailable(.custom(clientId: Self.id)))
    #expect(await model.addAccount(method: .azureCLI) == false)
    #expect(model.notice == "That sign-in method is not permitted by your organization")
}
@Test func accountWithDisallowedMethodStaysButCannotRetry() async { state with Sample.identity(method: .azureCLI), signInNeeded → retrySignIn returns false with the notice; identity still in model.identities }
@Test func allowedTenantsFilterDiscoveryAndAdds() async {
    // AllowedTenants: [Sample.tenantId, "fabrikam.com"]; state has home tenant + a tracked "other-tenant" (source .discovered)
    // after makeModel: "other-tenant" removed, home kept; allowedTenantIds == [Sample.tenantId, "aaaaaaaa-…02"]
    // trackTenants(identityId:, tenants: [DiscoveredTenant(tenantId: "zzz"…), DiscoveredTenant(tenantId: "aaaaaaaa-…02"…)]) tracks only the second
    // addTenant(domainOrId: "zzz") throws with message containing "not permitted by your organization"
}
@Test func pinnedTenantsAreTrackedAtBootstrapAndAfterSignIn() async {
    // PinnedTenants: ["fabrikam.com"]; state has one identity with only its home tenant
    // after makeModel: model.tenants(for:) contains "aaaaaaaa-…02" with source .discovered; isPinnedTenant true
    // removeTenant(that key) leaves it in place and sets notice "This tenant is pinned by your organization"
    // a second identity added through FakeTokenProvider.signIn gets the pinned tenant too
}
@Test func unresolvedManagedTenantIsAWarningNotABlock() async { AllowedTenants: ["nowhere.example"] (stub 404) → allowedTenantIds == nil, managedTenantWarnings == ["AllowedTenants: could not resolve 'nowhere.example'"], trackTenants still tracks anything }
```

- [ ] **Step 2: Run** app tests — failures.
- [ ] **Step 3: Implement.** Notices and errors use the exact copy above. `DiscoverTenantsView`: a disallowed row (`model.allowedTenantIds` non-nil and not containing the id) is `.disabled(true)` with the caption "Not permitted by your organization" in place of the domain. Account row caption: "Sign-in method no longer permitted by your organization". Tenant menu: replace "Remove tenant" with a disabled `Text("Pinned by your organization")` when pinned. `ManagedListRows`: rows "Allowed sign-in methods" (display names joined by ", "), "Allowed tenants", "Pinned tenants" (entries as given, with " → id" appended when the resolved id differs), and `managedTenantWarnings` in orange.
- [ ] **Step 4: Run** app + Core tests; build the app.
- [ ] **Step 5: Commit** — `git commit -m "macOS: managed sign-in methods, allowed and pinned tenants"`

---

### Task 10: CLI — stage 2

**Files:**
- Modify: `cli/src/Elevate.Cli/Commands/AccountCommands.cs` (`ParseMethod`, default method, accounts Flags), `cli/src/Elevate.Cli/Session/ElevateSession.Accounts.cs` (`AddTenantAsync`, `TrackTenants`, `RemoveTenant`, pinned tracking), `ElevateSession.cs` (`Load` cleanup; `ResolveManagedTenantsAsync`), `Commands/TenantCommands.cs` (Flags: `pinned`, `not permitted`; `remove` refusal), `Commands/CommandContext.cs` (await the resolver once per invocation before commands that touch tenants)
- Test: `cli/tests/Elevate.Cli.Tests/ManagedPolicyCommandTests.cs`

**Interfaces:** `ElevateSession.AllowedTenantIds : IReadOnlySet<string>?`, `PinnedTenantIds : IReadOnlyList<string>`, `ManagedTenantWarnings : IReadOnlyList<string>`, `IsPinnedTenant(TenantKey)`, `IsMethodAllowed(SignInMethod)`, `Task ResolveManagedTenantsAsync(CancellationToken)`, `Task TrackPinnedTenantsAsync(Identity, CancellationToken)`. `ParseMethod(string method, string? clientId, CliSettings settings)` throws `CliException("The sign-in method 'cli' is not permitted by your organization. Allowed: own.", ExitCodes.Usage)`; `login` without `--method` defaults to the first allowed of own, cli, pwsh.

- [ ] **Step 1: Failing tests** — mirror Task 9's five cases through the session and the command tree (`login --method cli` exit code and message; `tenants remove` on a pinned tenant → message "pinned by your organization", exit `ExitCodes.Usage`; `tenants` table Flags contain `pinned`; `accounts` Flags contain `not permitted`).
- [ ] **Steps 2–5:** run, implement, run, commit `CLI: managed sign-in methods, allowed and pinned tenants`.

---

### Task 11: Swift Core — managed profiles (format, resolver, fetcher)

**Files:**
- Modify: `macos/Sources/ElevateCore/Models/ActivationProfile.swift` (`ProfileSource`, `source`)
- Create: `macos/Sources/ElevateCore/Managed/ManagedProfileSet.swift`, `ManagedProfileResolver.swift`, `ManagedProfileFetcher.swift`
- Test: `macos/Tests/ElevateCoreTests/ManagedProfileSetTests.swift`, `ManagedProfileResolverTests.swift`, `ManagedProfileFetcherTests.swift`; extend `ActivationProfileTests` (or `AppStateTests`) for `source` round-trip

**Interfaces:**
```swift
public enum ProfileSource: String, Codable, Hashable, Sendable { case user, managed }
// ActivationProfile: `public var source: ProfileSource` (init default .user); decoded with decodeIfPresent ?? .user; encoded only when .managed
public struct ManagedProfileSet: Hashable, Sendable {
    public struct Role: Hashable, Sendable {
        public var kind: RoleScopeKind; public var tenant: String; public var role: String?; public var scope: String?
        public var directoryScope: String; public var group: String?; public var access: GroupAccess; public var duration: Duration?
    }
    public struct Profile: Hashable, Sendable, Identifiable { public var id: String /* slug */; public var name: String; public var reason: String?; public var pinned: Bool; public var roles: [Role]; public var profileId: UUID /* v5 */ }
    public var profiles: [Profile]
    public static let empty: ManagedProfileSet
    public static func parse(_ data: Data) throws -> ManagedProfileSet      // throws ManagedProfileError.invalid(String) with the profile id and field
    public static func parse(_ text: String) throws -> ManagedProfileSet
    public func merged(with other: ManagedProfileSet) -> ManagedProfileSet   // other wins by id
    public static func profileId(slug: String) -> UUID                     // UUID v5 of "managed-profile:<slug>" in the DNS namespace
}
public enum ManagedProfileError: Error, Hashable, Sendable { case invalid(String) }
public struct ManagedProfileResolution: Hashable, Sendable { public var profiles: [ActivationProfile]; public var warnings: [String] }
public enum ManagedProfileResolver {
    public static func resolve(_ set: ManagedProfileSet, tenantIds: [String: String], tenants: [TenantContext], roles: [RoleKey: EligibleRole]) -> ManagedProfileResolution
}
public struct ManagedProfileFetcher: Sendable {
    public init(http: any HTTPClient, cacheURL: URL)
    public static let maxBytes = 1_048_576
    public func cached() -> ManagedProfileSet?                                 // nil when no cache or unparsable
    public func fetch(from url: URL) async throws -> ManagedProfileSet         // GET with Accept: application/json; throws on non-200, size, parse; writes the cache on success
}
```

- [ ] **Step 1: Failing tests**
  - Parse: the spec's example parses (three roles, durations `PT2H`/`nil`/`PT4H`, access member, directoryScope "/"); `version: 2` → `invalid("version 2 is not supported")`; missing `name` → `invalid("profile 'prod-incident': name is required")`; bad slug `Prod Incident` → invalid; duplicate ids → invalid; unknown kind → `invalid("profile 'x' role 1: unknown kind 'foo'")`; azure without scope → invalid; group without group → invalid; `profileId(slug: "prod-incident")` equals the UUID v5 computed by the test with its own SHA-1 (assert a fixed literal you compute once with `uuidgen`-free Python: `uuid.uuid5(uuid.NAMESPACE_DNS, "managed-profile:prod-incident")`); `merged` replaces by id and appends new ones in order.
  - Resolver: with two identities in tenant T (ids `id-1`, `id-2`), roles `Contributor` on scope `/subscriptions/S` for `id-1` only, Entra `Security Reader` (template id `5d6b6bb7-…`) for both, a group "SRE on-call" for `id-2`: the resolved profile has entries `[id-1 azure, id-1 entra, id-2 entra, id-2 group]` (ordered by identity then kind), `lastDuration` from the spec, `lastJustification == reason`, `pinned` from the spec, `source == .managed`, `id == profileId(slug)`. Name match is case-insensitive; matching by role definition GUID (tail of the ARM id) and by group object id works; scope compared case-insensitively. Tenant tracked but no matching role → one placeholder entry per identity whose `RoleKey` is `RoleKey(identityId:, tenantId:, scope: .azureResource(scope: spec.scope, roleDefinitionId: spec.role))` (or `.entraDirectory(roleDefinitionId: spec.role, directoryScopeId: spec.directoryScope)`, `.group(groupId: spec.group, accessId: spec.access)`). Tenant tracked by nobody → role dropped, warning "Prod incident: no account in tenant fabrikam.com". Domain not in `tenantIds` → warning "Prod incident: could not resolve tenant 'x'".
  - Fetcher: 200 with the example → parsed and cached (file exists, `cached()` returns it); 500 → throws, cache untouched; body over `maxBytes` → throws; `http://` never reaches here (validated in Task 1) but the fetcher also refuses non-https with `invalid`.
- [ ] **Steps 2–5:** run, implement (UUID v5: SHA-1 over namespace bytes + name via `CryptoKit.Insecure.SHA1`, set version/variant bits), run, commit `Core: managed profile set, resolver and fetcher`.

---

### Task 12: C# Core — managed profiles

**Files:** `windows/src/Elevate.Core/Models/ActivationProfile.cs` (`ProfileSource Source`, ignored when `User` on write), `windows/src/Elevate.Core/Managed/ManagedProfileSet.cs`, `ManagedProfileResolver.cs`, `ManagedProfileFetcher.cs`; tests mirroring Task 11 under `windows/tests/Elevate.Core.Tests/Managed/`. Golden file `windows/tests/Elevate.Core.Tests/Fixtures/state-macos.json` must still round-trip (a user profile writes no `source`).

**Interfaces:** same shapes as Task 11 in C# (`ManagedProfileSet.Parse(string)`, `ProfileId(string slug)`, `Merged(ManagedProfileSet other)`, `ManagedProfileResolver.Resolve(set, tenantIds, tenants, roles)`, `ManagedProfileFetcher(IHttpClient http, string cachePath)` with `Cached()` and `FetchAsync(Uri, CancellationToken)`). The UUID v5 literal in the test must equal the Swift one.

- [ ] Steps 1–5; commit `Core (C#): managed profile set, resolver and fetcher`.

---

### Task 13: macOS app — stage 3 (managed profiles in the model, Manage window, Settings)

**Files:**
- Create: `macos/Sources/ElevateApp/App/AppModel+ManagedProfiles.swift`
- Modify: `AppModel+Profiles.swift` (`profiles`, `profile(id:)`, `pinnedProfiles`, guards on mutators, `plan(for:)`, `runProfile`), `AppModel.swift` (fetch schedule in `bootstrap` and the daily timer next to the access-package tick; see `AppModel+AccessPackages.swift` for the 8 h tick pattern), `Views/ManageProfilesView.swift`, `Views/ManagedSection.swift` (`ManagedListRows` profile rows), `AppModel+Operations.swift` (diagnostics profile names with `(managed)`)
- Test: `macos/Tests/ElevateAppTests/AppModelManagedProfilesTests.swift`

**Interfaces:**
```swift
// AppModel+ManagedProfiles
var managedProfileSet: ManagedProfileSet        // inline (parsed from settings.managed.managedProfilesDocument, parse errors → managedProfileWarnings) merged with fetchedProfileSet
private(set) var fetchedProfileSet: ManagedProfileSet?
private(set) var managedProfilesFetchedAt: Date?
private(set) var managedProfileWarnings: [String]
var managedProfiles: [ActivationProfile]        // ManagedProfileResolver.resolve(managedProfileSet, tenantIds: managedTenantIds, tenants: state.tenants, roles: allRolesByKey).profiles
func isManagedProfile(_ id: UUID) -> Bool
func refreshManagedProfiles(force: Bool = false) async   // fetch when url set and (force or fetchedAt older than 24 h); on failure keep cached, append warning
```
`managedTenantIds` is the `[entry: id]` map from Task 9's resolver, extended to include the tenants named by profile roles (resolve them in `resolveManagedTenants` too).

- [ ] **Step 1: Failing tests** — managed set inline via `ManagedProfiles` document: `model.profiles` lists the user profile then the managed one with `source == .managed`; `profile(id:)` finds it; `pinnedProfiles` starts with the pinned managed one and does not count it against the limit; `deleteProfile`, `renameProfile`, `setPinned`, `addProfileEntries` leave it unchanged and `state.profiles` untouched; `plan(for:)` marks the placeholder entry `.notEligible` once its tenant is loaded; `runProfile` activates the eligible entry (FakeTokenProvider/StubHTTPClient as in `AppModelProfileTests`) and leaves `state.profiles` untouched; `setHotKeyProfile(managedId)` is accepted. URL: stub 200 → `fetchedProfileSet` set, cache file written under the model's directory, merged by id with inline; second `refreshManagedProfiles()` within 24 h makes no request; stub 500 on a fresh model with an existing cache file → cached set used, warning appended. Diagnostics shows `Prod incident (managed)`.
- [ ] **Step 2: Run** — failures.
- [ ] **Step 3: Implement.** `ManageProfilesView`: row marker `Image(systemName: "building.2")` for managed; `ProfileEditor` for a managed profile shows `Text(profile.name).font(.headline)` instead of the field, the caption "Published by your organization", the Run… button and the shortcut toggle only; no pin toggle, no Add roles…, no minus buttons, no Delete…; the list's `onMove` ignores managed rows. `ManagedListRows`: "Managed profiles: N (inline)" and "Managed profiles URL: <url> · fetched <RelativeDateTimeFormatter>" rows plus `managedProfileWarnings`.
- [ ] **Step 4: Run** all tests; build; relaunch the app (`open macos/build/Build/Products/Debug/Elevate.app`) so the user can look.
- [ ] **Step 5: Commit** — `git commit -m "macOS: organization-published profiles"`

---

### Task 14: CLI — stage 3 and `profiles export`

**Files:**
- Modify: `cli/src/Elevate.Cli/Session/ElevateSession.Profiles.cs` (managed profiles in `Profiles`, `FindProfile`, guards), `ElevateSession.cs` (fetch + cache in `Load`/a `RefreshManagedProfilesAsync`), `Commands/ProfileCommands.cs` (Source column, `show` source, refusals, `export`)
- Test: `cli/tests/Elevate.Cli.Tests/ManagedProfilesCommandTests.cs`

**Interfaces:** `ElevateSession.ManagedProfiles : IReadOnlyList<ActivationProfile>`, `Profiles` = user + managed, `IsManagedProfile(Guid)`, `Task RefreshManagedProfilesAsync(bool force, CancellationToken)`, `ManagedProfileWarnings`. `profiles export <name-or-id>` prints the §7.1 JSON of a user profile (indented; roles named from loaded roles — when a role is not loaded, fall back to the ids from the key; tenant as id) to stdout; `--json` is implied.

- [ ] **Step 1: Failing tests** — `profiles list` shows `managed` in the Source column and `user` for the other; `profiles show <managed>` prints "Source: managed"; `profiles rename|delete <managed>` → exit `Usage`, message "'Prod incident' is published by your organization and cannot be changed."; `profiles save "Prod incident"` (same name as a managed one) refused the same way; `profiles run <managed> --dry-run` plans; `profiles export <user>` output parses with `ManagedProfileSet.Parse` and contains the role names.
- [ ] **Steps 2–5:** run, implement, run, commit `CLI: managed profiles, profiles export`.

---

### Task 15: Windows app — all three stages (verified by CI)

**Files:**
- Modify: `windows/src/Elevate.App.Model/Services/AppSettings.cs` (constructor `managed`, `Managed`, `IsClientIdManaged`, resolved `ClientId`, `UpdateCheckDisabled`), `ViewModels/AppModel.cs` (`ApplyClientId` refusal, bootstrap: resolve managed tenants, managed profile refresh and daily tick), `AppModel.Accounts.cs` (`AvailableMethods`, `IsAvailable`, notices, `AddTenantAsync`, `TrackTenantsAsync`, `RemoveTenant`, pinned tracking), `AppModel.Operations.cs` (`CheckForUpdatesAsync` gate, `DiagnosticsText` managed section and `(managed)` suffix), `AppModel.Profiles.cs` (managed profiles merged, guards), `AppModel.ManagedProfiles.cs` (new)
- Modify: `windows/src/Elevate.App/Views/SettingsWindow.xaml(.cs)` (disabled client id + caption, update caption, "Managed by your organization" group), `AddAccountWindow.xaml(.cs)` (hide disallowed radios and the custom row), `PanelView.xaml.cs` / `PanelItems.cs` (account caption, tenant "Pinned by your organization" menu item), `TenantWindows.xaml.cs` (greyed disallowed rows), `ManageProfilesWindow.xaml(.cs)` (managed marker, read-only editor)
- Test: `windows/tests/Elevate.App.Tests/` — add `ManagedSettingsTests.cs` for `AppSettings` resolution and `AppModel` stage-1/2/3 behaviour mirroring Task 5/9/13 (this project runs only on Windows CI)

**Interfaces:** the same member names as the macOS model in C# casing (`AllowedTenantIds`, `PinnedTenantIds`, `IsPinnedTenant`, `IsMethodAllowed`, `ResolveManagedTenantsAsync`, `TrackPinnedTenantsAsync`, `ManagedProfiles`, `IsManagedProfile`, `RefreshManagedProfilesAsync`, `ManagedProfileWarnings`).

- [ ] **Step 1:** write the tests first (they cannot run locally; keep them small and obviously correct).
- [ ] **Step 2:** implement, reading each existing file fully first; copy strings exactly from Tasks 5, 9, 13.
- [ ] **Step 3:** `dotnet build windows/src/Elevate.Core/Elevate.Core.csproj` still passes locally (Core untouched here); commit `Windows: managed configuration (client id, update check, methods, tenants, profiles)`; push the branch and open a draft PR so the Windows CI job compiles it: `gh pr create --draft --title "Managed configuration through Intune and Jamf" --body "Closes #101, closes #102, closes #103, closes #104. Draft while CI validates the Windows build."`. Watch `gh run list --branch managed-configuration` and fix compile errors until the Windows job is green (repeat build/push).

---

### Task 16: Enterprise kit, validator and CI workflow

**Files:**
- Create: `enterprise/README.md`, `enterprise/windows/Elevate.admx`, `enterprise/windows/en-US/Elevate.adml`, `enterprise/windows/example.reg`, `enterprise/macos/no.reothor.elevate.mobileconfig`, `enterprise/macos/intune-preference-file.plist`, `enterprise/macos/jamf-manifest.json`, `enterprise/cli/managed.json`, `enterprise/example/{README.md,no.reothor.elevate.mobileconfig,managed.json,profiles.json,example.reg}`
- Create: `docs/enterprise/keys.md` (the validator reads it; write it in this task, the other docs in Task 18)
- Create: `scripts/validate-enterprise-kit.py`, `.github/workflows/enterprise-kit.yml`
- Test: the validator run against the kit (and a deliberately broken copy in `/tmp` to see it fail)

**Interfaces:** `docs/enterprise/keys.md` has one table whose rows are `| \`Key\` | type | allowed values | platforms | stage | example |` — the validator parses the first column. `python3 scripts/validate-enterprise-kit.py [--root <repo>]` exits 0 with "enterprise kit: 7 keys, all templates consistent", else one `error: …` line per problem and exit 1.

- [ ] **Step 1:** write `keys.md` from spec §2 (one row per key, examples in plist/registry/JSON syntax under the table per key).
- [ ] **Step 2:** write the ADMX/ADML per spec §5.1 (`policyNamespaces target prefix="elevate" namespace="Reothor.Elevate"`, `supersededAdm` none, `resources minRequiredRevision="1.0"`, `supportedOn` definition `SUPPORTED_Elevate_1_6`, category `Reothor` → `Elevate`, seven policies with `class="Both"` and `key="Software\Policies\Reothor\Elevate"`; list elements `<list id="AllowedSignInMethods" key="Software\Policies\Reothor\Elevate\AllowedSignInMethods" additive="false"/>`; `<multiText id="ManagedProfiles" valueName="ManagedProfiles"/>`; boolean policy via `enabledValue`/`disabledValue` decimal 1/0 with `valueName="DisableUpdateCheck"`). ADML: `stringTable` with every `$(string.X)` referenced and `presentationTable` with every `$(presentation.X)`.
- [ ] **Step 3:** write the mobileconfig (payload type `com.apple.ManagedClient.preferences`, `PayloadContent` → `no.reothor.elevate` → `Forced` → one dict with `mcx_preference_settings` holding all seven keys with placeholders; `ManagedProfiles` as a string holding the example JSON), the Intune plist (a flat plist of the seven keys), the Jamf manifest (Jamf "custom schema" JSON: `title`, `description`, `properties` per key with `type`, `description`, `enum` for methods, `items` for lists), `cli/managed.json`, and the worked example (client id `11111111-2222-3333-4444-555555555555`, tenants `contoso.com` and `fabrikam.com` pinned, one profile "Prod incident" as in spec §7.1) in all three syntaxes plus `example.reg` writing HKLM keys.
- [ ] **Step 4:** write the validator (stdlib only: `xml.etree`, `plistlib`, `json`, `re`) implementing spec §5.1's checks, including a §7.1 shape check for every `ManagedProfiles` value it finds. Run it; fix the templates until it passes; break a copy (rename a policy) and confirm it fails.
- [ ] **Step 5:** workflow `enterprise-kit.yml`: on `pull_request` and `push` to main with `paths: [enterprise/**, docs/enterprise/**, scripts/validate-enterprise-kit.py]`, `ubuntu-latest`, `actions/checkout@v7`, `python3 scripts/validate-enterprise-kit.py`.
- [ ] **Step 6: Commit** — `git commit -m "Enterprise kit: ADMX/ADML, mobileconfig, Jamf and Intune templates, managed.json, validator and CI"`

---

### Task 17: Release workflow — `Elevate.pkg` and the enterprise kit

**Files:**
- Modify: `.github/workflows/release.yml` (macos job: "Package pkg", sign/notarize/staple when the installer secrets exist, checksum, artifact glob; publish job: "Enterprise kit" step, release notes "Enterprise" paragraph, `gh release create` file list)
- Modify: `docs/releasing.md` (new secrets `MACOS_INSTALLER_CERT_P12`, `MACOS_INSTALLER_CERT_PASSWORD`; the pkg; the kit)
- Test: `actionlint` if available (`brew install actionlint`), else `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/release.yml'))"`; run the kit zip step locally as a shell snippet with `VERSION=0.0.0` to confirm the zip layout (`Elevate-enterprise-kit-0.0.0/README.md`, `windows/Elevate.admx`, …, `keys.md`).

- [ ] **Step 1:** macOS job, after "Package DMG":

```yaml
      - name: Package pkg
        run: |
          pkgbuild --component build/Build/Products/Release/Elevate.app --install-location /Applications \
            --identifier no.reothor.elevate --version "$VERSION" "dist/Elevate-$VERSION-unsigned.pkg"
          if [ -n "${{ secrets.MACOS_INSTALLER_CERT_P12 }}" ]; then
            echo "${{ secrets.MACOS_INSTALLER_CERT_P12 }}" | base64 --decode > installer.p12
            security import installer.p12 -k build.keychain -P "${{ secrets.MACOS_INSTALLER_CERT_PASSWORD }}" -T /usr/bin/productsign
            productsign --sign "Developer ID Installer" "dist/Elevate-$VERSION-unsigned.pkg" "dist/Elevate-$VERSION.pkg"
            echo "PKG_SIGNED=1" >> "$GITHUB_ENV"
          else
            mv "dist/Elevate-$VERSION-unsigned.pkg" "dist/Elevate-$VERSION.pkg"
          fi
          rm -f "dist/Elevate-$VERSION-unsigned.pkg"
```

Read the existing keychain setup step (the one importing `MACOS_CERT_P12`) and reuse its keychain name and unlock; notarize/staple the pkg in the existing "Notarize DMG" pattern when `PKG_SIGNED == 1`; checksum after stapling; add `macos/dist/Elevate-*.pkg` and `.pkg.sha256` to the upload; add a job output `pkg_signed`.

- [ ] **Step 2:** publish job: after "Versions and hashes", `PKG_SHA256`; "Enterprise kit" step builds `dist/Elevate-enterprise-kit-$VERSION.zip` from `enterprise/` plus `docs/enterprise/keys.md`, stamping `$VERSION` into the kit README's `{{VERSION}}` placeholder, plus `.sha256`; release notes gain an "## Enterprise" section (pkg, signed or not, kit, link to `docs/enterprise/README.md`); the `gh release create` list gains the pkg, the kit and their checksums.
- [ ] **Step 3:** `docs/releasing.md` updates; run the YAML check.
- [ ] **Step 4: Commit** — `git commit -m "Release: signed Elevate.pkg and the enterprise kit"` (push over SSH: workflow files need the `workflow` scope, see memory; `git push ssh://git@ssh.github.com:443/FrodeHus/elevate.git managed-configuration`).

---

### Task 18: Documentation

**Files:**
- Create: `docs/enterprise/README.md`, `macos-jamf.md`, `macos-intune.md`, `windows-intune.md`, `windows-group-policy.md`, `cli.md`, `profiles.md`, `troubleshooting.md` (keys.md exists from Task 16)
- Modify: `README.md` (Enterprise-ready section between Install and Repository layout; `enterprise/` in the layout block; Documentation row), `docs/README.md` (Reference: Enterprise entries; Design documents: the spec), `macos/README.md`, `windows/README.md`, `cli/README.md` (a "Managed configuration" paragraph linking to `docs/enterprise/`), `docs/troubleshooting.md` (user guide: "A field says Managed by your organization"), `CHANGELOG.md` (Unreleased section)
- Test: `python3 scripts/validate-enterprise-kit.py` still passes; every relative link in the new pages resolves (`for f in docs/enterprise/*.md; do grep -o '](\([^)#]*\)' "$f" | …; done` or a small Python link checker); the pages read end to end as the spec's §5.3 demands.

- [ ] **Step 1:** write each page per spec §5.3, in the voice of the existing guides (`docs/getting-started.md`): what you end up with, prerequisites, numbered steps with exact UI paths (Jamf Pro → Computers → Configuration Profiles → New → Application & Custom Settings → External Applications / Upload; Intune → Devices → macOS → Configuration → Create → Templates → Custom; Intune → Devices → Windows → Configuration → Import ADMX; Group Policy central store `\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions`), verification (Settings caption, Diagnostics keys, `elevate config`, `reg query`, `defaults read`, `sudo profiles show`), and the commands in fenced blocks. `cli.md` includes an Ansible task (`ansible.builtin.copy` with `owner: root`, `mode: "0644"`) and a Jamf script (`mkdir -p /etc/elevate && cat > /etc/elevate/managed.json <<'EOF' … EOF && chown root:wheel … && chmod 644 …`).
- [ ] **Step 2:** README and index changes; changelog: under `## [Unreleased]` list the three stages, the pkg, the kit, the docs (follow the file's existing style).
- [ ] **Step 3:** run the validator and the link check.
- [ ] **Step 4: Commit** — `git commit -m "Docs: enterprise rollout guides, keys reference, README"`; push; mark the PR ready (`gh pr ready`).

---

## Self-review

- Spec coverage: §2 keys and sources → Tasks 1, 2; §3 model, policy, resolver, diagnostics → 1, 2, 3, 4, 7, 8; §4 stage 1 → 5, 6, 15; §5 kit, release, docs → 16, 17, 18; §6 stage 2 → 9, 10, 15; §7 stage 3 → 11, 12, 13, 14, 15; §8 tests are inside each task; §9 delivery → 15 (draft PR) and 18 (ready).
- Deviation from the spec, deliberate: `ManagedConfiguration` keeps the raw `managedProfilesDocument` string (Tasks 1, 2); parsing lives with the profile layer (Tasks 11–14), so stage 1 does not depend on stage 3 types.
- Type names are consistent across tasks: `ManagedConfiguration`, `ManagedKey`, `DictionaryManagedSource`, `ManagedPreferences`, `ManagedPolicy`, `ManagedTenantResolver`, `ManagedTenantResolution`, `ManagedProfileSet`, `ManagedProfileResolver`, `ManagedProfileResolution`, `ManagedProfileFetcher`, `ProfileSource`, `DiagnosticsManaged`.
