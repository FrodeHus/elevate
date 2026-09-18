# Pinned App Registrations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an "Entra app registration" account use its own client ID ("pinned") alongside the Settings registration, let that ID be changed while keeping the account's configuration, and keep accounts (asking them to sign in again) when the Settings client ID changes.

**Architecture:** A new `SignInMethod.pinnedApp(clientId:)` case (stored `ownApp:<id>`) sits beside `.ownApp`. The macOS app routes it through a per-client-ID MSAL registry on signed builds and the loopback registry on unsigned builds. `AppModel` gains `dropRuntime`, a re-key path in `applyClientId`, and `changeSignInRegistration` that commits only after a same-user sign-in. The C# Core learns the storage form; the Windows app and CLI refuse pinned accounts with a clear message until their parity work.

**Tech Stack:** Swift 6 / SwiftUI (macOS 26), MSAL, Swift Testing; .NET 10, xUnit, FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-09-17-elevate-pinned-app-registration-design.md`

## Global Constraints

- Work in the worktree `/Users/frode.hus/pimtray-pinned` on branch `pinned-app-registration`. Never `git commit -a`; add files by path.
- Storage key for a pinned account: `ownApp:<id>`, `<id>` trimmed and lower-cased. `ownApp:` with an empty ID must fail to decode.
- `SignInMethod.pinnedApp` display name is "Entra app registration"; `detailedName` appends the first 8 characters of the ID and "…".
- Copy diagnostics never contains a client ID.
- Pinning is hidden/disabled when `ClientId` is managed. No new managed-configuration key.
- Only construct `.pinnedApp` through `SignInMethod.pinned(_:)` (it normalises the ID), except in `switch` patterns.
- C# pinned-account message, verbatim: `This account uses its own app registration, which this version of Elevate does not support yet.`
- Test commands:
  - Swift Core: `cd /Users/frode.hus/pimtray-pinned/macos && swift test`
  - macOS app: `cd /Users/frode.hus/pimtray-pinned/macos && xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO test 2>&1 | tail -30` (add `-only-testing:ElevateAppTests/<Suite>` to narrow)
  - C# Core: `dotnet test /Users/frode.hus/pimtray-pinned/windows/tests/Elevate.Core.Tests/Elevate.Core.Tests.csproj`
  - Windows app model: `dotnet test /Users/frode.hus/pimtray-pinned/windows/tests/Elevate.App.Tests -p:EnableWindowsTargeting=true`
  - CLI: `dotnet test /Users/frode.hus/pimtray-pinned/cli/tests/Elevate.Cli.Tests`

## File Map

| File | Change |
|---|---|
| `macos/Sources/ElevateCore/Models/SignInMethod.swift` | `pinnedApp` case, `pinned(_:)`, `normalizedClientId`, `isOwnApp`, `isPinned`, `detailedName`, storage |
| `macos/Sources/ElevateCore/Providers/GraphTransport.swift` | consent mapping uses `isOwnApp` |
| `macos/Tests/ElevateCoreTests/SignInMethodTests.swift` | pinned tests |
| `macos/Tests/ElevateCoreTests/Support/FakeTokenProvider.swift` | `signInError` |
| `windows/src/Elevate.Core/Models/SignInMethod.cs` | `PinnedApp`, `PinnedClientId`, `IsPinned`, `DetailedName`, storage, message const |
| `windows/src/Elevate.App.Model/Auth/CompositeTokenProvider.cs` | refuse pinned |
| `cli/src/Elevate.Cli/Auth/CliTokenProvider.cs` | refuse pinned |
| `windows/tests/...SignInMethodTests.cs`, `AppStateGoldenTests.cs`, `Fixtures/state-macos.json`, `Elevate.App.Tests/CompositeTokenProviderTests.cs`, `cli/tests/.../PinnedAccountTests.cs` | tests |
| `macos/Sources/ElevateApp/MSAL/MSALTokenProvider.swift` | stamps a given method |
| `macos/Sources/ElevateApp/MSAL/MSALProviderRegistry.swift` (new) | MSAL provider per pinned ID |
| `macos/Sources/ElevateApp/Auth/LoopbackProviderRegistry.swift` | nil for own-app forms |
| `macos/Sources/ElevateApp/Auth/CompositeTokenProvider.swift` | pinned route, `discardCachedSignIn` |
| `macos/Sources/ElevateApp/App/AppSettings.swift` | `pinnedClientId` |
| `macos/Sources/ElevateApp/App/AppModel.swift` | registry wiring, `pinnedRoute`, `effectiveClientId`, bootstrap, consent URL, `dropRuntime`, re-key in `applyClientId` |
| `macos/Sources/ElevateApp/App/AppModel+Accounts.swift` | availability, `loopbackStore`, `canPin`, labels, `changeSignInRegistration`, `refreshTenants` |
| `macos/Sources/ElevateApp/App/AppModel+Operations.swift` | diagnostics name |
| `macos/Sources/ElevateApp/App/PanelRoute.swift`, `Views/RouteWindow.swift` | `.changeRegistration` |
| `macos/Sources/ElevateApp/Views/AddAccountView.swift` | registration sub-options |
| `macos/Sources/ElevateApp/Views/ChangeRegistrationView.swift` (new) | Change app registration sheet |
| `macos/Sources/ElevateApp/Views/IdentitySection.swift` | menu item, detailed name |
| `macos/Sources/ElevateApp/Views/SettingsView.swift` | confirmation text |
| `macos/Tests/ElevateAppTests/AppModelPinnedAppTests.swift` (new) | app tests |
| `docs/getting-started.md`, `docs/entra-app-registration.md`, `CHANGELOG.md` | docs |

---

### Task 1: Swift Core `SignInMethod.pinnedApp`

**Files:**
- Modify: `macos/Sources/ElevateCore/Models/SignInMethod.swift`
- Modify: `macos/Sources/ElevateCore/Providers/GraphTransport.swift:88-91`
- Modify: `macos/Tests/ElevateCoreTests/Support/FakeTokenProvider.swift`
- Test: `macos/Tests/ElevateCoreTests/SignInMethodTests.swift`

**Interfaces:**
- Produces: `case pinnedApp(clientId: String)`; `static func pinned(_ clientId: String) -> SignInMethod`; `static func normalizedClientId(_ raw: String) -> String`; `var isOwnApp: Bool`; `var isPinned: Bool`; `var detailedName: String`; `usesMSAL == isOwnApp`; storage `ownApp:<id>`. `FakeTokenProvider.setSignInError(_ e: PIMError?)`.

- [ ] **Step 1: Write the failing tests** — append to `SignInMethodTests`:

```swift
    @Test func pinnedAppIsAnOwnAppFormWithItsOwnClientId() {
        let pinned = SignInMethod.pinned("  AAAAAAAA-2222-3333-4444-555555555555 ")
        #expect(pinned == .pinnedApp(clientId: "aaaaaaaa-2222-3333-4444-555555555555"))
        #expect(pinned.clientId == "aaaaaaaa-2222-3333-4444-555555555555")
        #expect(pinned.isOwnApp && pinned.isPinned && pinned.usesMSAL)
        #expect(SignInMethod.ownApp.isOwnApp && !SignInMethod.ownApp.isPinned)
        #expect(!SignInMethod.custom(clientId: "abc").isOwnApp)
        #expect(pinned.kind == .ownApp)
        #expect(pinned.displayName == "Entra app registration")
        #expect(pinned.detailedName == "Entra app registration (aaaaaaaa…)")
        #expect(SignInMethod.ownApp.detailedName == "Entra app registration")
        #expect(pinned.isPreauthorisedForEntraActivation && pinned.limitationSummary == nil)
        #expect(!pinned.sharesToolTokenCache && !pinned.isCustom)
        #expect(pinned != .ownApp)
    }

    @Test func pinnedAppRoundTripsLowerCased() throws {
        let pinned = SignInMethod.pinned("AAAAAAAA-2222-3333-4444-555555555555")
        let encoded = String(decoding: try JSONEncoder().encode(pinned), as: UTF8.self)
        #expect(encoded == "\"ownApp:aaaaaaaa-2222-3333-4444-555555555555\"")
        #expect(try JSONDecoder().decode(SignInMethod.self, from: Data(encoded.utf8)) == pinned)
        #expect(SignInMethod(storageKey: "ownApp:AAAAAAAA-2222-3333-4444-555555555555") == pinned)
        #expect(SignInMethod(storageKey: "ownApp:") == nil)
        #expect(SignInMethod(storageKey: "ownApp") == .ownApp)
        // A case built directly with upper case still encodes lower-cased.
        let raw = SignInMethod.pinnedApp(clientId: "BBBB")
        #expect(raw.storageKey == "ownApp:bbbb")
    }

    @Test func pinnedAppCountsAsOwnAppForTheManagedAllowList() {
        var cliOnly = ManagedConfiguration()
        cliOnly.allowedSignInMethods = [.azureCLI]
        #expect(!ManagedPolicy.isAllowed(.pinned("abc"), by: cliOnly))
        var ownOnly = ManagedConfiguration()
        ownOnly.allowedSignInMethods = [.ownApp]
        #expect(ManagedPolicy.isAllowed(.pinned("abc"), by: ownOnly))
    }
```

In `macos/Tests/ElevateCoreTests/AccessPackageProviderTests.swift`, next to `forbiddenMapsToConsentRequiredForOwnApp`, add the same test for a pinned identity (the provider goes through `GraphTransport`):

```swift
    @Test func forbiddenMapsToConsentRequiredForAPinnedApp() async {
        let (p, http) = makeProvider()
        await http.on("GET", "accessPackages/filterByCurrentUser", status: 403, body: Data(#"{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}"#.utf8))
        var pinned = identity
        pinned.signInMethod = .pinned("aaaaaaaa-2222-3333-4444-555555555555")
        await #expect(throws: PIMError.consentRequired) {
            _ = try await p.requestablePackages(identity: pinned, tenantId: "t1")
        }
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cd /Users/frode.hus/pimtray-pinned/macos && swift test --filter SignInMethodTests`
Expected: compile error, `pinned` / `pinnedApp` not found.

- [ ] **Step 3: Implement** in `SignInMethod.swift`:

Add the case and helpers:

```swift
public enum SignInMethod: Hashable, Sendable {
    case ownApp
    /// An Elevate-equivalent registration of the account's own, independent of the Settings
    /// client id. Build it with `pinned(_:)`, which normalises the id.
    case pinnedApp(clientId: String)
    case azureCLI
    case azurePowerShell
    case custom(clientId: String)

    /// A pinned own-app method with `clientId` trimmed and lower-cased, so the same GUID typed
    /// in another case cannot become a second method or a second keychain item.
    public static func pinned(_ clientId: String) -> SignInMethod {
        .pinnedApp(clientId: normalizedClientId(clientId))
    }

    public static func normalizedClientId(_ raw: String) -> String {
        raw.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    }
```

Update the switches:

```swift
    public var displayName: String {
        switch self {
        case .ownApp, .pinnedApp: "Entra app registration"
        case .azureCLI: "Azure CLI app"
        case .azurePowerShell: "Azure PowerShell app"
        case .custom: "Company app (client ID)"
        }
    }

    /// `displayName`, plus the start of the client id for a pinned registration so two
    /// "Entra app registration" accounts can be told apart.
    public var detailedName: String {
        guard case .pinnedApp(let id) = self else { return displayName }
        return "\(displayName) (\(id.prefix(8))…)"
    }

    public var clientId: String? {
        switch self {
        case .ownApp: nil
        case .pinnedApp(let id): id
        case .azureCLI: "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
        case .azurePowerShell: "1950a258-227b-4e31-a9cf-717495945fc2"
        case .custom(let id): id
        }
    }

    /// Either form of the Entra app registration: the Settings one or a pinned one.
    public var isOwnApp: Bool {
        switch self {
        case .ownApp, .pinnedApp: true
        default: false
        }
    }

    public var isPinned: Bool { if case .pinnedApp = self { true } else { false } }

    public var usesMSAL: Bool { isOwnApp }
```

`kind`: add `case .ownApp, .pinnedApp: .ownApp`. `isPreauthorisedForEntraActivation`: `case .ownApp, .pinnedApp, .custom: true`. Update the doc comment on `clientId` to "Client id of the registration, or nil for `.ownApp`, which uses the Settings client id."

Storage:

```swift
// Stored as a single string so existing state files keep decoding: the fixed methods by name,
// a pinned own-app registration as "ownApp:<client id>", a custom one as "custom:<client id>".
    public var storageKey: String {
        switch self {
        case .ownApp: "ownApp"
        case .pinnedApp(let id): "ownApp:\(Self.normalizedClientId(id))"
        case .azureCLI: "azureCLI"
        case .azurePowerShell: "azurePowerShell"
        case .custom(let id): "custom:\(id)"
        }
    }

    public init?(storageKey: String) {
        switch storageKey {
        case "ownApp": self = .ownApp
        case "azureCLI": self = .azureCLI
        case "azurePowerShell": self = .azurePowerShell
        default:
            for (prefix, make) in [("ownApp:", SignInMethod.pinned), ("custom:", { SignInMethod.custom(clientId: $0) })]
            where storageKey.hasPrefix(prefix) {
                let id = String(storageKey.dropFirst(prefix.count))
                guard !Self.normalizedClientId(id).isEmpty else { return nil }
                self = make(id)
                return
            }
            return nil
        }
    }
```

In `GraphTransport.swift`, change the consent check to:

```swift
        // Admin consent only helps an Entra app registration (the Settings one or a pinned one);
        // for a first-party or company sign-in a 403 is a plain refusal.
        if case .consentRequired = error, !identity.signInMethod.isOwnApp {
```

In `FakeTokenProvider.swift` add:

```swift
    var signInError: PIMError?
    func setSignInError(_ e: PIMError?) { signInError = e }
```

and make `signIn(method:)` start with `if let signInError { throw signInError }`.

- [ ] **Step 4: Run tests**

Run: `cd /Users/frode.hus/pimtray-pinned/macos && swift test`
Expected: all pass (220+ plus new).

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray-pinned
git add macos/Sources/ElevateCore/Models/SignInMethod.swift macos/Sources/ElevateCore/Providers/GraphTransport.swift macos/Tests/ElevateCoreTests/SignInMethodTests.swift macos/Tests/ElevateCoreTests/AccessPackageProviderTests.swift macos/Tests/ElevateCoreTests/Support/FakeTokenProvider.swift
git commit -m "Core: SignInMethod.pinnedApp stored as ownApp:<client id>"
```

---

### Task 2: C# Core storage form and Windows/CLI refusal

**Files:**
- Modify: `windows/src/Elevate.Core/Models/SignInMethod.cs`
- Modify: `windows/src/Elevate.App.Model/Auth/CompositeTokenProvider.cs` (`Provider`)
- Modify: `cli/src/Elevate.Cli/Auth/CliTokenProvider.cs` (`ClientIdFor`, `Provider`)
- Modify: `windows/tests/Elevate.Core.Tests/Fixtures/state-macos.json`, `windows/tests/Elevate.Core.Tests/AppStateGoldenTests.cs`
- Test: `windows/tests/Elevate.Core.Tests/SignInMethodTests.cs`, `windows/tests/Elevate.App.Tests/CompositeTokenProviderTests.cs`, `cli/tests/Elevate.Cli.Tests/PinnedAccountTests.cs` (new)

**Interfaces:**
- Produces: `SignInMethod.PinnedApp(string)`, `PinnedClientId`, `IsPinned`, `DetailedName`, `NormalizeClientId(string)`, `SignInMethod.PinnedUnsupportedMessage`. `UsesMsal` is false for pinned.

- [ ] **Step 1: Write the failing tests**

Append to `SignInMethodTests.cs`:

```csharp
    [Fact]
    public void PinnedAppRoundTripsLowerCased()
    {
        var pinned = SignInMethod.PinnedApp(" AAAAAAAA-2222-3333-4444-555555555555 ");

        pinned.Kind.Should().Be(SignInMethodKind.OwnApp);
        pinned.IsPinned.Should().BeTrue();
        pinned.PinnedClientId.Should().Be("aaaaaaaa-2222-3333-4444-555555555555");
        pinned.ClientId.Should().Be("aaaaaaaa-2222-3333-4444-555555555555");
        pinned.CustomClientId.Should().BeNull();
        pinned.UsesMsal.Should().BeFalse("the Windows app and CLI do not support pinned accounts yet");
        pinned.Should().NotBe(SignInMethod.OwnApp);
        pinned.DisplayName.Should().Be("Entra app registration");
        pinned.DetailedName.Should().Be("Entra app registration (aaaaaaaa…)");
        SignInMethod.OwnApp.IsPinned.Should().BeFalse();
        SignInMethod.OwnApp.DetailedName.Should().Be("Entra app registration");
        SignInMethod.Custom("abc").PinnedClientId.Should().BeNull();

        var encoded = Json.Serialize(pinned);
        encoded.Should().Be("\"ownApp:aaaaaaaa-2222-3333-4444-555555555555\"");
        Json.Deserialize<SignInMethod>(encoded).Should().Be(pinned);
        Json.Deserialize<SignInMethod>("\"ownApp:AAAAAAAA-2222-3333-4444-555555555555\"").Should().Be(pinned);
        SignInMethod.TryFromStorageKey("ownApp:", out _).Should().BeFalse();
    }
```

In `state-macos.json`, add a second identity after the first:

```json
    ,
    {
      "id" : "id2",
      "upn" : "p@contoso.com",
      "displayName" : "Pia",
      "homeTenantId" : "t-home",
      "signInMethod" : "ownApp:aaaaaaaa-2222-3333-4444-555555555555"
    }
```

(Place the comma so the array stays valid.) In `AppStateGoldenTests.MacOsStateFileDecodesToTheExpectedValues` replace `state.Identities.Should().ContainSingle();` with:

```csharp
        state.Identities.Should().HaveCount(2);
        state.Identities[1].SignInMethod.Should().Be(SignInMethod.PinnedApp("aaaaaaaa-2222-3333-4444-555555555555"));
```

Run the whole Core suite once after the fixture change and fix any other test that counted identities in this fixture the same way.

Append to `CompositeTokenProviderTests.cs`:

```csharp
    [Fact]
    public async Task PinnedAccountsAreRefusedWithAClearMessage()
    {
        var composite = new CompositeTokenProvider(new FakeOwnAppProvider(), new FakeFirstPartyProviders(new FakeTokenProvider()));
        var pinned = Sample.Identity("pin", SignInMethod.PinnedApp("aaaaaaaa-2222-3333-4444-555555555555"));

        var act = () => composite.AccessTokenAsync(pinned, "t1", Scopes.GraphAll, CancellationToken.None);

        (await act.Should().ThrowAsync<PimException>()).Which.Message.Should().Contain(SignInMethod.PinnedUnsupportedMessage);
    }
```

Create `cli/tests/Elevate.Cli.Tests/PinnedAccountTests.cs`:

```csharp
using Elevate.Cli.Auth;
using Elevate.Cli.Infrastructure;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Cli.Tests;

public class PinnedAccountTests
{
    [Fact]
    public async Task PinnedAccountsAreNotAvailableAndSayWhy()
    {
        var dir = Directory.CreateTempSubdirectory("elevate-cli-pinned").FullName;
        try
        {
            var tokens = new CliTokenProvider(new TokenCacheStore(dir, true),
                () => "11111111-2222-3333-4444-555555555555", default, _ => { });
            var pinned = SignInMethod.PinnedApp("aaaaaaaa-2222-3333-4444-555555555555");

            tokens.IsAvailable(pinned).Should().BeFalse();
            var act = () => tokens.SignInAsync(pinned);
            (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Be(SignInMethod.PinnedUnsupportedMessage);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
```

Adjust the `using` lines and the `TokenCacheStore`/`InteractiveFlow` arguments to what `cli/src/Elevate.Cli/Commands/CommandContext.cs:82-83` passes if they do not compile.

- [ ] **Step 2: Run to verify failure**

Run the C# Core, Windows app model and CLI test commands from Global Constraints.
Expected: compile errors for `PinnedApp`, `PinnedUnsupportedMessage`.

- [ ] **Step 3: Implement**

In `SignInMethod.cs`, replace the constructor and `CustomClientId` with one field and add members:

```csharp
    /// <summary>Shown wherever a pinned account is used before the Windows app and CLI support it.</summary>
    public const string PinnedUnsupportedMessage =
        "This account uses its own app registration, which this version of Elevate does not support yet.";

    // One field for the client id of both forms that carry one, so equality covers it.
    private readonly string? _clientId;

    private SignInMethod(SignInMethodKind kind, string? clientId)
    {
        Kind = kind;
        _clientId = clientId;
    }

    public SignInMethodKind Kind { get; }

    /// <summary>Client id of a custom registration; null for every other method.</summary>
    public string? CustomClientId => Kind == SignInMethodKind.Custom ? _clientId : null;

    /// <summary>Client id of a pinned own-app registration (stored "ownApp:&lt;id&gt;"); null otherwise.</summary>
    public string? PinnedClientId => Kind == SignInMethodKind.OwnApp ? _clientId : null;

    public bool IsPinned => PinnedClientId is not null;

    /// <summary>An own-app registration of the account's own, independent of the settings client id.</summary>
    public static SignInMethod PinnedApp(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return new SignInMethod(SignInMethodKind.OwnApp, NormalizeClientId(clientId));
    }

    /// <summary>Trimmed and lower-cased, matching the Swift side.</summary>
    public static string NormalizeClientId(string clientId) => clientId.Trim().ToLowerInvariant();

    public string DetailedName => IsPinned
        ? $"{DisplayName} ({PinnedClientId![..Math.Min(8, PinnedClientId.Length)]}…)"
        : DisplayName;
```

Change `ClientId`: `SignInMethodKind.OwnApp => _clientId,` and `_ => _clientId,`. Change `UsesMsal`:

```csharp
    /// <summary>
    /// Whether the settings registration's MSAL provider serves this method. False for a pinned
    /// account until the Windows app and CLI support per-account registrations.
    /// </summary>
    public bool UsesMsal => Kind == SignInMethodKind.OwnApp && !IsPinned;
```

`StorageKey`: `SignInMethodKind.OwnApp => IsPinned ? $"ownApp:{_clientId}" : "ownApp",` and `_ => $"custom:{_clientId}",`. In `TryFromStorageKey`, before the `custom:` block:

```csharp
        const string pinnedPrefix = "ownApp:";
        if (storageKey is not null && storageKey.StartsWith(pinnedPrefix, StringComparison.Ordinal))
        {
            var id = storageKey[pinnedPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(id))
            {
                method = PinnedApp(id);
                return true;
            }

            return false;
        }
```

Update the class doc comment's list of stored strings to include `"ownApp:<client id>"`, and the same in `SignInMethodJsonConverter.cs`.

In the Windows `CompositeTokenProvider.Provider`, first line:

```csharp
        if (method.IsPinned)
        {
            throw new PimException(PimErrorKind.Unexpected, SignInMethod.PinnedUnsupportedMessage);
        }
```

In `CliTokenProvider.ClientIdFor`, first line: `if (method.IsPinned) return null;` (use braces per repo style). In `Provider`, make the exception message:

```csharp
            method.IsPinned
                ? SignInMethod.PinnedUnsupportedMessage
                : method.UsesMsal
                    ? "No client ID is configured. Run 'elevate config set client-id <application id>' first, or sign in with --method cli."
                    : "That sign-in method has no usable client ID.",
```

If `PimException` formats its message so `Contain` fails, assert on the detail the way other `CompositeTokenProviderTests` do.

- [ ] **Step 4: Run tests** — all three C# commands. Expected: pass.

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray-pinned
git add windows/src/Elevate.Core windows/src/Elevate.App.Model/Auth/CompositeTokenProvider.cs cli/src/Elevate.Cli/Auth/CliTokenProvider.cs windows/tests cli/tests/Elevate.Cli.Tests/PinnedAccountTests.cs
git commit -m "C# Core: read and write pinned own-app accounts; Windows and CLI refuse them for now"
```

---

### Task 3: macOS token routing for pinned accounts

**Files:**
- Modify: `macos/Sources/ElevateApp/MSAL/MSALTokenProvider.swift`
- Create: `macos/Sources/ElevateApp/MSAL/MSALProviderRegistry.swift`
- Modify: `macos/Sources/ElevateApp/Auth/LoopbackProviderRegistry.swift`
- Modify: `macos/Sources/ElevateApp/Auth/CompositeTokenProvider.swift`
- Test: `macos/Tests/ElevateAppTests/AppModelPinnedAppTests.swift` (new)

**Interfaces:**
- Consumes: Task 1 `SignInMethod` API.
- Produces:
  - `MSALTokenProvider.init(method: SignInMethod = .ownApp, clientId:redirectUri:anchor:gate:)`
  - `final class MSALProviderRegistry: Sendable { init(anchor: AuthAnchorWindow, gate: InteractiveGate); func provider(clientId: String) throws -> MSALTokenProvider }`
  - `CompositeTokenProvider.init(msal:loopback:ownAppLoopback:pinned:)` with `pinned: (@Sendable (String) throws -> any TokenProviding)?`
  - `CompositeTokenProvider.discardCachedSignIn(_ identity: Identity) async`
  - `LoopbackProviderRegistry.provider(for:)` returns nil when `method.isOwnApp`.

- [ ] **Step 1: Write the failing test** — create `AppModelPinnedAppTests.swift`:

```swift
import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// Accounts with their own Entra app registration ("pinned"), beside the Settings registration.
@MainActor
struct AppModelPinnedAppTests {
    static let settingsId = "11111111-2222-3333-4444-555555555555"
    static let pinnedId = "aaaaaaaa-2222-3333-4444-555555555555"

    @Test func loopbackRegistryNeverServesOwnAppFormsDirectly() {
        let registry = LoopbackProviderRegistry(http: StubHTTPClient(), gate: InteractiveGate(),
                                                makeStore: { _ in InMemoryRefreshTokenStore() })
        #expect(registry.provider(for: .ownApp) == nil)
        #expect(registry.provider(for: .pinned(Self.pinnedId)) == nil)
        let stamped = registry.provider(clientId: Self.pinnedId, reportedMethod: .pinned(Self.pinnedId))
        #expect(stamped?.reportedMethod == .pinned(Self.pinnedId))
        // A company-app provider for the same id is a separate cache slot with its own stamp.
        #expect(registry.provider(for: .custom(clientId: Self.pinnedId))?.reportedMethod == .custom(clientId: Self.pinnedId))
    }

    @Test func compositeRoutesPinnedAccountsThroughThePinnedRoute() async throws {
        let fake = FakeTokenProvider()
        let routed = RouteLog()
        let composite = CompositeTokenProvider(
            msal: nil, loopback: LoopbackProviderRegistry(http: StubHTTPClient(), gate: InteractiveGate()),
            pinned: { id in routed.record(id); return fake })
        let identity = Sample.identity(method: .pinned(Self.pinnedId))
        _ = try await composite.accessToken(identity: identity, tenantId: Sample.tenantId, scopes: [])
        #expect(routed.ids == [Self.pinnedId])
        #expect(await fake.silentCalls == [Sample.tenantId])
        await composite.discardCachedSignIn(identity)
        #expect(await fake.signOutCalls == [Sample.identityId])
    }
}

/// Records the client ids the composite asked the pinned route for.
final class RouteLog: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [String] = []
    func record(_ id: String) { lock.withLock { stored.append(id) } }
    var ids: [String] { lock.withLock { stored } }
}
```

`InMemoryRefreshTokenStore` is the store `OAuthSessionTests` uses; if the app test target cannot see it, add its file to the `ElevateAppTests` sources in `macos/project.yml` the same way `Tests/ElevateCoreTests/Support` is added.

- [ ] **Step 2: Run to verify failure**

Run the macOS app command with `-only-testing:ElevateAppTests/AppModelPinnedAppTests`.
Expected: compile error (`pinned:` argument, `discardCachedSignIn`).

- [ ] **Step 3: Implement**

`MSALTokenProvider`:

```swift
    /// The method stamped on the identities this provider returns: `.ownApp` for the Settings
    /// registration, `.pinnedApp` for a registration an account was added with.
    let method: SignInMethod

    init(method: SignInMethod = .ownApp, clientId: String, redirectUri: String, anchor: AuthAnchorWindow,
         gate: InteractiveGate = InteractiveGate()) throws {
        precondition(method.isOwnApp, "MSAL serves only Entra app registrations, got \(method)")
        ...existing body...
        self.method = method
    }
```

In `signIn(method:)`: `guard method == self.method else { throw PIMError.unexpected(status: 0, body: "MSAL only signs in with an Entra app registration") }`. Make `identity(from:)` take the method: `static func identity(from account: MSALAccount, method: SignInMethod) -> Identity` and pass `self.method` at both call sites (`signIn`, `identities()`), stamping `signInMethod: method`.

Create `MSALProviderRegistry.swift`:

```swift
import Foundation
import os
import ElevateCore

/// One `MSALTokenProvider` per pinned client id (`SignInMethod.pinnedApp`), created on first use
/// and kept for the life of the app. The Settings registration keeps its own provider in
/// `AppModel`; these stamp `.pinnedApp` so each account routes back to its own registration.
/// All share the redirect URI, the auth anchor and the interactive gate.
final class MSALProviderRegistry: @unchecked Sendable {
    private let anchor: AuthAnchorWindow
    private let gate: InteractiveGate
    private let providers = OSAllocatedUnfairLock<[String: MSALTokenProvider]>(initialState: [:])

    init(anchor: AuthAnchorWindow, gate: InteractiveGate) {
        self.anchor = anchor
        self.gate = gate
    }

    func provider(clientId: String) throws -> MSALTokenProvider {
        let method = SignInMethod.pinned(clientId)
        guard let id = method.clientId, !id.isEmpty else {
            throw PIMError.unexpected(status: 0, body: "Enter the application (client) ID as a GUID")
        }
        return try providers.withLockUnchecked { cache in
            if let existing = cache[id] { return existing }
            let created = try MSALTokenProvider(method: method, clientId: id, redirectUri: AppSettings.redirectUri,
                                                anchor: anchor, gate: gate)
            cache[id] = created
            return created
        }
    }
}
```

`LoopbackProviderRegistry.provider(for:)`:

```swift
    /// The provider for a loopback method, or nil for either Entra app registration form, which
    /// go through `provider(clientId:reportedMethod:)` on unsigned builds and MSAL otherwise.
    func provider(for method: SignInMethod) -> LoopbackTokenProvider? {
        guard !method.isOwnApp, let clientId = method.clientId else { return nil }
        return provider(clientId: clientId, method: method, reportedMethod: method)
    }
```

`CompositeTokenProvider`: add the stored closure and parameter:

```swift
    /// Resolves the provider for a pinned client id: an MSAL registry entry on a signed build,
    /// a loopback provider stamping `.pinnedApp` on an unsigned one. nil where pinning is unavailable.
    private let pinned: (@Sendable (String) throws -> any TokenProviding)?

    init(msal: MSALTokenProvider?, loopback: LoopbackProviderRegistry, ownAppLoopback: LoopbackTokenProvider? = nil,
         pinned: (@Sendable (String) throws -> any TokenProviding)? = nil) {
        ...
        self.pinned = pinned
    }
```

Routing:

```swift
    private func provider(for method: SignInMethod) throws -> any TokenProviding {
        switch method {
        case .pinnedApp(let id):
            guard let pinned else { throw PIMError.unexpected(status: 0, body: "Sign-in is unavailable in this build") }
            return try pinned(id)
        case .ownApp:
            if let msal { return msal }
            guard let ownAppLoopback else {
                throw PIMError.unexpected(status: 0, body: "Configure a client id in Settings")
            }
            return ownAppLoopback
        default:
            guard let provider = loopback.provider(for: method) else {
                throw PIMError.unexpected(status: 0, body: "Unsupported sign-in method")
            }
            return provider
        }
    }

    /// Forgets `identity`'s saved sign-in for its own method without opening a browser: MSAL's
    /// cache entry is removed locally, a loopback refresh token is deleted from the keychain.
    func discardCachedSignIn(_ identity: Identity) async {
        guard let provider = try? provider(for: identity.signInMethod) else { return }
        if let msal = provider as? MSALTokenProvider {
            try? msal.removeCachedAccounts([identity])
        } else {
            try? await provider.signOut(identity)
        }
    }
```

Update the type's doc comment to mention the pinned route.

- [ ] **Step 4: Run tests** — macOS app command, full suite. Expected: pass (the call site in `AppModel.live()` still compiles because `method` defaults to `.ownApp`).

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray-pinned
git add macos/Sources/ElevateApp/MSAL macos/Sources/ElevateApp/Auth macos/Tests/ElevateAppTests/AppModelPinnedAppTests.swift
git commit -m "macOS: route pinned accounts to a per-client-id MSAL or loopback provider"
```

---

### Task 4: AppModel support for adding and using pinned accounts

**Files:**
- Modify: `macos/Sources/ElevateApp/App/AppSettings.swift`
- Modify: `macos/Sources/ElevateApp/App/AppModel.swift` (init, `live()`, `adminConsentURL`, `bootstrap`, `applyClientId` composite rebuild)
- Modify: `macos/Sources/ElevateApp/App/AppModel+Accounts.swift`
- Modify: `macos/Sources/ElevateApp/App/AppModel+Operations.swift:61-64`
- Modify: `macos/Tests/ElevateAppTests/Support/TestModel.swift`
- Test: `macos/Tests/ElevateAppTests/AppModelPinnedAppTests.swift`

**Interfaces:**
- Consumes: Task 3 registry, composite `pinned:`.
- Produces on `AppModel`:
  - `init(..., pinnedMSAL: MSALProviderRegistry? = nil, ...)`
  - `nonisolated static func pinnedRoute(registry: MSALProviderRegistry?, loopback: LoopbackProviderRegistry, viaLoopback: Bool) -> @Sendable (String) throws -> any TokenProviding`
  - `func effectiveClientId(for method: SignInMethod) -> String?`
  - `func loopbackStore(for method: SignInMethod) -> LoopbackTokenProvider?`
  - `var canPin: Bool`
  - `var settingsRegistrationLabel: String`
  - `func matchesSettingsClientId(_ raw: String) -> Bool`
  - `var rememberedPinnedClientId: String`
  - `func isAccountBusy(_ identityId: String) -> Bool`
  - `func discardCachedSignIn(_ identity: Identity) async`
- `AppSettings.pinnedClientId: String` (key `pinnedClientId`).
- `makeModel(..., pinningAvailable: Bool = false)` is not added; tests use `ownAppViaLoopback: true`, which enables pinning.

- [ ] **Step 1: Write the failing tests** — add to `AppModelPinnedAppTests`:

```swift
    @Test func pinnedMethodIsAvailableWithoutASettingsClientId() async {
        let model = await makeModel(ownAppViaLoopback: true)
        defer { cleanup(model) }
        #expect(!model.isAvailable(.ownApp))
        #expect(model.canPin)
        #expect(model.isAvailable(.pinned(Self.pinnedId)))
        #expect(!model.isAvailable(.pinned("not-a-guid")))
        #expect(model.loopbackStore(for: .pinned(Self.pinnedId))?.reportedMethod == .pinned(Self.pinnedId))
        #expect(model.effectiveClientId(for: .pinned(Self.pinnedId)) == Self.pinnedId)
        #expect(model.effectiveClientId(for: .ownApp) == nil)
        #expect(model.settingsRegistrationLabel == "not configured")
    }

    @Test func pinningNeedsATransportAndAnUnmanagedClientId() async {
        let signed = await makeModel(ownAppViaLoopback: false)
        #expect(!signed.canPin)   // no MSAL registry in tests
        #expect(!signed.isAvailable(.pinned(Self.pinnedId)))
        cleanup(signed)

        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": Self.settingsId]))
        let model = await makeModel(managed: managed, ownAppViaLoopback: true)
        #expect(!model.canPin)
        #expect(!model.isAvailable(.pinned(Self.pinnedId)))
        #expect(model.settingsRegistrationLabel == "managed by your organization")
        cleanup(model)
    }

    @Test func addingAPinnedAccountRemembersTheIdAndStampsTheAccount() async {
        let tokens = FakeTokenProvider()
        let model = await makeModel(tokens: tokens, ownAppViaLoopback: true)
        defer { cleanup(model) }
        let added = await model.addAccount(method: .pinned(Self.pinnedId))
        #expect(added)
        #expect(model.identity("new")?.signInMethod == .pinned(Self.pinnedId))
        #expect(model.rememberedPinnedClientId == Self.pinnedId)
        #expect(model.settings.clientId.isEmpty)
    }

    @Test func consentLinkUsesThePinnedClientId() async {
        let settings = makeSettings()
        settings.clientId = Self.settingsId
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        defer { cleanup(model) }
        model.state.identities = [Sample.identity(method: .pinned(Self.pinnedId))]
        let url = model.adminConsentURL(identityId: Sample.identityId, tenantId: Sample.tenantId)?.absoluteString
        #expect(url?.contains(Self.pinnedId) == true)
        #expect(url?.contains(Self.settingsId) == false)
        #expect(url?.contains("nativeclient") == true)
        #expect(model.matchesSettingsClientId(Self.settingsId.uppercased()))
        #expect(!model.matchesSettingsClientId(Self.pinnedId))
    }

    @Test func pinnedSharedAppIdGetsTheSharedConsentRedirect() async {
        let model = await makeModel(ownAppViaLoopback: true)
        defer { cleanup(model) }
        model.state.identities = [Sample.identity(method: .pinned(AppSettings.sharedClientId))]
        let url = model.adminConsentURL(identityId: Sample.identityId, tenantId: Sample.tenantId)?.absoluteString
        #expect(url?.contains("elevate.reothor.no") == true)
    }

    @Test func diagnosticsNeverCarryThePinnedClientId() async {
        let model = await makeModel(ownAppViaLoopback: true)
        defer { cleanup(model) }
        model.state.identities = [Sample.identity(method: .pinned(Self.pinnedId))]
        let text = model.diagnosticsText()
        #expect(!text.contains(Self.pinnedId))
        #expect(text.contains("Entra app registration (own client ID)"))
    }

    @Test func busyAccountsAreReported() async {
        let model = await makeModel(ownAppViaLoopback: true)
        defer { cleanup(model) }
        #expect(!model.isAccountBusy(Sample.identityId))
        model.inFlight = [Sample.azureKey]
        #expect(model.isAccountBusy(Sample.identityId))
        model.inFlight = []
        model.busy = [Sample.tenantKey]
        #expect(model.isAccountBusy(Sample.identityId))
    }
```

Copy the managed-config construction from `AppModelManagedTests` if the type names differ.

- [ ] **Step 2: Run to verify failure** — app command, `-only-testing:ElevateAppTests/AppModelPinnedAppTests`. Expected: compile errors.

- [ ] **Step 3: Implement**

`AppSettings`: add beside `customClientIdKey`:

```swift
    static let pinnedClientIdKey = "pinnedClientId"
```

and beside `customClientId`:

```swift
    /// Last client id typed into "Use a different registration" in Add account.
    var pinnedClientId: String {
        didSet { defaults.set(pinnedClientId, forKey: Self.pinnedClientIdKey) }
    }
```

and in `init` after `customClientId = …`: `pinnedClientId = defaults.string(forKey: Self.pinnedClientIdKey) ?? ""`.

`AppModel.swift`:
- Stored property below `msal`: `/// MSAL providers for pinned client ids; nil on unsigned builds and in tests.` `private let pinnedMSAL: MSALProviderRegistry?`
- `init` gains `pinnedMSAL: MSALProviderRegistry? = nil` (after `msal:`) and sets it.
- Add:

```swift
    /// The route `CompositeTokenProvider` uses for `.pinnedApp` accounts: MSAL on a signed build,
    /// the loopback flow (stamping `.pinnedApp`) on an unsigned one.
    nonisolated static func pinnedRoute(registry: MSALProviderRegistry?, loopback: LoopbackProviderRegistry,
                                        viaLoopback: Bool) -> @Sendable (String) throws -> any TokenProviding {
        { clientId in
            let method = SignInMethod.pinned(clientId)
            if viaLoopback {
                guard let id = method.clientId, let provider = loopback.provider(clientId: id, reportedMethod: method) else {
                    throw PIMError.unexpected(status: 0, body: "Enter the application (client) ID as a GUID")
                }
                return provider
            }
            guard let registry else { throw PIMError.unexpected(status: 0, body: "Sign-in is unavailable in this build") }
            return try registry.provider(clientId: clientId)
        }
    }

    /// The client id `method` signs in with: the Settings id for `.ownApp` (nil when it is not a
    /// valid GUID), the method's own id otherwise.
    func effectiveClientId(for method: SignInMethod) -> String? {
        guard method == .ownApp else { return method.clientId }
        let id = settings.clientId.trimmingCharacters(in: .whitespacesAndNewlines)
        return AppSettings.isValidClientId(id) ? id : nil
    }

    /// Forgets `identity`'s saved sign-in for its current method without a browser window.
    func discardCachedSignIn(_ identity: Identity) async {
        if let composite = tokens as? CompositeTokenProvider {
            await composite.discardCachedSignIn(identity)
        } else {
            try? await tokens.signOut(identity)
        }
    }
```

- In `live()`: `let pinnedMSAL = viaLoopback ? nil : MSALProviderRegistry(anchor: anchor, gate: gate)`; build the composite with `pinned: pinnedRoute(registry: pinnedMSAL, loopback: loopback, viaLoopback: viaLoopback)` and pass `pinnedMSAL: pinnedMSAL` to `AppModel(...)`.
- In `applyClientId`, the composite rebuild becomes `CompositeTokenProvider(msal: replacement, loopback: loopback, ownAppLoopback: ownAppLoopbackProvider, pinned: Self.pinnedRoute(registry: pinnedMSAL, loopback: loopback, viaLoopback: ownAppViaLoopback))`.
- Replace the two consent URL functions and the helper:

```swift
    /// Only Entra app registration accounts can be consented to: the first-party client ids are
    /// Microsoft's, already consented tenant-wide, and are not ours to request consent for.
    func adminConsentURL(identityId: String, tenantId: String) -> URL? {
        guard let method = identity(identityId)?.signInMethod, method.isOwnApp else { return nil }
        if method == .ownApp, !isConfigured { return nil }
        guard let clientId = effectiveClientId(for: method) else { return nil }
        return adminConsentURL(tenantSegment: tenantId, clientId: clientId)
    }

    func sharedAppAdminConsentURL() -> URL? {
        guard isConfigured, usesSharedApp else { return nil }
        return adminConsentURL(tenantSegment: "organizations", clientId: AppSettings.sharedClientId)
    }

    private func adminConsentURL(tenantSegment: String, clientId: String) -> URL? {
        var components = URLComponents()
        components.scheme = "https"
        components.host = "login.microsoftonline.com"
        components.path = "/\(tenantSegment)/v2.0/adminconsent"
        // The shared app registration has a Web redirect URI on the product site that explains
        // the consent result; own registrations keep the loopback-friendly `nativeclient` URI.
        let shared = clientId.caseInsensitiveCompare(AppSettings.sharedClientId) == .orderedSame
        let redirectURI = shared
            ? AppSettings.sharedConsentRedirectURI
            : "https://login.microsoftonline.com/common/oauth2/nativeclient"
        components.queryItems = [
            URLQueryItem(name: "client_id", value: clientId),
            URLQueryItem(name: "scope", value: (GraphScopes.all + GroupScopes.all + EntitlementScopes.all).joined(separator: " ")),
            URLQueryItem(name: "redirect_uri", value: redirectURI),
        ]
        return components.url
    }
```

- In `bootstrap()`: after the MSAL `.ownApp` reconciliation block add:

```swift
        // Pinned accounts on a signed build: each registration has its own MSAL cache.
        if let pinnedMSAL, !ownAppViaLoopback {
            for identity in state.identities {
                guard case .pinnedApp(let id) = identity.signInMethod,
                      let known = try? await pinnedMSAL.provider(clientId: id).identities() else { continue }
                if !known.contains(where: { $0.id == identity.id }) { needsSignIn.append(identity) }
            }
        }
```

  and change the loopback loop to:

```swift
        for identity in state.identities where !identity.signInMethod.usesMSAL || ownAppViaLoopback {
            guard let provider = loopbackStore(for: identity.signInMethod) else { continue }
```

  (removing the old `let known = …` lines).

`AppModel+Accounts.swift`:

```swift
    /// The last client id typed into "Use a different registration", for prefilling Add account.
    var rememberedPinnedClientId: String { settings.pinnedClientId }

    /// Whether an account can use an Entra app registration of its own: the organization allows
    /// the method and has not fixed the client id, and this build has a way to sign in with it.
    var canPin: Bool {
        isMethodAllowed(.ownApp) && !settings.isClientIdManaged && (pinnedMSAL != nil || ownAppViaLoopback)
    }

    /// How Add account and the Change app registration sheet name the Settings registration.
    var settingsRegistrationLabel: String {
        if settings.isClientIdManaged { return "managed by your organization" }
        if usesSharedApp { return "shared Elevate app" }
        guard let id = effectiveClientId(for: .ownApp) else { return "not configured" }
        return "\(id.prefix(8))…"
    }

    func matchesSettingsClientId(_ raw: String) -> Bool {
        guard let id = effectiveClientId(for: .ownApp) else { return false }
        return SignInMethod.normalizedClientId(id) == SignInMethod.normalizedClientId(raw)
    }

    /// Whether any request for the account is running, so its registration must not change now.
    func isAccountBusy(_ identityId: String) -> Bool {
        inFlight.contains { $0.identityId == identityId } || busy.contains { $0.identityId == identityId }
    }

    /// The loopback provider holding `method`'s refresh tokens, or nil when MSAL holds them.
    func loopbackStore(for method: SignInMethod) -> LoopbackTokenProvider? {
        switch method {
        case .ownApp: ownAppLoopbackProvider
        case .pinnedApp(let id): ownAppViaLoopback ? loopback.provider(clientId: id, reportedMethod: method) : nil
        default: loopback.provider(for: method)
        }
    }
```

`pinnedMSAL` is `private` in `AppModel.swift`; make it internal (`let pinnedMSAL`) with the comment `// internal for AppModel+Accounts`.

`isAvailable`: add `case .pinnedApp(let id): canPin && AppSettings.isValidClientId(id)`.

`loopbackClientId(for:)` becomes:

```swift
    private func loopbackClientId(for method: SignInMethod) -> String? {
        guard method.usesMSAL else { return method.clientId }
        guard ownAppViaLoopback else { return nil }
        return effectiveClientId(for: method)
    }
```

(update its doc comment: "either Entra app registration form on a signed build keeps its tokens in MSAL's cache").

`addAccount`:
- In the unavailable switch add `case .pinnedApp: notice = canPin ? "Enter the registration's application (client) ID as a GUID" : "Your own app registration is unavailable in this build"`.
- After the custom line: `if case .pinnedApp(let id) = method { settings.pinnedClientId = id }`.
- `"This account is already added with \(existing.signInMethod.detailedName)"` (both the notice and the log line).
- `let store = loopbackStore(for: method)`.

`retrySignIn`: `let store = loopbackStore(for: method)`.

`AppModel+Operations.diagnosticsText`: `method: identity.signInMethod.isPinned ? "\(identity.signInMethod.displayName) (own client ID)" : identity.signInMethod.displayName,`

- [ ] **Step 4: Run tests** — full app suite. Expected: pass, including the existing `AppModelSignInTransportTests` and `AppModelSharedAppTests`.

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray-pinned
git add macos/Sources/ElevateApp/App macos/Tests/ElevateAppTests
git commit -m "macOS: add and use accounts with their own Entra app registration"
```

---

### Task 5: Keep accounts when the Settings client ID changes

**Files:**
- Modify: `macos/Sources/ElevateApp/App/AppModel.swift` (`applyClientId`, `forgetIdentity`, new `dropRuntime`)
- Modify: `macos/Sources/ElevateApp/Views/SettingsView.swift:167`
- Test: `macos/Tests/ElevateAppTests/AppModelPinnedAppTests.swift`

**Interfaces:**
- Produces: `func dropRuntime(_ identityId: String)` (internal).

- [ ] **Step 1: Write the failing tests**

```swift
    private static func configuredState(for identity: Identity) -> (AppState, ManualRole) {
        var state = AppState()
        state.identities = [identity]
        state.upsertTenant(Sample.tenant(identityId: identity.id))
        let manual = ManualRole(tenantKey: TenantKey(identityId: identity.id, tenantId: Sample.tenantId),
                                scope: Sample.azureKey.scope, displayName: "Owner")
        state.manualRoles = [manual]
        return (state, manual)
    }

    @Test func changingTheSettingsIdKeepsFollowingAccountsAndAsksThemToSignIn() async throws {
        let settings = makeSettings()
        settings.clientId = Self.settingsId
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        defer { cleanup(model) }
        let own = Sample.identity(method: .ownApp)
        let pinned = Sample.identity("pin", method: .pinned(Self.pinnedId))
        var (state, _) = Self.configuredState(for: own)
        state.identities.append(pinned)
        state.upsertTenant(Sample.tenant(identityId: pinned.id))
        // Set after bootstrap, which would flag both for having no keychain token.
        model.state = state
        model.signInNeeded = []
        model.roles[Sample.tenantKey] = [Sample.role(Sample.azureKey, name: "Owner")]

        try model.applyClientId("22222222-2222-3333-4444-555555555555")

        #expect(model.identities.map(\.id) == [own.id, pinned.id])
        #expect(model.tenants(for: own.id).map(\.tenantId) == [Sample.tenantId])
        #expect(model.state.manualRoles.count == 1)
        #expect(model.roles(for: Sample.tenantKey).isEmpty)
        #expect(model.needsSignIn(own.id))
        #expect(!model.needsSignIn(pinned.id))
        #expect(model.identity(pinned.id)?.signInMethod == .pinned(Self.pinnedId))
    }

    @Test func signOutStillForgetsEverything() async {
        let model = await makeModel(ownAppViaLoopback: true)
        defer { cleanup(model) }
        let own = Sample.identity(method: .ownApp)
        model.state = Self.configuredState(for: own).0
        model.forgetIdentity(own.id)
        #expect(model.identities.isEmpty)
        #expect(model.tenants(for: own.id).isEmpty)
        #expect(!model.needsSignIn(own.id))
    }
```

- [ ] **Step 2: Run to verify failure** — expected: the first test fails (`identities` is `[pin]`).

- [ ] **Step 3: Implement**

Split `forgetIdentity`:

```swift
    /// Drops one identity and everything derived from it, in state and in memory.
    // internal for AppModel+Accounts
    func forgetIdentity(_ identityId: String) {
        state.removeIdentity(identityId)
        signInNeeded.remove(identityId)
        dropRuntime(identityId)
    }

    /// Drops what this session read or started for an account, keeping the account itself, its
    /// tenants, configured roles, profile entries and role memory. Used when its registration
    /// changes and everything read under the old one is stale.
    // internal for AppModel+Accounts
    func dropRuntime(_ identityId: String) {
        for key in roles.keys where key.identityId == identityId { roles[key] = nil }
        active = active.filter { $0.key.identityId != identityId }
        progress = progress.filter { $0.key.identityId != identityId }
        deactivationProgress = deactivationProgress.filter { $0.key.identityId != identityId }
        recentlyDeactivated = recentlyDeactivated.filter { $0.key.identityId != identityId }
        profileDeactivationProgress.removeAll()
        tenantErrors = tenantErrors.filter { $0.key.identityId != identityId }
        tenantsAwaitingSignIn = tenantsAwaitingSignIn.filter { $0.identityId != identityId }
        dropApprovals { $0.identityId == identityId }
        dropPolicies { $0.identityId == identityId }
    }
```

In `applyClientId` replace `for identity in ownApp { forgetIdentity(identity.id) }` with:

```swift
        // The accounts keep their tenants, roles and profiles; they sign in again under the new id.
        for identity in ownApp { dropRuntime(identity.id) }
        signInNeeded.formUnion(ownApp.map(\.id))
```

Update the comment above `let ownApp` to say only accounts following Settings are affected (`== .ownApp` deliberately excludes pinned ones).

`SettingsView` message:

```swift
            Text("Saving a different client ID signs out \(model.ownAppIdentityCount) account\(model.ownAppIdentityCount == 1 ? "" : "s") that use it. They keep their tenants, roles and profiles and need to sign in again. Accounts with their own app registration, and Azure CLI and Azure PowerShell accounts, are unaffected.")
```

Update `ownAppIdentityCount`'s doc comment: "Accounts that follow the Settings client id and would need to sign in again after it changes."

- [ ] **Step 4: Run tests** — full app suite. Expected: pass.

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray-pinned
git add macos/Sources/ElevateApp/App/AppModel.swift macos/Sources/ElevateApp/Views/SettingsView.swift macos/Tests/ElevateAppTests/AppModelPinnedAppTests.swift
git commit -m "macOS: keep accounts when the Settings client ID changes and ask them to sign in again"
```

---

### Task 6: Change an account's app registration

**Files:**
- Modify: `macos/Sources/ElevateApp/App/AppModel+Accounts.swift`
- Test: `macos/Tests/ElevateAppTests/AppModelPinnedAppTests.swift`

**Interfaces:**
- Produces: `@discardableResult func changeSignInRegistration(_ identity: Identity, to method: SignInMethod) async -> Bool` (sets `notice` on failure); `func refreshTenants(of identityId: String) async`.

- [ ] **Step 1: Write the failing tests** (`FakeTokenProvider.signIn` returns id `"new"` with the method it was given)

```swift
    private func modelWithAccount(_ identity: Identity, tokens: FakeTokenProvider,
                                  managed: ManagedConfiguration = .none) async -> AppModel {
        let settings = makeSettings(managed: managed)
        if !settings.isClientIdManaged { settings.clientId = Self.settingsId }
        let model = await makeModel(settings: settings, tokens: tokens, ownAppViaLoopback: true)
        model.state = Self.configuredState(for: identity).0
        model.signInNeeded = []
        return model
    }

    @Test func switchingToAPinnedIdCommitsAfterTheSameUserSignsIn() async {
        let tokens = FakeTokenProvider()
        let model = await modelWithAccount(Sample.identity("new", method: .ownApp), tokens: tokens)
        defer { cleanup(model) }
        let ok = await model.changeSignInRegistration(model.identity("new")!, to: .pinned(Self.pinnedId))
        #expect(ok)
        #expect(model.identity("new")?.signInMethod == .pinned(Self.pinnedId))
        #expect(model.tenants(for: "new").count == 1)
        #expect(model.state.manualRoles.count == 1)
        #expect(!model.needsSignIn("new"))
        // The old registration's token is discarded (the fake records it as a sign-out).
        #expect(await tokens.signOutCalls == ["new"])
        #expect(model.rememberedPinnedClientId == Self.pinnedId)
    }

    @Test func switchingBackToSettingsWorksToo() async {
        let tokens = FakeTokenProvider()
        let model = await modelWithAccount(Sample.identity("new", method: .pinned(Self.pinnedId)), tokens: tokens)
        defer { cleanup(model) }
        #expect(await model.changeSignInRegistration(model.identity("new")!, to: .ownApp))
        #expect(model.identity("new")?.signInMethod == .ownApp)
    }

    @Test func aDifferentUserChangesNothing() async {
        let tokens = FakeTokenProvider()
        let model = await modelWithAccount(Sample.identity("id-1", method: .ownApp), tokens: tokens)
        defer { cleanup(model) }
        let ok = await model.changeSignInRegistration(model.identity("id-1")!, to: .pinned(Self.pinnedId))
        #expect(!ok)
        #expect(model.identity("id-1")?.signInMethod == .ownApp)
        #expect(await tokens.signOutCalls == ["new"])
        #expect(model.notice?.contains("was expected") == true)
    }

    @Test func aFailedSignInChangesNothing() async {
        let tokens = FakeTokenProvider()
        await tokens.setSignInError(.network("Sign-in cancelled"))
        let model = await modelWithAccount(Sample.identity("new", method: .ownApp), tokens: tokens)
        defer { cleanup(model) }
        #expect(!(await model.changeSignInRegistration(model.identity("new")!, to: .pinned(Self.pinnedId))))
        #expect(model.identity("new")?.signInMethod == .ownApp)
        #expect(await tokens.signOutCalls.isEmpty)
        #expect(model.notice != nil)
    }

    @Test func theSameEffectiveIdKeepsTheSavedSignIn() async {
        let tokens = FakeTokenProvider()
        let model = await modelWithAccount(Sample.identity("new", method: .ownApp), tokens: tokens)
        defer { cleanup(model) }
        #expect(await model.changeSignInRegistration(model.identity("new")!, to: .pinned(Self.settingsId.uppercased())))
        #expect(model.identity("new")?.signInMethod == .pinned(Self.settingsId))
        #expect(await tokens.signOutCalls.isEmpty)
    }

    @Test func noChangeIsANoOpAndBusyOrManagedAccountsAreRefused() async {
        let tokens = FakeTokenProvider()
        let model = await modelWithAccount(Sample.identity(method: .pinned(Self.pinnedId)), tokens: tokens)
        #expect(await model.changeSignInRegistration(model.identity(Sample.identityId)!, to: .pinned(Self.pinnedId)))
        #expect(await tokens.storedIdentities.isEmpty)   // no sign-in happened
        model.inFlight = [Sample.azureKey]
        #expect(!(await model.changeSignInRegistration(model.identity(Sample.identityId)!, to: .ownApp)))
        cleanup(model)

        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": Self.settingsId]))
        let locked = await modelWithAccount(Sample.identity(method: .ownApp), tokens: tokens, managed: managed)
        #expect(!(await locked.changeSignInRegistration(locked.identity(Sample.identityId)!, to: .pinned(Self.pinnedId))))
        #expect(locked.notice?.contains("organization") == true)
        cleanup(locked)
    }
```

- [ ] **Step 2: Run to verify failure** — expected: compile error, `changeSignInRegistration` missing.

- [ ] **Step 3: Implement** in `AppModel+Accounts.swift` (next to `retrySignIn`):

```swift
    /// Moves an Entra app registration account to another registration — a pinned client id or
    /// the Settings one — keeping its tenants, configured roles, profiles and role memory. The
    /// change is saved only after the same user has signed in with the new registration; a
    /// cancelled sign-in or a different account changes nothing. Sets `notice` on failure.
    @discardableResult
    func changeSignInRegistration(_ identity: Identity, to method: SignInMethod) async -> Bool {
        let current = identity.signInMethod
        guard current.isOwnApp, method.isOwnApp else {
            notice = "Only Entra app registration accounts can change registration"
            return false
        }
        guard method != current else { return true }
        guard !settings.isClientIdManaged else {
            notice = "The app registration is managed by your organization"
            logError("Change registration: \(notice ?? "")")
            return false
        }
        guard isAvailable(method) else {
            notice = method == .ownApp ? "Configure a client ID in Settings first" : "Enter the application (client) ID as a GUID"
            logError("Change registration: \(notice ?? "")")
            return false
        }
        guard !isAccountBusy(identity.id) else {
            notice = "Wait for this account's requests to finish"
            return false
        }
        if case .pinnedApp(let id) = method { settings.pinnedClientId = id }
        do {
            let signedIn = try await tokens.signIn(method: method)
            guard signedIn.id == identity.id else {
                // Keep a session that belongs to another account already in the list.
                if !state.identities.contains(where: { $0.id == signedIn.id }) {
                    try? await tokens.signOut(signedIn)
                }
                notice = "Signed in as \(signedIn.upn), but \(identity.upn) was expected. Nothing was changed."
                logError("Change registration: got \(signedIn.upn), expected \(identity.upn)")
                return false
            }
            guard let index = state.identities.firstIndex(where: { $0.id == identity.id }) else { return false }
            let old = state.identities[index]
            state.identities[index].signInMethod = method
            persist()
            if effectiveClientId(for: old.signInMethod).map(SignInMethod.normalizedClientId)
                != effectiveClientId(for: method).map(SignInMethod.normalizedClientId) {
                await discardCachedSignIn(old)
            }
            dropRuntime(identity.id)
            signInNeeded.remove(identity.id)
            if let failure = await loopbackStore(for: method)?.persistenceError() {
                notice = "Signed in, but the refresh token could not be saved to the Keychain: \(failure). You will be asked to sign in again after restart."
                logError("Refresh token not saved to the Keychain: \(failure)")
            } else {
                notice = nil
            }
            await refreshTenants(of: identity.id)
            return true
        } catch {
            let message = (error as? PIMError)?.userMessage ?? error.localizedDescription
            notice = message
            logError("Change registration (\(method.detailedName)): \(message)")
            return false
        }
    }

    /// Reads every tenant of an account again, clearing their errors first.
    func refreshTenants(of identityId: String) async {
        let keys = tenants(for: identityId).map(\.id)
        for key in keys { tenantErrors[key] = nil }
        let generation = configGeneration
        await withTaskGroup(of: Void.self) { group in
            for key in keys {
                group.addTask {
                    guard await self.configGeneration == generation else { return }
                    await self.refresh(key)
                }
            }
        }
    }
```

Replace the matching block at the end of `retrySignIn` (from `let keys = …` through the task group) with `await refreshTenants(of: identity.id)`.

Note on the order in the success path: `discardCachedSignIn(old)` must run with `old` (the identity carrying the old method), so the composite routes it to the old provider.

- [ ] **Step 4: Run tests** — full app suite. Expected: pass.

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray-pinned
git add macos/Sources/ElevateApp/App/AppModel+Accounts.swift macos/Tests/ElevateAppTests/AppModelPinnedAppTests.swift
git commit -m "macOS: change an account's app registration after a same-user sign-in"
```

---

### Task 7: Add account and Change app registration UI

**Files:**
- Modify: `macos/Sources/ElevateApp/Views/AddAccountView.swift`
- Create: `macos/Sources/ElevateApp/Views/ChangeRegistrationView.swift`
- Modify: `macos/Sources/ElevateApp/App/PanelRoute.swift`, `macos/Sources/ElevateApp/Views/RouteWindow.swift`
- Modify: `macos/Sources/ElevateApp/Views/IdentitySection.swift`

**Interfaces:**
- Consumes: `canPin`, `isAvailable`, `settingsRegistrationLabel`, `matchesSettingsClientId`, `rememberedPinnedClientId`, `isAccountBusy`, `changeSignInRegistration`, `ownAppViaLoopback`.
- Produces: `PanelRoute.changeRegistration(String)` (identity id).

- [ ] **Step 1: Routing.** Add `case changeRegistration(String)  // identity id` to `PanelRoute` and `case .changeRegistration(let identityId): ChangeRegistrationView(identityId: identityId)` to `RouteWindow`.

- [ ] **Step 2: AddAccountView.** Make these changes:

State and selection:

```swift
    private enum Choice: Hashable { case fixed(SignInMethod), custom }
    /// Which registration the "Entra app registration" row uses.
    private enum Registration: Hashable { case settings, different }

    @State private var choice: Choice?
    @State private var registration: Registration?
    @State private var customClientId = ""
    @State private var pinnedClientId = ""
    @State private var error: String?
    @State private var working = false

    private var methods: [SignInMethod] { model.availableMethods }
    /// The Entra row is usable through either registration.
    private func rowEnabled(_ m: SignInMethod) -> Bool {
        m == .ownApp ? (model.isAvailable(.ownApp) || model.canPin) : model.isAvailable(m)
    }
    private var selectedChoice: Choice {
        choice ?? methods.first { rowEnabled($0) }.map(Choice.fixed) ?? methods.first.map(Choice.fixed) ?? .custom
    }
    private var selectedRegistration: Registration {
        registration ?? (model.isAvailable(.ownApp) || !model.canPin ? .settings : .different)
    }
    private var selection: SignInMethod {
        switch selectedChoice {
        case .fixed(.ownApp) where selectedRegistration == .different: .pinned(pinnedClientId)
        case .fixed(let m): m
        case .custom: .custom(clientId: customClientId.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }
```

Body — the Entra row gets its own one-row radio group so the sub-options sit directly under it; both groups share the selection binding:

```swift
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if methods.contains(.ownApp) {
                methodPicker([.ownApp], includeCustom: false)
                if selectedChoice == .fixed(.ownApp) { registrationOptions.padding(.leading, 20) }
            }
            methodPicker(methods.filter { $0 != .ownApp }, includeCustom: model.isCustomMethodAllowed)
            if selectedChoice == .custom {
                ...existing custom TextField and GUID hint, unchanged...
            }
            limitations
            ...existing error and buttons, unchanged...
        }
        .padding(16).frame(width: 460)
        .navigationTitle("Add account")
        .onAppear {
            customClientId = model.rememberedCustomClientId
            pinnedClientId = model.rememberedPinnedClientId
        }
    }

    @ViewBuilder
    private func methodPicker(_ rows: [SignInMethod], includeCustom: Bool) -> some View {
        if !rows.isEmpty || includeCustom {
            Picker("", selection: Binding(get: { selectedChoice }, set: { choice = $0 })) {
                ForEach(rows, id: \.self) { m in
                    VStack(alignment: .leading, spacing: 1) {
                        Text(m.displayName)
                        Text(caption(for: m)).font(.caption).foregroundStyle(.secondary)
                    }
                    .tag(Choice.fixed(m))
                    .disabled(!rowEnabled(m))
                }
                if includeCustom {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Company app (client ID)")
                        Text("A registration that lists only http://localhost, such as an existing company PIM app; signs in through the browser")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    .tag(Choice.custom)
                }
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()
        }
    }

    @ViewBuilder private var registrationOptions: some View {
        VStack(alignment: .leading, spacing: 6) {
            Picker("", selection: Binding(get: { selectedRegistration }, set: { registration = $0 })) {
                Text("Use the registration in Settings (\(model.settingsRegistrationLabel))")
                    .tag(Registration.settings)
                    .disabled(!model.isAvailable(.ownApp))
                if model.canPin {
                    Text("Use a different registration").tag(Registration.different)
                }
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()
            if selectedRegistration == .different {
                VStack(alignment: .leading, spacing: 4) {
                    TextField("Application (client) ID", text: $pinnedClientId)
                        .textFieldStyle(.roundedBorder)
                        .font(.body.monospaced())
                    if !pinnedClientId.isEmpty, !model.isAvailable(selection) {
                        Text("Enter the application (client) ID as a GUID").font(.caption).foregroundStyle(.orange)
                    } else if model.matchesSettingsClientId(pinnedClientId) {
                        Text("Matches the registration in Settings; this account keeps this ID even if Settings changes.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Text(model.ownAppViaLoopback
                         ? "Needs the same setup as the Elevate app: http://localhost under Mobile and desktop applications on this unsigned build, the Graph PIM scopes, and admin consent."
                         : "Needs the same setup as the Elevate app: redirect \(AppSettings.redirectUri) under Mobile and desktop applications, the Graph PIM scopes, and admin consent.")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                .padding(.leading, 20)
            }
        }
    }
```

Replace the static `caption(for:available:viaLoopback:)` with an instance method:

```swift
    private func caption(for method: SignInMethod) -> String {
        switch method {
        case .ownApp, .pinnedApp:
            rowEnabled(.ownApp)
                ? "Full Entra, Azure and Groups support; needs admin consent in each tenant"
                : "Unavailable — configure a client ID in Settings"
        case .azureCLI:
            "Microsoft's Azure CLI app; no consent needed; Azure resource roles only"
        case .azurePowerShell:
            "Azure resource roles only; for tenants that block the Azure CLI app"
        case .custom:
            "A registration that lists only http://localhost, such as an existing company PIM app; signs in through the browser"
        }
    }
```

Update the file's doc comment: "The Entra app registration row uses the Settings registration or one the account keeps for itself; the two first-party rows work out of the box through the loopback browser flow."

- [ ] **Step 3: ChangeRegistrationView.** Create `ChangeRegistrationView.swift`:

```swift
import SwiftUI
import ElevateCore

/// Moves an Entra app registration account between the Settings registration and one of its
/// own. The switch is saved only after the same account signs in with the new registration.
struct ChangeRegistrationView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let identityId: String

    private enum Registration: Hashable { case settings, different }

    @State private var registration: Registration?
    @State private var clientId = ""
    @State private var error: String?
    @State private var working = false

    private var identity: Identity? { model.identity(identityId) }
    private var chosen: Registration {
        registration ?? (identity?.signInMethod.isPinned == true ? .different : .settings)
    }
    private var target: SignInMethod { chosen == .settings ? .ownApp : .pinned(clientId) }
    private var canContinue: Bool {
        guard let identity, !working else { return false }
        return target != identity.signInMethod && model.isAvailable(target) && !model.isAccountBusy(identityId)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Elevate signs \(identity?.upn ?? identityId) in with the registration you choose and keeps its tenants, roles and profiles. Nothing changes if the sign-in is cancelled or a different account signs in.")
                .font(.callout).fixedSize(horizontal: false, vertical: true)
            Picker("", selection: Binding(get: { chosen }, set: { registration = $0 })) {
                Text("Follow the registration in Settings (\(model.settingsRegistrationLabel))")
                    .tag(Registration.settings)
                    .disabled(!model.isAvailable(.ownApp))
                Text("Use a different registration").tag(Registration.different)
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()
            if chosen == .different {
                TextField("Application (client) ID", text: $clientId)
                    .textFieldStyle(.roundedBorder)
                    .font(.body.monospaced())
                    .padding(.leading, 20)
                if !clientId.isEmpty, !AppSettings.isValidClientId(clientId) {
                    Text("Enter the application (client) ID as a GUID").font(.caption).foregroundStyle(.orange).padding(.leading, 20)
                }
            }
            if model.isAccountBusy(identityId) {
                Text("Wait for this account's requests to finish.").font(.caption).foregroundStyle(.orange)
            }
            if let error { Text(error).font(.caption).foregroundStyle(.red).textSelection(.enabled) }
            HStack {
                if working { ProgressView().controlSize(.small) }
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction).disabled(working)
                Button("Sign in and switch") { Task { await apply() } }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                    .disabled(!canContinue)
            }
        }
        .padding(16).frame(width: 440)
        .navigationTitle("App registration for \(identity?.upn ?? identityId)")
        .onAppear {
            if case .pinnedApp(let id) = identity?.signInMethod { clientId = id }
            else { clientId = model.rememberedPinnedClientId }
        }
    }

    private func apply() async {
        guard let identity else { return }
        working = true
        defer { working = false }
        error = nil
        let previousNotice = model.notice
        if await model.changeSignInRegistration(identity, to: target) {
            dismiss()
        } else {
            error = model.notice
            model.notice = previousNotice
        }
    }
}
```

- [ ] **Step 4: IdentitySection.** Line 39: use `identity.signInMethod.detailedName`. In the `HeaderMenu`, after "Add tenant…":

```swift
                if identity.signInMethod.isOwnApp {
                    Button("Change app registration…") { open(.changeRegistration(identity.id)) }
                        .disabled(!model.canPin || model.isAccountBusy(identity.id))
                }
```

- [ ] **Step 5: Build and test**

Run the macOS app command (it regenerates the project so the new file is included). Expected: build succeeds, all tests pass.

- [ ] **Step 6: Visual check.** Render `AddAccountView` (with "Use a different registration" selected) and `ChangeRegistrationView` offscreen using the hosted-test pattern in memory note `elevate-ui-screenshots` (temporary test, not committed), look at both images, fix clipping or alignment, then delete the temporary test.

- [ ] **Step 7: Commit**

```bash
cd /Users/frode.hus/pimtray-pinned
git add macos/Sources/ElevateApp/App/PanelRoute.swift macos/Sources/ElevateApp/Views
git commit -m "macOS: choose or change an account's own app registration"
```

---

### Task 8: Documentation and changelog

**Files:**
- Modify: `docs/getting-started.md`, `docs/entra-app-registration.md`, `CHANGELOG.md`

- [ ] **Step 1:** In `docs/entra-app-registration.md`, add a section "Using a second registration for some accounts": when to use it (a tenant with its own copy of the Elevate registration), the requirements (same scopes, redirect `msauth.no.reothor.elevate://auth` under Mobile and desktop applications, `http://localhost` for unsigned builds, admin consent), where to choose it (Add account → Entra app registration → Use a different registration), how to change it later (account menu → Change app registration…), and that it is hidden when the organization manages the client ID. Also state that the Windows app and CLI do not support these accounts yet.

- [ ] **Step 2:** In `docs/getting-started.md`, where Add account's methods are described, add one sentence for "Use a different registration" linking to the new section, and note that changing the Settings client ID now keeps accounts and asks them to sign in again.

- [ ] **Step 3:** Add an "Unreleased" entry to `CHANGELOG.md` following its existing format: accounts can use their own Entra app registration (macOS); changing the Settings client ID keeps accounts; Windows and CLI show a "not supported yet" message for such accounts.

- [ ] **Step 4: Commit**

```bash
cd /Users/frode.hus/pimtray-pinned
git add docs/getting-started.md docs/entra-app-registration.md CHANGELOG.md
git commit -m "Docs: accounts with their own app registration"
```

---

### Task 9: Final verification and hand-off

- [ ] **Step 1:** Run every test command in Global Constraints; all must pass.
- [ ] **Step 2:** `grep -rn "== .ownApp\|!= .ownApp" macos/Sources` and confirm each remaining site means "follows Settings" (expected: `applyClientId`, `ownAppIdentityCount`, `bootstrap` MSAL reconciliation, `adminConsentURL`, `effectiveClientId`, `retrySignIn` notice, AddAccountView).
- [ ] **Step 3:** Ask the user before pushing, opening the PR, and creating the two parity issues (Windows app: WAM provider per client ID, Add account option, Change app registration; CLI: `elevate accounts add --client-id`, `elevate accounts set-client-id`). Manual check to list in the PR: a copy of the shared registration in a test tenant on a signed build (MSAL) and an unsigned build (loopback): add, refresh, activate, change registration, change the Settings ID.
