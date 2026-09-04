# PimTray Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A macOS 26 menu bar app that signs in Entra identities, lists each identity's tenants and eligible Entra directory PIM roles, and activates, bulk-activates, and deactivates them with countdowns and expiry notifications.

**Architecture:** A UI-free Swift package `PimTrayCore` holds models, an `HTTPClient` and `TokenProviding` abstraction, the `EntraDirectoryProvider` (Microsoft Graph), a manual role source, persistence, tenant discovery, and an `ActivationCoordinator`. An XcodeGen-generated app target `PimTrayApp` adds MSAL, an `@Observable` `AppModel`, and SwiftUI `MenuBarExtra` views. Azure resource and group providers are stubs behind the same protocol for phases 2 and 3.

**Tech Stack:** Swift 6.2 toolchain (Xcode 26.6), SwiftUI `MenuBarExtra(.window)`, Observation, Swift Testing, MSAL for iOS and macOS 2.15.0 (SPM), XcodeGen 2.46.0, Microsoft Graph v1.0, Azure Resource Manager `tenants` API.

**Spec:** `docs/superpowers/specs/2026-09-04-pimtray-design.md`

## Global Constraints

- Minimum deployment target: macOS 26.0. Swift language mode 6, strict concurrency complete.
- `PimTrayCore` never imports MSAL, AppKit, or SwiftUI. Only Foundation.
- Bundle ID `no.frodehus.pimtray`; redirect URI `msauth.no.frodehus.pimtray://auth`; keychain group `com.microsoft.identity.universalstorage`; development team `VLJKN96D7N`.
- Client ID is read at runtime from `PimTrayConfig.plist` (git-ignored); `PimTrayConfig.plist.example` is committed.
- Graph delegated scopes (fully qualified): `https://graph.microsoft.com/User.Read`, `https://graph.microsoft.com/RoleEligibilitySchedule.Read.Directory`, `https://graph.microsoft.com/RoleAssignmentSchedule.ReadWrite.Directory`, `https://graph.microsoft.com/RoleManagementPolicy.Read.Directory`. ARM scope: `https://management.azure.com/user_impersonation`.
- Manual-role default policy: default duration 1 h, maximum 8 h, justification required, no ticket, no approval.
- Remembered justification and last duration are keyed per `RoleKey` (identity + tenant + scope).
- Expiry notification fires 5 minutes before `endDateTime`.
- Tests use Swift Testing (`import Testing`), never XCTest. Network is stubbed through the `HTTPClient` protocol (a deliberate simplification of the spec's `URLProtocol` wording: same isolation, less ceremony).
- Commit after every task with the message given in that task. Never commit `PimTrayConfig.plist` or `PimTray.xcodeproj`.

## Known risk carried into this plan

All four Graph PIM delegated scopes require admin consent. With the user's own app registration, a tenant that refuses consent blocks activation as well as discovery. The plan therefore (a) signs in with `User.Read` only and requests PIM scopes per tenant on first use, (b) maps consent failures to a "consent required" tenant state with an admin-consent URL the user can forward, and (c) keeps `TokenProviding` swappable so a later phase can add a loopback-OAuth provider using a first-party client ID. Manual-roles mode covers the real case where an admin consented to `RoleAssignmentSchedule.ReadWrite.Directory` but not `RoleEligibilitySchedule.Read.Directory`.

## File structure

```
Package.swift                                   SwiftPM: PimTrayCore + PimTrayCoreTests
project.yml                                     XcodeGen: PimTrayApp (depends on local package + MSAL)
PimTrayConfig.plist.example                     clientId placeholder
Scripts/update-role-catalogue.pl                already committed
Sources/PimTrayCore/
  Models/Identity.swift                         Identity, TenantKey, TenantContext
  Models/RoleScope.swift                        RoleScopeKind, GroupAccess, RoleScope, RoleKey
  Models/Roles.swift                            RolePolicy, EligibleRole, ActiveAssignment, ActivationRequest, TicketInfo
  Models/PIMError.swift                         PIMError
  Support/ISO8601Duration.swift                 PT8H <-> Duration
  Support/GraphJSON.swift                       tolerant date decoder/encoder
  Support/Countdown.swift                       remaining time + label
  Networking/HTTPClient.swift                   HTTPRequest, HTTPResponse, HTTPClient, URLSessionHTTPClient
  Networking/ClaimsChallenge.swift              parse WWW-Authenticate claims
  Auth/TokenProviding.swift                     TokenProviding, GraphScopes, ArmScopes, InteractionRetry
  Providers/PIMProvider.swift                   protocol
  Providers/GraphTransport.swift                authorized GET/POST + error mapping
  Providers/EntraDirectoryProvider.swift        phase 1 provider
  Providers/StubProviders.swift                 AzureResourceProvider, GroupProvider stubs
  Catalogue/RoleCatalogue.swift                 built-in roles JSON loader
  Catalogue/ManualRoleSource.swift              manual roles -> EligibleRole
  Storage/AppState.swift                        persisted state model
  Storage/AppStateStore.swift                   JSON file persistence
  Discovery/TenantDiscovery.swift               ARM tenants + domain resolution
  Coordination/ActivationCoordinator.swift      single/bulk activation, retries
  Resources/EntraBuiltInRoles.json              already committed
Tests/PimTrayCoreTests/
  Support/StubHTTPClient.swift, FakeTokenProvider.swift, Fixtures.swift
  ModelsTests.swift, ISO8601DurationTests.swift, ClaimsChallengeTests.swift,
  EntraDirectoryProviderTests.swift, RoleCatalogueTests.swift, AppStateStoreTests.swift,
  TenantDiscoveryTests.swift, ActivationCoordinatorTests.swift, CountdownTests.swift
Tests/PimTrayCoreTests/Fixtures/*.json
Sources/PimTrayApp/
  App/PimTrayApp.swift, App/AppDelegate.swift, App/AppConfig.swift, App/AppModel.swift, App/PanelRoute.swift
  MSAL/MSALTokenProvider.swift, MSAL/AuthAnchorWindow.swift
  Notifications/ExpiryNotifier.swift
  Views/PanelView.swift, Views/IdentitySection.swift, Views/TenantSection.swift, Views/RoleRow.swift,
  Views/ActivationView.swift, Views/ConfigureRolesView.swift, Views/TenantSheets.swift, Views/RouteWindow.swift
  Info.plist (generated by XcodeGen from project.yml), PimTray.entitlements (generated)
README.md
```

---

### Task 1: Package scaffold and toolchain

**Files:**
- Create: `Package.swift`, `Sources/PimTrayCore/PimTrayCore.swift`, `Tests/PimTrayCoreTests/SmokeTests.swift`, `PimTrayConfig.plist.example`
- Modify: `.gitignore`

**Interfaces:**
- Produces: `PimTrayCore` library target and `PimTrayCoreTests` test target that `swift test` runs.

- [ ] **Step 1: Install XcodeGen**

```bash
brew install xcodegen
xcodegen --version
```
Expected: `Version: 2.46.0` (or newer).

- [ ] **Step 2: Write Package.swift**

```swift
// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "PimTray",
    platforms: [.macOS(.v26)],
    products: [
        .library(name: "PimTrayCore", targets: ["PimTrayCore"]),
    ],
    targets: [
        .target(
            name: "PimTrayCore",
            path: "Sources/PimTrayCore",
            resources: [.process("Resources")],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "PimTrayCoreTests",
            dependencies: ["PimTrayCore"],
            path: "Tests/PimTrayCoreTests",
            resources: [.copy("Fixtures")],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
    ]
)
```

- [ ] **Step 3: Write a placeholder source and smoke test**

`Sources/PimTrayCore/PimTrayCore.swift`:
```swift
public enum PimTrayCore {
    public static let version = "0.1.0"
}
```

`Tests/PimTrayCoreTests/SmokeTests.swift`:
```swift
import Testing
@testable import PimTrayCore

@Test func packageBuilds() {
    #expect(PimTrayCore.version == "0.1.0")
}
```

Create an empty fixtures directory so the resource declaration resolves:
```bash
mkdir -p Tests/PimTrayCoreTests/Fixtures && touch Tests/PimTrayCoreTests/Fixtures/.keep
```

- [ ] **Step 4: Write the config example and gitignore**

`PimTrayConfig.plist.example`:
```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>ClientId</key>
    <string>00000000-0000-0000-0000-000000000000</string>
</dict>
</plist>
```

Append to `.gitignore`:
```
.swiftpm/
*.xcworkspace/
```

- [ ] **Step 5: Run the tests**

```bash
swift test 2>&1 | tail -5
```
Expected: `Test run with 1 test passed`.

- [ ] **Step 6: Commit**

```bash
git add Package.swift Sources Tests PimTrayConfig.plist.example .gitignore
git commit -m "Scaffold PimTrayCore package"
```

---

### Task 2: Core models

**Files:**
- Create: `Sources/PimTrayCore/Models/Identity.swift`, `Sources/PimTrayCore/Models/RoleScope.swift`, `Sources/PimTrayCore/Models/Roles.swift`, `Sources/PimTrayCore/Models/PIMError.swift`
- Test: `Tests/PimTrayCoreTests/ModelsTests.swift`

**Interfaces:**
- Produces: every type below, all `Codable, Hashable, Sendable`.

- [ ] **Step 1: Write the failing tests**

```swift
import Testing
import Foundation
@testable import PimTrayCore

@Suite struct ModelsTests {
    @Test func roleKeyDistinguishesTenants() {
        let scope = RoleScope.entraDirectory(roleDefinitionId: "abc", directoryScopeId: "/")
        let a = RoleKey(identityId: "id1", tenantId: "t1", scope: scope)
        let b = RoleKey(identityId: "id1", tenantId: "t2", scope: scope)
        #expect(a != b)
        #expect(a.tenantKey == TenantKey(identityId: "id1", tenantId: "t1"))
    }

    @Test func roleScopeRoundTripsThroughJSON() throws {
        let scopes: [RoleScope] = [
            .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"),
            .azureResource(scope: "/subscriptions/s", roleDefinitionId: "d"),
            .group(groupId: "g", accessId: .owner),
        ]
        let data = try JSONEncoder().encode(scopes)
        let back = try JSONDecoder().decode([RoleScope].self, from: data)
        #expect(back == scopes)
        #expect(scopes.map(\.kind) == [.entraDirectory, .azureResource, .group])
    }

    @Test func manualPolicyDefaults() {
        let p = RolePolicy.manualDefault
        #expect(p.defaultDuration == .seconds(3600))
        #expect(p.maximumDuration == .seconds(8 * 3600))
        #expect(p.requiresJustification)
        #expect(!p.requiresTicket)
        #expect(!p.requiresApproval)
    }

    @Test func activeAssignmentStatusRoundTrips() throws {
        let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
        let a = ActiveAssignment(roleKey: key, assignmentId: "x", startDateTime: Date(timeIntervalSince1970: 0), endDateTime: nil, status: .failed("boom"))
        let data = try JSONEncoder().encode(a)
        #expect(try JSONDecoder().decode(ActiveAssignment.self, from: data) == a)
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
swift test 2>&1 | grep -E "error:|passed|failed" | head
```
Expected: compile errors, `RoleScope` not found.

- [ ] **Step 3: Write the models**

`Sources/PimTrayCore/Models/Identity.swift`:
```swift
import Foundation

/// A signed-in Entra user. `id` is MSAL's home account identifier.
public struct Identity: Codable, Hashable, Sendable, Identifiable {
    public let id: String
    public var upn: String
    public var displayName: String
    public var homeTenantId: String

    public init(id: String, upn: String, displayName: String, homeTenantId: String) {
        self.id = id
        self.upn = upn
        self.displayName = displayName
        self.homeTenantId = homeTenantId
    }
}

public struct TenantKey: Codable, Hashable, Sendable {
    public let identityId: String
    public let tenantId: String
    public init(identityId: String, tenantId: String) {
        self.identityId = identityId
        self.tenantId = tenantId
    }
}

/// One tenant an identity can act in. The same identity may have many.
public struct TenantContext: Codable, Hashable, Sendable, Identifiable {
    public enum Source: String, Codable, Sendable { case home, discovered, manual }
    public enum DiscoveryMode: String, Codable, Sendable { case automatic, manualRoles }

    public var identityId: String
    public var tenantId: String
    public var displayName: String
    public var source: Source
    public var discoveryMode: DiscoveryMode
    /// Object id of the identity *inside this tenant* (guests differ per tenant).
    public var principalObjectId: String?
    public var lastDiscoveryError: String?

    public var id: TenantKey { TenantKey(identityId: identityId, tenantId: tenantId) }

    public init(identityId: String, tenantId: String, displayName: String, source: Source,
                discoveryMode: DiscoveryMode = .automatic, principalObjectId: String? = nil,
                lastDiscoveryError: String? = nil) {
        self.identityId = identityId
        self.tenantId = tenantId
        self.displayName = displayName
        self.source = source
        self.discoveryMode = discoveryMode
        self.principalObjectId = principalObjectId
        self.lastDiscoveryError = lastDiscoveryError
    }
}
```

`Sources/PimTrayCore/Models/RoleScope.swift`:
```swift
import Foundation

public enum RoleScopeKind: String, Codable, Hashable, Sendable, CaseIterable {
    case entraDirectory, azureResource, group
}

public enum GroupAccess: String, Codable, Hashable, Sendable { case member, owner }

public enum RoleScope: Codable, Hashable, Sendable {
    case entraDirectory(roleDefinitionId: String, directoryScopeId: String)
    case azureResource(scope: String, roleDefinitionId: String)
    case group(groupId: String, accessId: GroupAccess)

    public var kind: RoleScopeKind {
        switch self {
        case .entraDirectory: .entraDirectory
        case .azureResource: .azureResource
        case .group: .group
        }
    }
}

public struct RoleKey: Codable, Hashable, Sendable {
    public let identityId: String
    public let tenantId: String
    public let scope: RoleScope

    public init(identityId: String, tenantId: String, scope: RoleScope) {
        self.identityId = identityId
        self.tenantId = tenantId
        self.scope = scope
    }

    public var tenantKey: TenantKey { TenantKey(identityId: identityId, tenantId: tenantId) }
}
```

`Sources/PimTrayCore/Models/Roles.swift`:
```swift
import Foundation

public struct RolePolicy: Codable, Hashable, Sendable {
    public var defaultDuration: Duration
    public var maximumDuration: Duration
    public var requiresJustification: Bool
    public var requiresTicket: Bool
    public var requiresMFA: Bool
    public var requiresApproval: Bool

    public init(defaultDuration: Duration, maximumDuration: Duration, requiresJustification: Bool,
                requiresTicket: Bool, requiresMFA: Bool, requiresApproval: Bool) {
        self.defaultDuration = defaultDuration
        self.maximumDuration = maximumDuration
        self.requiresJustification = requiresJustification
        self.requiresTicket = requiresTicket
        self.requiresMFA = requiresMFA
        self.requiresApproval = requiresApproval
    }

    public static let manualDefault = RolePolicy(
        defaultDuration: .seconds(3600), maximumDuration: .seconds(8 * 3600),
        requiresJustification: true, requiresTicket: false, requiresMFA: false, requiresApproval: false)
}

public enum RoleSource: String, Codable, Hashable, Sendable { case discovered, manual }

public struct EligibleRole: Codable, Hashable, Sendable, Identifiable {
    public let key: RoleKey
    public var displayName: String
    public var source: RoleSource
    public var policy: RolePolicy
    public var id: RoleKey { key }

    public init(key: RoleKey, displayName: String, source: RoleSource, policy: RolePolicy) {
        self.key = key
        self.displayName = displayName
        self.source = source
        self.policy = policy
    }
}

public struct ActiveAssignment: Codable, Hashable, Sendable, Identifiable {
    public enum Status: Codable, Hashable, Sendable {
        case active, pendingApproval, pendingProvisioning, failed(String)
    }
    public let roleKey: RoleKey
    public var assignmentId: String?
    public var startDateTime: Date
    public var endDateTime: Date?
    public var status: Status
    public var id: RoleKey { roleKey }

    public init(roleKey: RoleKey, assignmentId: String?, startDateTime: Date, endDateTime: Date?, status: Status) {
        self.roleKey = roleKey
        self.assignmentId = assignmentId
        self.startDateTime = startDateTime
        self.endDateTime = endDateTime
        self.status = status
    }
}

public struct TicketInfo: Codable, Hashable, Sendable {
    public var number: String
    public var system: String
    public init(number: String, system: String) { self.number = number; self.system = system }
}

public struct ActivationRequest: Codable, Hashable, Sendable, Identifiable {
    public let roleKey: RoleKey
    public var duration: Duration
    public var justification: String
    public var ticket: TicketInfo?
    public var id: RoleKey { roleKey }

    public init(roleKey: RoleKey, duration: Duration, justification: String, ticket: TicketInfo? = nil) {
        self.roleKey = roleKey
        self.duration = duration
        self.justification = justification
        self.ticket = ticket
    }
}
```

`Sources/PimTrayCore/Models/PIMError.swift`:
```swift
import Foundation

public enum PIMError: Error, Hashable, Sendable {
    /// Tenant has not consented to a required scope (Graph 403 or AADSTS65001).
    case consentRequired
    /// Silent token acquisition failed; the caller must run an interactive flow.
    case interactionRequired
    /// Resource returned a claims challenge; payload is the decoded claims JSON.
    case claimsChallenge(String)
    case notEligible
    case policyViolation(String)
    case pendingApproval
    case network(String)
    case unexpected(status: Int, body: String)

    public var userMessage: String {
        switch self {
        case .consentRequired: "Admin consent required for this tenant"
        case .interactionRequired: "Sign in again"
        case .claimsChallenge: "Multi-factor authentication required"
        case .notEligible: "Not eligible for this role"
        case .policyViolation(let m): m
        case .pendingApproval: "Awaiting approval"
        case .network(let m): "Network error: \(m)"
        case .unexpected(let s, _): "Unexpected response (\(s))"
        }
    }
}
```

- [ ] **Step 4: Run tests**

```bash
swift test 2>&1 | tail -3
```
Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add Sources/PimTrayCore/Models Tests/PimTrayCoreTests/ModelsTests.swift
git commit -m "Add core domain models"
```

---

### Task 3: Duration parsing, tolerant JSON dates, countdown

**Files:**
- Create: `Sources/PimTrayCore/Support/ISO8601Duration.swift`, `Sources/PimTrayCore/Support/GraphJSON.swift`, `Sources/PimTrayCore/Support/Countdown.swift`
- Test: `Tests/PimTrayCoreTests/ISO8601DurationTests.swift`, `Tests/PimTrayCoreTests/CountdownTests.swift`

**Interfaces:**
- Produces: `ISO8601Duration.parse(_:) -> Duration?`, `ISO8601Duration.format(_:) -> String`, `GraphJSON.decoder`, `GraphJSON.encoder`, `Countdown.remaining(until:now:) -> Duration?`, `Countdown.label(_:) -> String`.

- [ ] **Step 1: Write the failing tests**

`Tests/PimTrayCoreTests/ISO8601DurationTests.swift`:
```swift
import Testing
import Foundation
@testable import PimTrayCore

@Suite struct ISO8601DurationTests {
    @Test func parsesHoursMinutes() {
        #expect(ISO8601Duration.parse("PT8H") == .seconds(8 * 3600))
        #expect(ISO8601Duration.parse("PT30M") == .seconds(1800))
        #expect(ISO8601Duration.parse("PT1H30M") == .seconds(5400))
        #expect(ISO8601Duration.parse("P1D") == .seconds(86400))
        #expect(ISO8601Duration.parse("garbage") == nil)
    }

    @Test func formatsAsHoursAndMinutes() {
        #expect(ISO8601Duration.format(.seconds(8 * 3600)) == "PT8H")
        #expect(ISO8601Duration.format(.seconds(1800)) == "PT30M")
        #expect(ISO8601Duration.format(.seconds(5400)) == "PT1H30M")
    }

    @Test func decoderAcceptsFractionalAndPlainDates() throws {
        struct Box: Decodable { let d: Date }
        let plain = try GraphJSON.decoder.decode(Box.self, from: Data(#"{"d":"2026-09-04T08:00:00Z"}"#.utf8))
        let frac = try GraphJSON.decoder.decode(Box.self, from: Data(#"{"d":"2026-09-04T08:00:00.1234567Z"}"#.utf8))
        #expect(abs(plain.d.timeIntervalSince(frac.d)) < 1)
    }
}
```

`Tests/PimTrayCoreTests/CountdownTests.swift`:
```swift
import Testing
import Foundation
@testable import PimTrayCore

@Suite struct CountdownTests {
    let now = Date(timeIntervalSince1970: 1_000_000)

    @Test func remainingIsNilWhenExpired() {
        #expect(Countdown.remaining(until: now.addingTimeInterval(-1), now: now) == nil)
    }

    @Test func labelFormatsHoursMinutes() {
        #expect(Countdown.label(.seconds(2 * 3600 + 41 * 60 + 10)) == "02:41")
        #expect(Countdown.label(.seconds(59)) == "00:00")
        #expect(Countdown.label(.seconds(5 * 60)) == "00:05")
    }

    @Test func remainingRoundsDownToSeconds() {
        let r = Countdown.remaining(until: now.addingTimeInterval(125.9), now: now)
        #expect(r == .seconds(125))
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
swift test 2>&1 | grep -E "error:" | head -3
```
Expected: `ISO8601Duration` and `Countdown` not found.

- [ ] **Step 3: Implement**

`Sources/PimTrayCore/Support/ISO8601Duration.swift`:
```swift
import Foundation

public enum ISO8601Duration {
    /// Parses `PnDTnHnMnS` (days, hours, minutes, seconds). Weeks, months, years are not supported by Graph PIM.
    public static func parse(_ text: String) -> Duration? {
        let pattern = /^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?)?$/
        guard let m = text.wholeMatch(of: pattern) else { return nil }
        let days = Int(m.1 ?? "0") ?? 0
        let hours = Int(m.2 ?? "0") ?? 0
        let minutes = Int(m.3 ?? "0") ?? 0
        let seconds = Double(m.4 ?? "0") ?? 0
        let total = Double(days * 86400 + hours * 3600 + minutes * 60) + seconds
        guard total > 0 || text == "PT0S" else { return nil }
        return .seconds(total)
    }

    /// Formats whole hours and minutes, e.g. `PT1H30M`. Seconds are dropped.
    public static func format(_ duration: Duration) -> String {
        let totalMinutes = Int(duration.components.seconds / 60)
        let h = totalMinutes / 60, m = totalMinutes % 60
        var out = "PT"
        if h > 0 { out += "\(h)H" }
        if m > 0 || h == 0 { out += "\(m)M" }
        return out
    }
}
```

`Sources/PimTrayCore/Support/GraphJSON.swift`:
```swift
import Foundation

public enum GraphJSON {
    nonisolated(unsafe) private static let fractional: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    nonisolated(unsafe) private static let plain: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    public static func parseDate(_ s: String) -> Date? {
        // Graph emits up to 7 fractional digits; ISO8601DateFormatter accepts at most 3, so trim.
        let trimmed = s.replacing(/\.(\d{3})\d+/) { "." + String($0.output.1) }
        return fractional.date(from: trimmed) ?? plain.date(from: trimmed)
    }

    public static var decoder: JSONDecoder {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .custom { decoder in
            let s = try decoder.singleValueContainer().decode(String.self)
            guard let date = parseDate(s) else {
                throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Bad date \(s)"))
            }
            return date
        }
        return d
    }

    public static var encoder: JSONEncoder {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .custom { date, encoder in
            var c = encoder.singleValueContainer()
            try c.encode(plain.string(from: date))
        }
        return e
    }
}
```

`Sources/PimTrayCore/Support/Countdown.swift`:
```swift
import Foundation

public enum Countdown {
    /// Whole seconds until `end`, or nil once it has passed.
    public static func remaining(until end: Date, now: Date = .now) -> Duration? {
        let secs = end.timeIntervalSince(now)
        guard secs > 0 else { return nil }
        return .seconds(Int(secs.rounded(.down)))
    }

    /// `HH:MM`, floored to the minute.
    public static func label(_ d: Duration) -> String {
        let total = Int(d.components.seconds)
        return String(format: "%02d:%02d", total / 3600, (total % 3600) / 60)
    }
}
```

- [ ] **Step 4: Run tests**

```bash
swift test 2>&1 | tail -3
```
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Sources/PimTrayCore/Support Tests/PimTrayCoreTests/ISO8601DurationTests.swift Tests/PimTrayCoreTests/CountdownTests.swift
git commit -m "Add duration parsing, tolerant JSON dates and countdown helpers"
```

---

### Task 4: HTTP client, claims challenge, token provider protocol, test doubles

**Files:**
- Create: `Sources/PimTrayCore/Networking/HTTPClient.swift`, `Sources/PimTrayCore/Networking/ClaimsChallenge.swift`, `Sources/PimTrayCore/Auth/TokenProviding.swift`
- Create: `Tests/PimTrayCoreTests/Support/StubHTTPClient.swift`, `Tests/PimTrayCoreTests/Support/FakeTokenProvider.swift`, `Tests/PimTrayCoreTests/Support/Fixtures.swift`
- Test: `Tests/PimTrayCoreTests/ClaimsChallengeTests.swift`

**Interfaces:**
- Produces:
  - `struct HTTPRequest { method, url, headers, body }`, `struct HTTPResponse { status, headers, body; header(_:) }`, `protocol HTTPClient { func send(_:) async throws -> HTTPResponse }`, `URLSessionHTTPClient`.
  - `ClaimsChallenge.parse(wwwAuthenticate:) -> String?`
  - `protocol TokenProviding` with `signIn()`, `signOut(_:)`, `identities()`, `accessToken(identity:tenantId:scopes:)`, `acquireInteractively(identity:tenantId:scopes:claims:)`.
  - `GraphScopes.all`, `ArmScopes.all`, `InteractionRetry.run(...)`.
  - Test doubles `StubHTTPClient` (actor), `FakeTokenProvider` (actor), `Fixtures.data(_:)`.

- [ ] **Step 1: Write the failing test**

`Tests/PimTrayCoreTests/ClaimsChallengeTests.swift`:
```swift
import Testing
import Foundation
@testable import PimTrayCore

@Suite struct ClaimsChallengeTests {
    @Test func extractsAndDecodesClaims() {
        let json = #"{"access_token":{"acrs":{"essential":true,"values":["c1"]}}}"#
        let b64 = Data(json.utf8).base64EncodedString().trimmingCharacters(in: CharacterSet(charactersIn: "="))
        let header = #"Bearer realm="", authorization_uri="https://login.microsoftonline.com/common/oauth2/authorize", error="insufficient_claims", claims="\#(b64)""#
        #expect(ClaimsChallenge.parse(wwwAuthenticate: header) == json)
    }

    @Test func returnsNilWithoutClaims() {
        #expect(ClaimsChallenge.parse(wwwAuthenticate: #"Bearer realm="""#) == nil)
    }

    @Test func interactionRetryRunsInteractiveThenRetries() async throws {
        let tokens = FakeTokenProvider()
        let identity = Identity(id: "i", upn: "u@x", displayName: "U", homeTenantId: "t")
        var calls = 0
        let result = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: "t", scopes: GraphScopes.all) {
            calls += 1
            if calls == 1 { throw PIMError.claimsChallenge("{}") }
            return "ok"
        }
        #expect(result == "ok")
        #expect(calls == 2)
        #expect(await tokens.interactiveCalls.count == 1)
        #expect(await tokens.interactiveCalls.first?.claims == "{}")
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
swift test 2>&1 | grep -E "error:" | head -3
```
Expected: `ClaimsChallenge`, `FakeTokenProvider` not found.

- [ ] **Step 3: Implement networking and auth protocols**

`Sources/PimTrayCore/Networking/HTTPClient.swift`:
```swift
import Foundation

public struct HTTPRequest: Sendable, Hashable {
    public var method: String
    public var url: URL
    public var headers: [String: String]
    public var body: Data?

    public init(method: String, url: URL, headers: [String: String] = [:], body: Data? = nil) {
        self.method = method
        self.url = url
        self.headers = headers
        self.body = body
    }
}

public struct HTTPResponse: Sendable {
    public let status: Int
    public let headers: [String: String]
    public let body: Data

    public init(status: Int, headers: [String: String] = [:], body: Data = Data()) {
        self.status = status
        self.headers = headers
        self.body = body
    }

    public func header(_ name: String) -> String? {
        headers.first { $0.key.caseInsensitiveCompare(name) == .orderedSame }?.value
    }

    public var bodyText: String { String(decoding: body, as: UTF8.self) }
}

public protocol HTTPClient: Sendable {
    func send(_ request: HTTPRequest) async throws -> HTTPResponse
}

public final class URLSessionHTTPClient: HTTPClient {
    private let session: URLSession
    public init(session: URLSession = .shared) { self.session = session }

    public func send(_ request: HTTPRequest) async throws -> HTTPResponse {
        var req = URLRequest(url: request.url)
        req.httpMethod = request.method
        req.httpBody = request.body
        for (k, v) in request.headers { req.setValue(v, forHTTPHeaderField: k) }
        do {
            let (data, resp) = try await session.data(for: req)
            guard let http = resp as? HTTPURLResponse else { throw PIMError.network("non-HTTP response") }
            var headers: [String: String] = [:]
            for (k, v) in http.allHeaderFields { headers["\(k)"] = "\(v)" }
            return HTTPResponse(status: http.statusCode, headers: headers, body: data)
        } catch let e as PIMError {
            throw e
        } catch {
            throw PIMError.network(error.localizedDescription)
        }
    }
}
```

`Sources/PimTrayCore/Networking/ClaimsChallenge.swift`:
```swift
import Foundation

public enum ClaimsChallenge {
    /// Extracts the base64url `claims` parameter from a `WWW-Authenticate` header and returns the decoded JSON.
    public static func parse(wwwAuthenticate header: String) -> String? {
        guard let m = header.firstMatch(of: /claims="([^"]+)"/) else { return nil }
        var b64 = String(m.1).replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while b64.count % 4 != 0 { b64 += "=" }
        guard let data = Data(base64Encoded: b64) else { return nil }
        return String(decoding: data, as: UTF8.self)
    }
}
```

`Sources/PimTrayCore/Auth/TokenProviding.swift`:
```swift
import Foundation

public enum GraphScopes {
    public static let userRead = "https://graph.microsoft.com/User.Read"
    public static let all = [
        "https://graph.microsoft.com/User.Read",
        "https://graph.microsoft.com/RoleEligibilitySchedule.Read.Directory",
        "https://graph.microsoft.com/RoleAssignmentSchedule.ReadWrite.Directory",
        "https://graph.microsoft.com/RoleManagementPolicy.Read.Directory",
    ]
}

public enum ArmScopes {
    public static let all = ["https://management.azure.com/user_impersonation"]
}

public protocol TokenProviding: Sendable {
    /// Interactive sign-in against the `organizations` authority. Returns the new identity.
    func signIn() async throws -> Identity
    func signOut(_ identity: Identity) async throws
    func identities() async throws -> [Identity]
    /// Silent acquisition for `tenantId`. Throws `PIMError.interactionRequired` when a prompt is needed.
    func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String
    /// Interactive acquisition, optionally carrying a claims challenge. Throws `PIMError.consentRequired` on AADSTS65001.
    @discardableResult
    func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String
}

public enum InteractionRetry {
    /// Runs `operation`; on `interactionRequired` or `claimsChallenge` acquires a token interactively once and retries once.
    public static func run<T: Sendable>(
        tokens: any TokenProviding, identity: Identity, tenantId: String, scopes: [String],
        operation: () async throws -> T
    ) async throws -> T {
        do {
            return try await operation()
        } catch PIMError.interactionRequired {
            try await tokens.acquireInteractively(identity: identity, tenantId: tenantId, scopes: scopes, claims: nil)
            return try await operation()
        } catch PIMError.claimsChallenge(let claims) {
            try await tokens.acquireInteractively(identity: identity, tenantId: tenantId, scopes: scopes, claims: claims)
            return try await operation()
        }
    }
}
```

- [ ] **Step 4: Write the test doubles**

`Tests/PimTrayCoreTests/Support/StubHTTPClient.swift`:
```swift
import Foundation
@testable import PimTrayCore

/// Routes requests by HTTP method plus a substring of the URL. Records every request.
actor StubHTTPClient: HTTPClient {
    struct Route { let method: String; let urlContains: String; let respond: @Sendable (HTTPRequest) -> HTTPResponse }
    private var routes: [Route] = []
    private(set) var requests: [HTTPRequest] = []

    func on(_ method: String, _ urlContains: String, status: Int = 200, headers: [String: String] = [:], body: Data = Data()) {
        routes.append(Route(method: method, urlContains: urlContains) { _ in HTTPResponse(status: status, headers: headers, body: body) })
    }

    func on(_ method: String, _ urlContains: String, respond: @escaping @Sendable (HTTPRequest) -> HTTPResponse) {
        routes.append(Route(method: method, urlContains: urlContains, respond: respond))
    }

    func send(_ request: HTTPRequest) async throws -> HTTPResponse {
        requests.append(request)
        guard let route = routes.last(where: { $0.method == request.method && request.url.absoluteString.contains($0.urlContains) }) else {
            return HTTPResponse(status: 599, body: Data("no stub for \(request.method) \(request.url)".utf8))
        }
        return route.respond(request)
    }

    func requests(matching substring: String) -> [HTTPRequest] {
        requests.filter { $0.url.absoluteString.contains(substring) }
    }
}
```

`Tests/PimTrayCoreTests/Support/FakeTokenProvider.swift`:
```swift
import Foundation
@testable import PimTrayCore

actor FakeTokenProvider: TokenProviding {
    struct InteractiveCall: Equatable { let tenantId: String; let scopes: [String]; let claims: String? }
    var storedIdentities: [Identity] = []
    var silentError: PIMError?
    var interactiveError: PIMError?
    private(set) var interactiveCalls: [InteractiveCall] = []
    private(set) var silentCalls: [String] = []

    func setSilentError(_ e: PIMError?) { silentError = e }
    func setInteractiveError(_ e: PIMError?) { interactiveError = e }

    func signIn() async throws -> Identity {
        let i = Identity(id: "new", upn: "new@x", displayName: "New", homeTenantId: "home")
        storedIdentities.append(i)
        return i
    }
    func signOut(_ identity: Identity) async throws { storedIdentities.removeAll { $0.id == identity.id } }
    func identities() async throws -> [Identity] { storedIdentities }

    func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String {
        silentCalls.append(tenantId)
        if let silentError { throw silentError }
        return "token-\(tenantId)"
    }

    func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String {
        interactiveCalls.append(InteractiveCall(tenantId: tenantId, scopes: scopes, claims: claims))
        if let interactiveError { throw interactiveError }
        silentError = nil
        return "token-\(tenantId)"
    }
}
```

`Tests/PimTrayCoreTests/Support/Fixtures.swift`:
```swift
import Foundation

enum Fixtures {
    static func data(_ name: String) -> Data {
        let url = Bundle.module.url(forResource: name, withExtension: "json", subdirectory: "Fixtures")!
        return try! Data(contentsOf: url)
    }
}
```

- [ ] **Step 5: Run tests**

```bash
swift test 2>&1 | tail -3
```
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Sources/PimTrayCore/Networking Sources/PimTrayCore/Auth Tests/PimTrayCoreTests
git commit -m "Add HTTP client, claims challenge parsing, token provider protocol and test doubles"
```

---

### Task 5: PIMProvider protocol, Graph transport, Entra provider reads

**Files:**
- Create: `Sources/PimTrayCore/Providers/PIMProvider.swift`, `Sources/PimTrayCore/Providers/GraphTransport.swift`, `Sources/PimTrayCore/Providers/EntraDirectoryProvider.swift`, `Sources/PimTrayCore/Providers/StubProviders.swift`
- Create fixtures: `Tests/PimTrayCoreTests/Fixtures/entra-eligible.json`, `Tests/PimTrayCoreTests/Fixtures/entra-active.json`, `Tests/PimTrayCoreTests/Fixtures/entra-pending-requests.json`
- Test: `Tests/PimTrayCoreTests/EntraDirectoryProviderTests.swift`

**Interfaces:**
- Consumes: `HTTPClient`, `TokenProviding`, models, `GraphJSON`, `ClaimsChallenge`.
- Produces:
  - `protocol PIMProvider: Sendable { var kind: RoleScopeKind; var scopes: [String]; eligibleRoles(identity:tenant:), activeAssignments(identity:tenant:), policy(for:identity:), activate(_:identity:), deactivate(_:identity:) }`
  - `struct GraphTransport { init(http:tokens:); get(identity:tenantId:url:scopes:), post(identity:tenantId:url:scopes:body:) -> HTTPResponse; static func mapError(_:) -> PIMError }`
  - `struct EntraDirectoryProvider: PIMProvider { init(http:tokens:) }`
  - `struct AzureResourceProvider`, `struct GroupProvider` stubs that throw `PIMError.unexpected(status: 501, body: "phase 2/3")`.

- [ ] **Step 1: Write fixtures**

`Tests/PimTrayCoreTests/Fixtures/entra-eligible.json`:
```json
{
  "value": [
    {
      "id": "elig-1", "principalId": "user-obj-1",
      "roleDefinitionId": "f2ef992c-3afb-46b9-b7cf-a126ee74c451", "directoryScopeId": "/",
      "memberType": "Direct", "status": "Provisioned",
      "roleDefinition": { "id": "f2ef992c-3afb-46b9-b7cf-a126ee74c451", "displayName": "Global Reader" }
    },
    {
      "id": "elig-2", "principalId": "user-obj-1",
      "roleDefinitionId": "fe930be7-5e62-47db-91af-98c3a49a38b1", "directoryScopeId": "/",
      "memberType": "Group", "status": "Provisioned",
      "roleDefinition": { "id": "fe930be7-5e62-47db-91af-98c3a49a38b1", "displayName": "User Administrator" }
    }
  ]
}
```

`Tests/PimTrayCoreTests/Fixtures/entra-active.json`:
```json
{
  "value": [
    {
      "id": "inst-1", "principalId": "user-obj-1",
      "roleDefinitionId": "f2ef992c-3afb-46b9-b7cf-a126ee74c451", "directoryScopeId": "/",
      "assignmentType": "Activated", "memberType": "Direct",
      "startDateTime": "2026-09-04T08:00:00.1234567Z", "endDateTime": "2026-09-04T16:00:00Z",
      "roleDefinition": { "id": "f2ef992c-3afb-46b9-b7cf-a126ee74c451", "displayName": "Global Reader" }
    },
    {
      "id": "inst-2", "principalId": "user-obj-1",
      "roleDefinitionId": "62e90394-69f5-4237-9190-012177145e10", "directoryScopeId": "/",
      "assignmentType": "Assigned", "memberType": "Direct",
      "startDateTime": "2026-01-01T00:00:00Z", "endDateTime": null,
      "roleDefinition": { "id": "62e90394-69f5-4237-9190-012177145e10", "displayName": "Global Administrator" }
    }
  ]
}
```

`Tests/PimTrayCoreTests/Fixtures/entra-pending-requests.json`:
```json
{
  "value": [
    {
      "id": "req-9", "status": "PendingApproval", "action": "selfActivate",
      "principalId": "user-obj-1",
      "roleDefinitionId": "fe930be7-5e62-47db-91af-98c3a49a38b1", "directoryScopeId": "/",
      "createdDateTime": "2026-09-04T09:00:00Z",
      "scheduleInfo": { "startDateTime": "2026-09-04T09:00:00Z", "expiration": { "type": "afterDuration", "duration": "PT2H", "endDateTime": null } }
    },
    {
      "id": "req-8", "status": "Provisioned", "action": "selfActivate",
      "principalId": "user-obj-1",
      "roleDefinitionId": "f2ef992c-3afb-46b9-b7cf-a126ee74c451", "directoryScopeId": "/",
      "createdDateTime": "2026-09-04T08:00:00Z",
      "scheduleInfo": { "startDateTime": "2026-09-04T08:00:00Z", "expiration": { "type": "afterDuration", "duration": "PT8H", "endDateTime": null } }
    }
  ]
}
```

- [ ] **Step 2: Write the failing tests**

`Tests/PimTrayCoreTests/EntraDirectoryProviderTests.swift`:
```swift
import Testing
import Foundation
@testable import PimTrayCore

@Suite struct EntraDirectoryProviderTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t-home")
    let tenant = TenantContext(identityId: "id1", tenantId: "t1", displayName: "Contoso", source: .home)

    func makeProvider() -> (EntraDirectoryProvider, StubHTTPClient, FakeTokenProvider) {
        let http = StubHTTPClient()
        let tokens = FakeTokenProvider()
        return (EntraDirectoryProvider(http: http, tokens: tokens), http, tokens)
    }

    @Test func listsEligibleRolesWithBearerTokenForTenant() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilitySchedules/filterByCurrentUser", body: Fixtures.data("entra-eligible"))
        let roles = try await p.eligibleRoles(identity: identity, tenant: tenant)
        #expect(roles.map(\.displayName) == ["Global Reader", "User Administrator"])
        #expect(roles.allSatisfy { $0.source == .discovered && $0.key.tenantId == "t1" && $0.key.identityId == "id1" })
        #expect(roles[0].key.scope == .entraDirectory(roleDefinitionId: "f2ef992c-3afb-46b9-b7cf-a126ee74c451", directoryScopeId: "/"))
        let req = await http.requests.first!
        #expect(req.headers["Authorization"] == "Bearer token-t1")
        #expect(req.url.absoluteString.contains("$expand=roleDefinition"))
    }

    @Test func listsOnlyActivatedAssignmentsAndMergesPending() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleAssignmentScheduleInstances/filterByCurrentUser", body: Fixtures.data("entra-active"))
        await http.on("GET", "roleAssignmentScheduleRequests/filterByCurrentUser", body: Fixtures.data("entra-pending-requests"))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 2)
        let gr = active.first { $0.roleKey.scope == .entraDirectory(roleDefinitionId: "f2ef992c-3afb-46b9-b7cf-a126ee74c451", directoryScopeId: "/") }!
        #expect(gr.status == .active)
        #expect(gr.assignmentId == "inst-1")
        #expect(gr.endDateTime == GraphJSON.parseDate("2026-09-04T16:00:00Z"))
        let ua = active.first { $0.roleKey.scope == .entraDirectory(roleDefinitionId: "fe930be7-5e62-47db-91af-98c3a49a38b1", directoryScopeId: "/") }!
        #expect(ua.status == .pendingApproval)
        #expect(ua.assignmentId == "req-9")
    }

    @Test func forbiddenMapsToConsentRequired() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilitySchedules", status: 403, body: Data(#"{"error":{"code":"Authorization_RequestDenied"}}"#.utf8))
        await #expect(throws: PIMError.consentRequired) {
            _ = try await p.eligibleRoles(identity: identity, tenant: tenant)
        }
    }

    @Test func unauthorizedWithClaimsMapsToClaimsChallenge() async throws {
        let (p, http, _) = makeProvider()
        let claims = #"{"access_token":{"acrs":{"essential":true,"values":["c1"]}}}"#
        let b64 = Data(claims.utf8).base64EncodedString()
        await http.on("GET", "roleEligibilitySchedules", status: 401,
                      headers: ["WWW-Authenticate": #"Bearer error="insufficient_claims", claims="\#(b64)""#])
        await #expect(throws: PIMError.claimsChallenge(claims)) {
            _ = try await p.eligibleRoles(identity: identity, tenant: tenant)
        }
    }

    @Test func silentTokenFailurePropagatesInteractionRequired() async throws {
        let (p, _, tokens) = makeProvider()
        await tokens.setSilentError(.interactionRequired)
        await #expect(throws: PIMError.interactionRequired) {
            _ = try await p.eligibleRoles(identity: identity, tenant: tenant)
        }
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
swift test 2>&1 | grep -E "error:" | head -3
```
Expected: `EntraDirectoryProvider` not found.

- [ ] **Step 4: Implement the protocol, transport and provider**

`Sources/PimTrayCore/Providers/PIMProvider.swift`:
```swift
import Foundation

public protocol PIMProvider: Sendable {
    var kind: RoleScopeKind { get }
    /// Token scopes this provider needs; all against one resource.
    var scopes: [String] { get }
    func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole]
    func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment]
    func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy
    func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment
    func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws
}
```

`Sources/PimTrayCore/Providers/GraphTransport.swift`:
```swift
import Foundation

/// Sends authorized requests and maps non-success responses to `PIMError`.
public struct GraphTransport: Sendable {
    public static let graphBase = URL(string: "https://graph.microsoft.com/v1.0")!
    let http: any HTTPClient
    let tokens: any TokenProviding

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        self.http = http
        self.tokens = tokens
    }

    public func get(identity: Identity, tenantId: String, url: URL, scopes: [String]) async throws -> HTTPResponse {
        try await send(HTTPRequest(method: "GET", url: url), identity: identity, tenantId: tenantId, scopes: scopes)
    }

    public func post(identity: Identity, tenantId: String, url: URL, scopes: [String], body: Data) async throws -> HTTPResponse {
        try await send(HTTPRequest(method: "POST", url: url, headers: ["Content-Type": "application/json"], body: body),
                       identity: identity, tenantId: tenantId, scopes: scopes)
    }

    private func send(_ request: HTTPRequest, identity: Identity, tenantId: String, scopes: [String]) async throws -> HTTPResponse {
        var req = request
        let token = try await tokens.accessToken(identity: identity, tenantId: tenantId, scopes: scopes)
        req.headers["Authorization"] = "Bearer \(token)"
        req.headers["Accept"] = "application/json"
        let response = try await http.send(req)
        if (200..<300).contains(response.status) { return response }
        throw Self.mapError(response)
    }

    public static func mapError(_ r: HTTPResponse) -> PIMError {
        switch r.status {
        case 401:
            if let h = r.header("WWW-Authenticate"), let claims = ClaimsChallenge.parse(wwwAuthenticate: h) {
                return .claimsChallenge(claims)
            }
            return .interactionRequired
        case 403:
            return .consentRequired
        case 400:
            let text = r.bodyText
            if text.contains("RoleAssignmentRequestPolicyValidationFailed") || text.contains("RoleAssignmentRequestAcrsValidationFailed") {
                if text.contains("MfaRule") || text.contains("Acrs") {
                    // Graph did not hand us a claims header; ask the caller for a fresh interactive sign-in.
                    return .interactionRequired
                }
                return .policyViolation(Self.graphMessage(text) ?? "Policy validation failed")
            }
            if text.contains("RoleAssignmentDoesNotExist") || text.contains("RoleAssignmentRequestNotEligible") {
                return .notEligible
            }
            return .unexpected(status: 400, body: Self.graphMessage(text) ?? text)
        default:
            return .unexpected(status: r.status, body: Self.graphMessage(r.bodyText) ?? r.bodyText)
        }
    }

    static func graphMessage(_ body: String) -> String? {
        struct Envelope: Decodable { struct E: Decodable { let code: String?; let message: String? }; let error: E }
        return (try? JSONDecoder().decode(Envelope.self, from: Data(body.utf8)))?.error.message
    }
}
```

`Sources/PimTrayCore/Providers/EntraDirectoryProvider.swift`:
```swift
import Foundation

public struct EntraDirectoryProvider: PIMProvider {
    public let kind: RoleScopeKind = .entraDirectory
    public let scopes = GraphScopes.all
    let transport: GraphTransport

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        transport = GraphTransport(http: http, tokens: tokens)
    }

    // MARK: Wire models

    struct RoleDefinitionRef: Decodable { let id: String; let displayName: String? }
    struct Schedule: Decodable {
        let id: String
        let roleDefinitionId: String
        let directoryScopeId: String?
        let assignmentType: String?
        let startDateTime: Date?
        let endDateTime: Date?
        let roleDefinition: RoleDefinitionRef?
    }
    struct Expiration: Decodable { let type: String?; let duration: String?; let endDateTime: Date? }
    struct ScheduleInfo: Decodable { let startDateTime: Date?; let expiration: Expiration? }
    struct ScheduleRequest: Decodable {
        let id: String
        let status: String
        let roleDefinitionId: String
        let directoryScopeId: String?
        let createdDateTime: Date?
        let scheduleInfo: ScheduleInfo?
    }
    struct Collection<T: Decodable>: Decodable { let value: [T] }

    func url(_ path: String) -> URL {
        URL(string: GraphTransport.graphBase.absoluteString + path)!
    }

    // MARK: Reads

    public func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] {
        let r = try await transport.get(identity: identity, tenantId: tenant.tenantId,
                                        url: url("/roleManagement/directory/roleEligibilitySchedules/filterByCurrentUser(on='principal')?$expand=roleDefinition"),
                                        scopes: scopes)
        let items = try GraphJSON.decoder.decode(Collection<Schedule>.self, from: r.body).value
        var seen = Set<RoleScope>()
        var roles: [EligibleRole] = []
        for s in items {
            let scope = RoleScope.entraDirectory(roleDefinitionId: s.roleDefinitionId, directoryScopeId: s.directoryScopeId ?? "/")
            guard seen.insert(scope).inserted else { continue }
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: scope)
            roles.append(EligibleRole(key: key, displayName: s.roleDefinition?.displayName ?? s.roleDefinitionId,
                                      source: .discovered, policy: .manualDefault))
        }
        return roles.sorted { $0.displayName < $1.displayName }
    }

    public func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] {
        let instances = try await transport.get(identity: identity, tenantId: tenant.tenantId,
                                                url: url("/roleManagement/directory/roleAssignmentScheduleInstances/filterByCurrentUser(on='principal')?$expand=roleDefinition"),
                                                scopes: scopes)
        let requests = try await transport.get(identity: identity, tenantId: tenant.tenantId,
                                               url: url("/roleManagement/directory/roleAssignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter=status eq 'PendingApproval'"),
                                               scopes: scopes)
        let activated = try GraphJSON.decoder.decode(Collection<Schedule>.self, from: instances.body).value
            .filter { $0.assignmentType == "Activated" }
        let pending = try GraphJSON.decoder.decode(Collection<ScheduleRequest>.self, from: requests.body).value
            .filter { $0.status == "PendingApproval" }

        var result: [RoleKey: ActiveAssignment] = [:]
        for s in activated {
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId,
                              scope: .entraDirectory(roleDefinitionId: s.roleDefinitionId, directoryScopeId: s.directoryScopeId ?? "/"))
            result[key] = ActiveAssignment(roleKey: key, assignmentId: s.id, startDateTime: s.startDateTime ?? .now,
                                           endDateTime: s.endDateTime, status: .active)
        }
        for p in pending {
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId,
                              scope: .entraDirectory(roleDefinitionId: p.roleDefinitionId, directoryScopeId: p.directoryScopeId ?? "/"))
            guard result[key] == nil else { continue }
            result[key] = ActiveAssignment(roleKey: key, assignmentId: p.id,
                                           startDateTime: p.scheduleInfo?.startDateTime ?? p.createdDateTime ?? .now,
                                           endDateTime: nil, status: .pendingApproval)
        }
        return Array(result.values)
    }

    // Policy, activate and deactivate are implemented in Task 6.
    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy {
        throw PIMError.unexpected(status: 501, body: "policy: implemented in Task 6")
    }
    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        throw PIMError.unexpected(status: 501, body: "activate: implemented in Task 6")
    }
    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        throw PIMError.unexpected(status: 501, body: "deactivate: implemented in Task 6")
    }
}
```

`Sources/PimTrayCore/Providers/StubProviders.swift`:
```swift
import Foundation

/// Phase 2. Azure Resource Manager PIM.
public struct AzureResourceProvider: PIMProvider {
    public let kind: RoleScopeKind = .azureResource
    public let scopes = ArmScopes.all
    public init() {}
    public func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] { [] }
    public func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] { [] }
    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy { .manualDefault }
    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        throw PIMError.unexpected(status: 501, body: "Azure resource roles arrive in phase 2")
    }
    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        throw PIMError.unexpected(status: 501, body: "Azure resource roles arrive in phase 2")
    }
}

/// Phase 3. PIM for Groups.
public struct GroupProvider: PIMProvider {
    public let kind: RoleScopeKind = .group
    public let scopes = GraphScopes.all
    public init() {}
    public func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] { [] }
    public func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] { [] }
    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy { .manualDefault }
    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        throw PIMError.unexpected(status: 501, body: "PIM for Groups arrives in phase 3")
    }
    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        throw PIMError.unexpected(status: 501, body: "PIM for Groups arrives in phase 3")
    }
}
```

- [ ] **Step 5: Run tests**

```bash
swift test 2>&1 | tail -3
```
Expected: all pass. If `$expand` in the URL string fails `URL(string:)`, percent-encode with `addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed)` inside `url(_:)` and change the test assertion to `contains("expand=roleDefinition")`.

- [ ] **Step 6: Commit**

```bash
git add Sources/PimTrayCore/Providers Tests/PimTrayCoreTests
git commit -m "Add PIMProvider protocol, Graph transport and Entra eligibility/active reads"
```

---

### Task 6: Entra provider policy, activate, deactivate

**Files:**
- Modify: `Sources/PimTrayCore/Providers/EntraDirectoryProvider.swift` (replace the three 501 stubs)
- Create fixtures: `Tests/PimTrayCoreTests/Fixtures/entra-policy.json`, `Tests/PimTrayCoreTests/Fixtures/entra-activate-response.json`, `Tests/PimTrayCoreTests/Fixtures/me.json`
- Test: append to `Tests/PimTrayCoreTests/EntraDirectoryProviderTests.swift`

**Interfaces:**
- Produces: working `policy(for:identity:)`, `activate(_:identity:)`, `deactivate(_:identity:)` on `EntraDirectoryProvider`.

- [ ] **Step 1: Write fixtures**

`Tests/PimTrayCoreTests/Fixtures/entra-policy.json`:
```json
{
  "value": [
    {
      "id": "DirectoryRole_t1_abc_f2ef992c-3afb-46b9-b7cf-a126ee74c451",
      "policyId": "DirectoryRole_t1_abc", "scopeId": "/", "scopeType": "DirectoryRole",
      "roleDefinitionId": "f2ef992c-3afb-46b9-b7cf-a126ee74c451",
      "policy": {
        "id": "DirectoryRole_t1_abc",
        "rules": [
          { "@odata.type": "#microsoft.graph.unifiedRoleManagementPolicyExpirationRule", "id": "Expiration_EndUser_Assignment", "isExpirationRequired": true, "maximumDuration": "PT4H" },
          { "@odata.type": "#microsoft.graph.unifiedRoleManagementPolicyEnablementRule", "id": "Enablement_EndUser_Assignment", "enabledRules": ["MultiFactorAuthentication", "Justification"] },
          { "@odata.type": "#microsoft.graph.unifiedRoleManagementPolicyApprovalRule", "id": "Approval_EndUser_Assignment", "setting": { "isApprovalRequired": true, "approvalStages": [] } },
          { "@odata.type": "#microsoft.graph.unifiedRoleManagementPolicyExpirationRule", "id": "Expiration_Admin_Eligibility", "isExpirationRequired": false, "maximumDuration": "P365D" }
        ]
      }
    }
  ]
}
```

`Tests/PimTrayCoreTests/Fixtures/entra-activate-response.json`:
```json
{
  "id": "req-1", "status": "Provisioned", "action": "selfActivate",
  "principalId": "user-obj-1",
  "roleDefinitionId": "f2ef992c-3afb-46b9-b7cf-a126ee74c451", "directoryScopeId": "/",
  "createdDateTime": "2026-09-04T09:00:00Z",
  "scheduleInfo": { "startDateTime": "2026-09-04T09:00:00Z", "expiration": { "type": "afterDuration", "duration": "PT2H", "endDateTime": null } }
}
```

`Tests/PimTrayCoreTests/Fixtures/me.json`:
```json
{ "id": "user-obj-1" }
```

- [ ] **Step 2: Write the failing tests**

Append inside the `EntraDirectoryProviderTests` suite:
```swift
    var globalReader: EligibleRole {
        EligibleRole(key: RoleKey(identityId: "id1", tenantId: "t1",
                                  scope: .entraDirectory(roleDefinitionId: "f2ef992c-3afb-46b9-b7cf-a126ee74c451", directoryScopeId: "/")),
                     displayName: "Global Reader", source: .discovered, policy: .manualDefault)
    }

    @Test func readsEndUserActivationPolicy() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleManagementPolicyAssignments", body: Fixtures.data("entra-policy"))
        let policy = try await p.policy(for: globalReader, identity: identity)
        #expect(policy.maximumDuration == .seconds(4 * 3600))
        #expect(policy.defaultDuration == .seconds(4 * 3600))
        #expect(policy.requiresJustification)
        #expect(policy.requiresMFA)
        #expect(!policy.requiresTicket)
        #expect(policy.requiresApproval)
        let req = await http.requests.first!
        #expect(req.url.absoluteString.contains("roleDefinitionId%20eq%20'f2ef992c-3afb-46b9-b7cf-a126ee74c451'")
                || req.url.absoluteString.contains("roleDefinitionId eq 'f2ef992c-3afb-46b9-b7cf-a126ee74c451'"))
    }

    @Test func activatePostsSelfActivateAndComputesEnd() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "/me?", body: Fixtures.data("me"))
        await http.on("POST", "roleAssignmentScheduleRequests", status: 201, body: Fixtures.data("entra-activate-response"))
        let request = ActivationRequest(roleKey: globalReader.key, duration: .seconds(7200), justification: "Ticket 42")
        let a = try await p.activate(request, identity: identity)
        #expect(a.status == .active)
        #expect(a.assignmentId == "req-1")
        #expect(a.startDateTime == GraphJSON.parseDate("2026-09-04T09:00:00Z"))
        #expect(a.endDateTime == GraphJSON.parseDate("2026-09-04T11:00:00Z"))

        let post = await http.requests(matching: "roleAssignmentScheduleRequests").first!
        let body = try JSONSerialization.jsonObject(with: post.body!) as! [String: Any]
        #expect(body["action"] as? String == "selfActivate")
        #expect(body["principalId"] as? String == "user-obj-1")
        #expect(body["roleDefinitionId"] as? String == "f2ef992c-3afb-46b9-b7cf-a126ee74c451")
        #expect(body["directoryScopeId"] as? String == "/")
        #expect(body["justification"] as? String == "Ticket 42")
        let sched = body["scheduleInfo"] as! [String: Any]
        let exp = sched["expiration"] as! [String: Any]
        #expect(exp["type"] as? String == "afterDuration")
        #expect(exp["duration"] as? String == "PT2H")
        #expect(body["ticketInfo"] == nil)
    }

    @Test func activateReportsPendingApproval() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "/me?", body: Fixtures.data("me"))
        var json = try JSONSerialization.jsonObject(with: Fixtures.data("entra-activate-response")) as! [String: Any]
        json["status"] = "PendingApproval"
        await http.on("POST", "roleAssignmentScheduleRequests", status: 201, body: try JSONSerialization.data(withJSONObject: json))
        let a = try await p.activate(ActivationRequest(roleKey: globalReader.key, duration: .seconds(3600), justification: "x"), identity: identity)
        #expect(a.status == .pendingApproval)
    }

    @Test func activatePolicyFailureMapsToPolicyViolation() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "/me?", body: Fixtures.data("me"))
        await http.on("POST", "roleAssignmentScheduleRequests", status: 400,
                      body: Data(#"{"error":{"code":"RoleAssignmentRequestPolicyValidationFailed","message":"The following policy rules failed: [\"JustificationRule\"]"}}"#.utf8))
        await #expect(throws: PIMError.policyViolation(#"The following policy rules failed: ["JustificationRule"]"#)) {
            _ = try await p.activate(ActivationRequest(roleKey: globalReader.key, duration: .seconds(3600), justification: ""), identity: identity)
        }
    }

    @Test func deactivatePostsSelfDeactivate() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "/me?", body: Fixtures.data("me"))
        await http.on("POST", "roleAssignmentScheduleRequests", status: 201, body: Fixtures.data("entra-activate-response"))
        let a = ActiveAssignment(roleKey: globalReader.key, assignmentId: "inst-1", startDateTime: .now, endDateTime: nil, status: .active)
        try await p.deactivate(a, identity: identity)
        let post = await http.requests(matching: "roleAssignmentScheduleRequests").first!
        let body = try JSONSerialization.jsonObject(with: post.body!) as! [String: Any]
        #expect(body["action"] as? String == "selfDeactivate")
        #expect(body["scheduleInfo"] == nil)
    }
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
swift test 2>&1 | grep -E "failed|error:" | head -5
```
Expected: the five new tests fail with the 501 `unexpected` error.

- [ ] **Step 4: Replace the three stubs in EntraDirectoryProvider**

```swift
    // MARK: Policy

    struct PolicyRule: Decodable {
        let id: String
        let isExpirationRequired: Bool?
        let maximumDuration: String?
        let enabledRules: [String]?
        let setting: ApprovalSetting?
        struct ApprovalSetting: Decodable { let isApprovalRequired: Bool? }
    }
    struct Policy: Decodable { let id: String; let rules: [PolicyRule]? }
    struct PolicyAssignment: Decodable { let id: String; let roleDefinitionId: String?; let policy: Policy? }

    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy {
        guard case .entraDirectory(let roleDefinitionId, _) = role.key.scope else { throw PIMError.notEligible }
        let filter = "scopeId eq '/' and scopeType eq 'DirectoryRole' and roleDefinitionId eq '\(roleDefinitionId)'"
        let encoded = filter.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? filter
        let r = try await transport.get(identity: identity, tenantId: role.key.tenantId,
                                        url: url("/policies/roleManagementPolicyAssignments?$filter=\(encoded)&$expand=policy($expand=rules)"),
                                        scopes: scopes)
        let assignments = try GraphJSON.decoder.decode(Collection<PolicyAssignment>.self, from: r.body).value
        guard let rules = assignments.first?.policy?.rules else { return .manualDefault }
        var policy = RolePolicy.manualDefault
        for rule in rules {
            switch rule.id {
            case "Expiration_EndUser_Assignment":
                if let d = rule.maximumDuration.flatMap(ISO8601Duration.parse) {
                    policy.maximumDuration = d
                    policy.defaultDuration = d
                }
            case "Enablement_EndUser_Assignment":
                let enabled = Set(rule.enabledRules ?? [])
                policy.requiresJustification = enabled.contains("Justification")
                policy.requiresTicket = enabled.contains("Ticketing")
                policy.requiresMFA = enabled.contains("MultiFactorAuthentication")
            case "Approval_EndUser_Assignment":
                policy.requiresApproval = rule.setting?.isApprovalRequired ?? false
            default:
                break
            }
        }
        return policy
    }

    // MARK: Activate / deactivate

    struct Me: Decodable { let id: String }

    func principalId(identity: Identity, tenantId: String) async throws -> String {
        let r = try await transport.get(identity: identity, tenantId: tenantId, url: url("/me?$select=id"), scopes: scopes)
        return try GraphJSON.decoder.decode(Me.self, from: r.body).id
    }

    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        guard case .entraDirectory(let roleDefinitionId, let directoryScopeId) = request.roleKey.scope else { throw PIMError.notEligible }
        let principal = try await principalId(identity: identity, tenantId: request.roleKey.tenantId)
        var body: [String: Any] = [
            "action": "selfActivate",
            "principalId": principal,
            "roleDefinitionId": roleDefinitionId,
            "directoryScopeId": directoryScopeId,
            "justification": request.justification,
            "scheduleInfo": [
                "startDateTime": GraphJSON.encoderDateString(.now),
                "expiration": ["type": "afterDuration", "duration": ISO8601Duration.format(request.duration)],
            ],
        ]
        if let t = request.ticket {
            body["ticketInfo"] = ["ticketNumber": t.number, "ticketSystem": t.system]
        }
        let r = try await transport.post(identity: identity, tenantId: request.roleKey.tenantId,
                                         url: url("/roleManagement/directory/roleAssignmentScheduleRequests"),
                                         scopes: scopes, body: try JSONSerialization.data(withJSONObject: body))
        let created = try GraphJSON.decoder.decode(ScheduleRequest.self, from: r.body)
        let start = created.scheduleInfo?.startDateTime ?? .now
        let end = created.scheduleInfo?.expiration?.endDateTime
            ?? created.scheduleInfo?.expiration?.duration.flatMap(ISO8601Duration.parse).map { start.addingTimeInterval(TimeInterval($0.components.seconds)) }
            ?? start.addingTimeInterval(TimeInterval(request.duration.components.seconds))
        let status: ActiveAssignment.Status = switch created.status {
        case "PendingApproval", "PendingAdminDecision": .pendingApproval
        case "PendingProvisioning", "PendingScheduleCreation", "ScheduleCreated": .pendingProvisioning
        case "Denied", "Failed", "Canceled", "Revoked": .failed(created.status)
        default: .active
        }
        return ActiveAssignment(roleKey: request.roleKey, assignmentId: created.id, startDateTime: start,
                                endDateTime: status == .active ? end : nil, status: status)
    }

    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard case .entraDirectory(let roleDefinitionId, let directoryScopeId) = assignment.roleKey.scope else { throw PIMError.notEligible }
        let principal = try await principalId(identity: identity, tenantId: assignment.roleKey.tenantId)
        let body: [String: Any] = [
            "action": "selfDeactivate",
            "principalId": principal,
            "roleDefinitionId": roleDefinitionId,
            "directoryScopeId": directoryScopeId,
        ]
        _ = try await transport.post(identity: identity, tenantId: assignment.roleKey.tenantId,
                                     url: url("/roleManagement/directory/roleAssignmentScheduleRequests"),
                                     scopes: scopes, body: try JSONSerialization.data(withJSONObject: body))
    }
```

Add to `GraphJSON` (in `Support/GraphJSON.swift`):
```swift
    public static func encoderDateString(_ date: Date) -> String { plain.string(from: date) }
```

- [ ] **Step 5: Run tests**

```bash
swift test 2>&1 | tail -3
```
Expected: all pass. Note the `/me?` stub route deliberately matches `/me?$select=id`; the policy route matches on the path segment because the filter is percent-encoded.

- [ ] **Step 6: Commit**

```bash
git add Sources/PimTrayCore Tests/PimTrayCoreTests
git commit -m "Implement Entra PIM policy lookup, activation and deactivation"
```

---

### Task 7: Role catalogue and manual role source

**Files:**
- Create: `Sources/PimTrayCore/Catalogue/RoleCatalogue.swift`, `Sources/PimTrayCore/Catalogue/ManualRoleSource.swift`
- Test: `Tests/PimTrayCoreTests/RoleCatalogueTests.swift`

**Interfaces:**
- Consumes: `Sources/PimTrayCore/Resources/EntraBuiltInRoles.json` (already committed; 136 entries of `{templateId, displayName, description, isPrivileged}`).
- Produces:
  - `struct CatalogueRole: Codable, Hashable, Sendable, Identifiable { templateId, displayName, description, isPrivileged; id = templateId }`
  - `enum RoleCatalogue { static func entraBuiltInRoles() throws -> [CatalogueRole] }`
  - `struct ManualRole: Codable, Hashable, Sendable { tenantKey: TenantKey; scope: RoleScope; displayName: String }`
  - `enum ManualRoleSource { static func eligibleRoles(from: [ManualRole], tenantKey:) -> [EligibleRole]; static func merge(discovered:manual:) -> [EligibleRole] }`

- [ ] **Step 1: Write the failing tests**

```swift
import Testing
import Foundation
@testable import PimTrayCore

@Suite struct RoleCatalogueTests {
    @Test func loadsBuiltInRoles() throws {
        let roles = try RoleCatalogue.entraBuiltInRoles()
        #expect(roles.count >= 130)
        let ga = roles.first { $0.displayName == "Global Administrator" }
        #expect(ga?.templateId == "62e90394-69f5-4237-9190-012177145e10")
        #expect(ga?.isPrivileged == true)
        #expect(roles == roles.sorted { $0.displayName < $1.displayName })
    }

    @Test func manualRolesBecomeEligibleRolesWithDefaultPolicy() {
        let tk = TenantKey(identityId: "i", tenantId: "t")
        let manual = [ManualRole(tenantKey: tk, scope: .entraDirectory(roleDefinitionId: "62e90394-69f5-4237-9190-012177145e10", directoryScopeId: "/"), displayName: "Global Administrator"),
                      ManualRole(tenantKey: TenantKey(identityId: "i", tenantId: "other"), scope: .group(groupId: "g", accessId: .member), displayName: "Ops")]
        let roles = ManualRoleSource.eligibleRoles(from: manual, tenantKey: tk)
        #expect(roles.count == 1)
        #expect(roles[0].source == .manual)
        #expect(roles[0].policy == .manualDefault)
        #expect(roles[0].key == RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "62e90394-69f5-4237-9190-012177145e10", directoryScopeId: "/")))
    }

    @Test func mergePrefersDiscovered() {
        let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
        let discovered = EligibleRole(key: key, displayName: "Disc", source: .discovered, policy: .manualDefault)
        let manual = EligibleRole(key: key, displayName: "Man", source: .manual, policy: .manualDefault)
        let other = EligibleRole(key: RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "x", directoryScopeId: "/")), displayName: "X", source: .manual, policy: .manualDefault)
        let merged = ManualRoleSource.merge(discovered: [discovered], manual: [manual, other])
        #expect(merged.map(\.displayName) == ["Disc", "X"])
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
swift test 2>&1 | grep -E "error:" | head -3
```
Expected: `RoleCatalogue` not found.

- [ ] **Step 3: Implement**

`Sources/PimTrayCore/Catalogue/RoleCatalogue.swift`:
```swift
import Foundation

public struct CatalogueRole: Codable, Hashable, Sendable, Identifiable {
    public let templateId: String
    public let displayName: String
    public let description: String
    public let isPrivileged: Bool
    public var id: String { templateId }
}

public enum RoleCatalogue {
    /// Built-in Entra directory roles bundled with the app. Regenerate with `Scripts/update-role-catalogue.pl`.
    public static func entraBuiltInRoles() throws -> [CatalogueRole] {
        guard let url = Bundle.module.url(forResource: "EntraBuiltInRoles", withExtension: "json") else {
            throw PIMError.unexpected(status: 0, body: "EntraBuiltInRoles.json missing from bundle")
        }
        let roles = try JSONDecoder().decode([CatalogueRole].self, from: Data(contentsOf: url))
        return roles.sorted { $0.displayName < $1.displayName }
    }
}
```

`Sources/PimTrayCore/Catalogue/ManualRoleSource.swift`:
```swift
import Foundation

/// A role the user asserts they hold in a tenant where discovery is unavailable.
public struct ManualRole: Codable, Hashable, Sendable {
    public var tenantKey: TenantKey
    public var scope: RoleScope
    public var displayName: String
    public init(tenantKey: TenantKey, scope: RoleScope, displayName: String) {
        self.tenantKey = tenantKey
        self.scope = scope
        self.displayName = displayName
    }
}

public enum ManualRoleSource {
    public static func eligibleRoles(from manual: [ManualRole], tenantKey: TenantKey) -> [EligibleRole] {
        manual.filter { $0.tenantKey == tenantKey }.map {
            EligibleRole(key: RoleKey(identityId: tenantKey.identityId, tenantId: tenantKey.tenantId, scope: $0.scope),
                         displayName: $0.displayName, source: .manual, policy: .manualDefault)
        }
    }

    /// Discovered roles win over manual entries with the same key; manual-only roles are appended.
    public static func merge(discovered: [EligibleRole], manual: [EligibleRole]) -> [EligibleRole] {
        let known = Set(discovered.map(\.key))
        return discovered + manual.filter { !known.contains($0.key) }
    }
}
```

- [ ] **Step 4: Run tests**

```bash
swift test 2>&1 | tail -3
```
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Sources/PimTrayCore/Catalogue Tests/PimTrayCoreTests/RoleCatalogueTests.swift
git commit -m "Add built-in role catalogue loader and manual role source"
```

---

### Task 8: Persisted app state

**Files:**
- Create: `Sources/PimTrayCore/Storage/AppState.swift`, `Sources/PimTrayCore/Storage/AppStateStore.swift`
- Test: `Tests/PimTrayCoreTests/AppStateStoreTests.swift`

**Interfaces:**
- Produces:
  - `struct RoleMemory: Codable, Hashable, Sendable { roleKey, justification: String, lastDuration: Duration? }`
  - `struct AppState: Codable, Hashable, Sendable { identities: [Identity], tenants: [TenantContext], manualRoles: [ManualRole], memory: [RoleMemory]; mutating remember(roleKey:justification:duration:), memory(for:) -> RoleMemory?, tenants(for identityId:), upsertTenant(_:), removeTenant(_:), removeIdentity(_:) }`
  - `actor AppStateStore { init(directory: URL); load() throws -> AppState; save(_:) throws; static var defaultDirectory: URL }`

- [ ] **Step 1: Write the failing tests**

```swift
import Testing
import Foundation
@testable import PimTrayCore

@Suite struct AppStateStoreTests {
    func tempDir() -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("pimtray-\(UUID().uuidString)")
        try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    @Test func loadReturnsEmptyStateWhenNoFile() async throws {
        let store = AppStateStore(directory: tempDir())
        let s = try await store.load()
        #expect(s == AppState())
    }

    @Test func saveThenLoadRoundTrips() async throws {
        let store = AppStateStore(directory: tempDir())
        var s = AppState()
        s.identities = [Identity(id: "i", upn: "u@x", displayName: "U", homeTenantId: "t")]
        s.upsertTenant(TenantContext(identityId: "i", tenantId: "t", displayName: "Home", source: .home))
        let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
        s.remember(roleKey: key, justification: "Ops work", duration: .seconds(1800))
        s.manualRoles = [ManualRole(tenantKey: TenantKey(identityId: "i", tenantId: "t"), scope: key.scope, displayName: "R")]
        try await store.save(s)
        let back = try await store.load()
        #expect(back == s)
        #expect(back.memory(for: key)?.justification == "Ops work")
        #expect(back.memory(for: key)?.lastDuration == .seconds(1800))
    }

    @Test func rememberOverwritesPerKey() {
        var s = AppState()
        let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
        s.remember(roleKey: key, justification: "a", duration: nil)
        s.remember(roleKey: key, justification: "b", duration: .seconds(60))
        #expect(s.memory.count == 1)
        #expect(s.memory(for: key)?.justification == "b")
    }

    @Test func removingIdentityRemovesTenantsRolesAndMemory() {
        var s = AppState()
        s.identities = [Identity(id: "i", upn: "u@x", displayName: "U", homeTenantId: "t")]
        s.upsertTenant(TenantContext(identityId: "i", tenantId: "t", displayName: "Home", source: .home))
        let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
        s.remember(roleKey: key, justification: "a", duration: nil)
        s.manualRoles = [ManualRole(tenantKey: key.tenantKey, scope: key.scope, displayName: "R")]
        s.removeIdentity("i")
        #expect(s == AppState())
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
swift test 2>&1 | grep -E "error:" | head -3
```
Expected: `AppStateStore` not found.

- [ ] **Step 3: Implement**

`Sources/PimTrayCore/Storage/AppState.swift`:
```swift
import Foundation

public struct RoleMemory: Codable, Hashable, Sendable {
    public var roleKey: RoleKey
    public var justification: String
    public var lastDuration: Duration?
}

public struct AppState: Codable, Hashable, Sendable {
    public var identities: [Identity] = []
    public var tenants: [TenantContext] = []
    public var manualRoles: [ManualRole] = []
    public var memory: [RoleMemory] = []

    public init() {}

    public func tenants(for identityId: String) -> [TenantContext] {
        tenants.filter { $0.identityId == identityId }
    }

    public mutating func upsertTenant(_ tenant: TenantContext) {
        if let i = tenants.firstIndex(where: { $0.id == tenant.id }) { tenants[i] = tenant } else { tenants.append(tenant) }
    }

    public mutating func removeTenant(_ key: TenantKey) {
        tenants.removeAll { $0.id == key }
        manualRoles.removeAll { $0.tenantKey == key }
        memory.removeAll { $0.roleKey.tenantKey == key }
    }

    public mutating func removeIdentity(_ identityId: String) {
        identities.removeAll { $0.id == identityId }
        for t in tenants where t.identityId == identityId { removeTenant(t.id) }
    }

    public func memory(for key: RoleKey) -> RoleMemory? {
        memory.first { $0.roleKey == key }
    }

    public mutating func remember(roleKey: RoleKey, justification: String, duration: Duration?) {
        let entry = RoleMemory(roleKey: roleKey, justification: justification, lastDuration: duration)
        if let i = memory.firstIndex(where: { $0.roleKey == roleKey }) { memory[i] = entry } else { memory.append(entry) }
    }
}
```

`Sources/PimTrayCore/Storage/AppStateStore.swift`:
```swift
import Foundation

public actor AppStateStore {
    private let fileURL: URL

    public static var defaultDirectory: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("PimTray", isDirectory: true)
    }

    public init(directory: URL = AppStateStore.defaultDirectory) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        fileURL = directory.appendingPathComponent("state.json")
    }

    public func load() throws -> AppState {
        guard FileManager.default.fileExists(atPath: fileURL.path) else { return AppState() }
        return try GraphJSON.decoder.decode(AppState.self, from: Data(contentsOf: fileURL))
    }

    public func save(_ state: AppState) throws {
        let encoder = GraphJSON.encoder
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(state).write(to: fileURL, options: .atomic)
    }
}
```

- [ ] **Step 4: Run tests**

```bash
swift test 2>&1 | tail -3
```
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Sources/PimTrayCore/Storage Tests/PimTrayCoreTests/AppStateStoreTests.swift
git commit -m "Add persisted app state and JSON store"
```

---

### Task 9: Tenant discovery

**Files:**
- Create: `Sources/PimTrayCore/Discovery/TenantDiscovery.swift`
- Create fixture: `Tests/PimTrayCoreTests/Fixtures/arm-tenants.json`
- Test: `Tests/PimTrayCoreTests/TenantDiscoveryTests.swift`

**Interfaces:**
- Produces:
  - `struct DiscoveredTenant: Hashable, Sendable, Identifiable { tenantId, displayName, defaultDomain; id = tenantId }`
  - `struct TenantDiscovery { init(http:tokens:); discoverTenants(identity:) async throws -> [DiscoveredTenant]; resolveTenantId(domainOrId:) async throws -> String; tenantDisplayName(identity:tenantId:) async throws -> String }`

- [ ] **Step 1: Write fixture**

`Tests/PimTrayCoreTests/Fixtures/arm-tenants.json`:
```json
{
  "value": [
    { "id": "/tenants/t-home", "tenantId": "t-home", "displayName": "Contoso", "defaultDomain": "contoso.onmicrosoft.com", "tenantCategory": "Home" },
    { "id": "/tenants/t-cust", "tenantId": "t-cust", "displayName": "Fabrikam", "defaultDomain": "fabrikam.onmicrosoft.com", "tenantCategory": "Home" },
    { "id": "/tenants/t-nodisplay", "tenantId": "t-nodisplay", "tenantCategory": "Home" }
  ]
}
```

- [ ] **Step 2: Write the failing tests**

```swift
import Testing
import Foundation
@testable import PimTrayCore

@Suite struct TenantDiscoveryTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t-home")

    @Test func listsTenantsFromARM() async throws {
        let http = StubHTTPClient()
        let tokens = FakeTokenProvider()
        await http.on("GET", "management.azure.com/tenants", body: Fixtures.data("arm-tenants"))
        let d = TenantDiscovery(http: http, tokens: tokens)
        let tenants = try await d.discoverTenants(identity: identity)
        #expect(tenants.map(\.tenantId) == ["t-home", "t-cust", "t-nodisplay"])
        #expect(tenants[1].displayName == "Fabrikam")
        #expect(tenants[2].displayName == "t-nodisplay")
        let req = await http.requests.first!
        #expect(req.headers["Authorization"] == "Bearer token-t-home")
        #expect(req.url.absoluteString.contains("api-version=2022-12-01"))
        #expect(await tokens.silentCalls == ["t-home"])
    }

    @Test func resolvesDomainThroughOpenIdConfiguration() async throws {
        let http = StubHTTPClient()
        await http.on("GET", "fabrikam.com/v2.0/.well-known/openid-configuration",
                      body: Data(#"{"issuer":"https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0"}"#.utf8))
        let d = TenantDiscovery(http: http, tokens: FakeTokenProvider())
        #expect(try await d.resolveTenantId(domainOrId: "fabrikam.com") == "11111111-2222-3333-4444-555555555555")
    }

    @Test func passesGuidThroughWithoutNetwork() async throws {
        let http = StubHTTPClient()
        let d = TenantDiscovery(http: http, tokens: FakeTokenProvider())
        #expect(try await d.resolveTenantId(domainOrId: " 11111111-2222-3333-4444-555555555555 ") == "11111111-2222-3333-4444-555555555555")
        #expect(await http.requests.isEmpty)
    }

    @Test func unknownDomainThrows() async throws {
        let http = StubHTTPClient()
        await http.on("GET", "openid-configuration", status: 400, body: Data(#"{"error":"invalid_tenant"}"#.utf8))
        let d = TenantDiscovery(http: http, tokens: FakeTokenProvider())
        await #expect(throws: PIMError.self) { _ = try await d.resolveTenantId(domainOrId: "nope.example") }
    }

    @Test func readsOrganizationDisplayName() async throws {
        let http = StubHTTPClient()
        await http.on("GET", "/organization", body: Data(#"{"value":[{"id":"t-cust","displayName":"Fabrikam AS"}]}"#.utf8))
        let d = TenantDiscovery(http: http, tokens: FakeTokenProvider())
        #expect(try await d.tenantDisplayName(identity: identity, tenantId: "t-cust") == "Fabrikam AS")
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
swift test 2>&1 | grep -E "error:" | head -3
```
Expected: `TenantDiscovery` not found.

- [ ] **Step 4: Implement**

`Sources/PimTrayCore/Discovery/TenantDiscovery.swift`:
```swift
import Foundation

public struct DiscoveredTenant: Hashable, Sendable, Identifiable {
    public let tenantId: String
    public let displayName: String
    public let defaultDomain: String?
    public var id: String { tenantId }
}

public struct TenantDiscovery: Sendable {
    let http: any HTTPClient
    let tokens: any TokenProviding

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        self.http = http
        self.tokens = tokens
    }

    struct ArmTenant: Decodable { let tenantId: String; let displayName: String?; let defaultDomain: String? }
    struct ArmCollection: Decodable { let value: [ArmTenant] }

    /// Every tenant the identity can reach, via Azure Resource Manager using a home-tenant token.
    public func discoverTenants(identity: Identity) async throws -> [DiscoveredTenant] {
        let transport = GraphTransport(http: http, tokens: tokens)
        let url = URL(string: "https://management.azure.com/tenants?api-version=2022-12-01")!
        let r = try await transport.get(identity: identity, tenantId: identity.homeTenantId, url: url, scopes: ArmScopes.all)
        return try JSONDecoder().decode(ArmCollection.self, from: r.body).value.map {
            DiscoveredTenant(tenantId: $0.tenantId, displayName: $0.displayName ?? $0.defaultDomain ?? $0.tenantId, defaultDomain: $0.defaultDomain)
        }
    }

    /// Accepts a tenant GUID or a verified domain; domains are resolved via the OpenID configuration issuer.
    public func resolveTenantId(domainOrId: String) async throws -> String {
        let input = domainOrId.trimmingCharacters(in: .whitespacesAndNewlines)
        if input.wholeMatch(of: /[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/) != nil {
            return input.lowercased()
        }
        let url = URL(string: "https://login.microsoftonline.com/\(input)/v2.0/.well-known/openid-configuration")!
        let r = try await http.send(HTTPRequest(method: "GET", url: url))
        guard r.status == 200 else { throw PIMError.unexpected(status: r.status, body: "Unknown tenant '\(input)'") }
        struct Config: Decodable { let issuer: String }
        let issuer = try JSONDecoder().decode(Config.self, from: r.body).issuer
        guard let m = issuer.firstMatch(of: /([0-9a-fA-F-]{36})/) else {
            throw PIMError.unexpected(status: 200, body: "No tenant id in issuer \(issuer)")
        }
        return String(m.1).lowercased()
    }

    /// Display name from Graph `/organization` inside that tenant.
    public func tenantDisplayName(identity: Identity, tenantId: String) async throws -> String {
        let transport = GraphTransport(http: http, tokens: tokens)
        let url = URL(string: GraphTransport.graphBase.absoluteString + "/organization?$select=id,displayName")!
        let r = try await transport.get(identity: identity, tenantId: tenantId, url: url, scopes: [GraphScopes.userRead])
        struct Org: Decodable { let displayName: String? }
        struct Col: Decodable { let value: [Org] }
        return try JSONDecoder().decode(Col.self, from: r.body).value.first?.displayName ?? tenantId
    }
}
```

- [ ] **Step 5: Run tests**

```bash
swift test 2>&1 | tail -3
```
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Sources/PimTrayCore/Discovery Tests/PimTrayCoreTests
git commit -m "Add tenant discovery via ARM and OpenID configuration"
```

---

### Task 10: Activation coordinator

**Files:**
- Create: `Sources/PimTrayCore/Coordination/ActivationCoordinator.swift`
- Create: `Tests/PimTrayCoreTests/Support/FakeProvider.swift`
- Test: `Tests/PimTrayCoreTests/ActivationCoordinatorTests.swift`

**Interfaces:**
- Consumes: `PIMProvider`, `TokenProviding`, `InteractionRetry`.
- Produces:
  - `struct ActivationOutcome: Sendable, Hashable { roleKey: RoleKey; result: Result }` with `enum Result: Hashable, Sendable { case activated(ActiveAssignment), pendingApproval, failed(PIMError) }`
  - `final class ActivationCoordinator: Sendable { init(providers: [any PIMProvider], tokens: any TokenProviding); activate(_ requests: [ActivationRequest], identities: [Identity], onProgress: @Sendable @escaping (ActivationOutcome) -> Void) async -> [ActivationOutcome]; deactivate(_:identity:) async throws; provider(for:) -> (any PIMProvider)? }`

- [ ] **Step 1: Write the fake provider**

`Tests/PimTrayCoreTests/Support/FakeProvider.swift`:
```swift
import Foundation
@testable import PimTrayCore

/// Scriptable provider. `failures` is consumed one error per activate call, in order, before succeeding.
actor FakeProviderState {
    var failures: [PIMError] = []
    var activated: [ActivationRequest] = []
    var deactivated: [ActiveAssignment] = []
    var order: [String] = []
    func pushFailure(_ e: PIMError) { failures.append(e) }
    func nextFailure() -> PIMError? { failures.isEmpty ? nil : failures.removeFirst() }
    func recordActivate(_ r: ActivationRequest) { activated.append(r); order.append("\(r.roleKey.tenantId):\(r.justification)") }
    func recordDeactivate(_ a: ActiveAssignment) { deactivated.append(a) }
}

struct FakeProvider: PIMProvider {
    let kind: RoleScopeKind
    let scopes = ["scope"]
    let state = FakeProviderState()

    func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] { [] }
    func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] { [] }
    func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy { .manualDefault }

    func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        if let e = await state.nextFailure() { throw e }
        await state.recordActivate(request)
        if request.justification == "approve-me" {
            return ActiveAssignment(roleKey: request.roleKey, assignmentId: "p", startDateTime: .now, endDateTime: nil, status: .pendingApproval)
        }
        return ActiveAssignment(roleKey: request.roleKey, assignmentId: "a", startDateTime: .now,
                                endDateTime: Date().addingTimeInterval(TimeInterval(request.duration.components.seconds)), status: .active)
    }

    func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        if let e = await state.nextFailure() { throw e }
        await state.recordDeactivate(assignment)
    }
}
```

- [ ] **Step 2: Write the failing tests**

```swift
import Testing
import Foundation
@testable import PimTrayCore

@Suite struct ActivationCoordinatorTests {
    let identity = Identity(id: "id1", upn: "u@x", displayName: "U", homeTenantId: "t1")
    func key(_ tenant: String, _ role: String) -> RoleKey {
        RoleKey(identityId: "id1", tenantId: tenant, scope: .entraDirectory(roleDefinitionId: role, directoryScopeId: "/"))
    }

    @Test func activatesSingleRole() async throws {
        let provider = FakeProvider(kind: .entraDirectory)
        let c = ActivationCoordinator(providers: [provider], tokens: FakeTokenProvider())
        let outcomes = await c.activate([ActivationRequest(roleKey: key("t1", "r1"), duration: .seconds(3600), justification: "j")], identities: [identity]) { _ in }
        #expect(outcomes.count == 1)
        guard case .activated(let a) = outcomes[0].result else { Issue.record("expected activated"); return }
        #expect(a.status == .active)
        #expect(await provider.state.activated.count == 1)
    }

    @Test func bulkRunsSequentiallyWithinTenantAndReportsProgress() async throws {
        let provider = FakeProvider(kind: .entraDirectory)
        let c = ActivationCoordinator(providers: [provider], tokens: FakeTokenProvider())
        let requests = [
            ActivationRequest(roleKey: key("t1", "r1"), duration: .seconds(60), justification: "a"),
            ActivationRequest(roleKey: key("t1", "r2"), duration: .seconds(60), justification: "b"),
            ActivationRequest(roleKey: key("t2", "r1"), duration: .seconds(60), justification: "c"),
        ]
        let progress = ProgressSink()
        let outcomes = await c.activate(requests, identities: [identity]) { o in Task { await progress.add(o) } }
        #expect(outcomes.count == 3)
        #expect(Set(outcomes.map(\.roleKey)) == Set(requests.map(\.roleKey)))
        let order = await provider.state.order
        #expect(order.firstIndex(of: "t1:a")! < order.firstIndex(of: "t1:b")!)
        try await Task.sleep(for: .milliseconds(50))
        #expect(await progress.items.count == 3)
    }

    @Test func claimsChallengeTriggersInteractiveAndRetries() async throws {
        let provider = FakeProvider(kind: .entraDirectory)
        await provider.state.pushFailure(.claimsChallenge(#"{"access_token":{}}"#))
        let tokens = FakeTokenProvider()
        let c = ActivationCoordinator(providers: [provider], tokens: tokens)
        let outcomes = await c.activate([ActivationRequest(roleKey: key("t1", "r1"), duration: .seconds(60), justification: "j")], identities: [identity]) { _ in }
        guard case .activated = outcomes[0].result else { Issue.record("expected activated after retry"); return }
        let calls = await tokens.interactiveCalls
        #expect(calls.count == 1)
        #expect(calls[0].tenantId == "t1")
        #expect(calls[0].claims == #"{"access_token":{}}"#)
        #expect(calls[0].scopes == ["scope"])
    }

    @Test func secondFailureIsReportedNotRetriedForever() async throws {
        let provider = FakeProvider(kind: .entraDirectory)
        await provider.state.pushFailure(.claimsChallenge("{}"))
        await provider.state.pushFailure(.claimsChallenge("{}"))
        let c = ActivationCoordinator(providers: [provider], tokens: FakeTokenProvider())
        let outcomes = await c.activate([ActivationRequest(roleKey: key("t1", "r1"), duration: .seconds(60), justification: "j")], identities: [identity]) { _ in }
        #expect(outcomes[0].result == .failed(.claimsChallenge("{}")))
    }

    @Test func pendingApprovalAndPolicyViolationAreReported() async throws {
        let provider = FakeProvider(kind: .entraDirectory)
        await provider.state.pushFailure(.policyViolation("JustificationRule"))
        let c = ActivationCoordinator(providers: [provider], tokens: FakeTokenProvider())
        let outcomes = await c.activate([
            ActivationRequest(roleKey: key("t1", "r1"), duration: .seconds(60), justification: ""),
            ActivationRequest(roleKey: key("t1", "r2"), duration: .seconds(60), justification: "approve-me"),
        ], identities: [identity]) { _ in }
        let byKey = Dictionary(uniqueKeysWithValues: outcomes.map { ($0.roleKey, $0.result) })
        #expect(byKey[key("t1", "r1")] == .failed(.policyViolation("JustificationRule")))
        #expect(byKey[key("t1", "r2")] == .pendingApproval)
    }

    @Test func unknownIdentityOrProviderFails() async throws {
        let c = ActivationCoordinator(providers: [FakeProvider(kind: .entraDirectory)], tokens: FakeTokenProvider())
        let outcomes = await c.activate([
            ActivationRequest(roleKey: RoleKey(identityId: "ghost", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/")), duration: .seconds(60), justification: "j"),
            ActivationRequest(roleKey: RoleKey(identityId: "id1", tenantId: "t", scope: .group(groupId: "g", accessId: .member)), duration: .seconds(60), justification: "j"),
        ], identities: [identity]) { _ in }
        #expect(outcomes.allSatisfy { if case .failed = $0.result { true } else { false } })
    }

    @Test func deactivateRetriesOnInteractionRequired() async throws {
        let provider = FakeProvider(kind: .entraDirectory)
        await provider.state.pushFailure(.interactionRequired)
        let tokens = FakeTokenProvider()
        let c = ActivationCoordinator(providers: [provider], tokens: tokens)
        let a = ActiveAssignment(roleKey: key("t1", "r1"), assignmentId: "x", startDateTime: .now, endDateTime: nil, status: .active)
        try await c.deactivate(a, identity: identity)
        #expect(await provider.state.deactivated.count == 1)
        #expect(await tokens.interactiveCalls.count == 1)
    }
}

actor ProgressSink {
    var items: [ActivationOutcome] = []
    func add(_ o: ActivationOutcome) { items.append(o) }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
swift test 2>&1 | grep -E "error:" | head -3
```
Expected: `ActivationCoordinator` not found.

- [ ] **Step 4: Implement**

`Sources/PimTrayCore/Coordination/ActivationCoordinator.swift`:
```swift
import Foundation

public struct ActivationOutcome: Hashable, Sendable {
    public enum Result: Hashable, Sendable {
        case activated(ActiveAssignment)
        case pendingApproval
        case failed(PIMError)
    }
    public let roleKey: RoleKey
    public let result: Result
    public init(roleKey: RoleKey, result: Result) { self.roleKey = roleKey; self.result = result }
}

/// Runs activations grouped by identity+tenant: groups in parallel, requests within a group in sequence.
/// Handles `interactionRequired` and `claimsChallenge` with one interactive prompt and one retry per request.
public final class ActivationCoordinator: Sendable {
    private let providers: [RoleScopeKind: any PIMProvider]
    private let tokens: any TokenProviding

    public init(providers: [any PIMProvider], tokens: any TokenProviding) {
        self.providers = Dictionary(uniqueKeysWithValues: providers.map { ($0.kind, $0) })
        self.tokens = tokens
    }

    public func provider(for kind: RoleScopeKind) -> (any PIMProvider)? { providers[kind] }

    public func activate(_ requests: [ActivationRequest], identities: [Identity],
                         onProgress: @Sendable @escaping (ActivationOutcome) -> Void) async -> [ActivationOutcome] {
        let identityById = Dictionary(uniqueKeysWithValues: identities.map { ($0.id, $0) })
        let groups = Dictionary(grouping: requests) { $0.roleKey.tenantKey }
        return await withTaskGroup(of: [ActivationOutcome].self) { group in
            for (tenantKey, groupRequests) in groups {
                group.addTask { [self] in
                    var outcomes: [ActivationOutcome] = []
                    for request in groupRequests {
                        let outcome = await self.activateOne(request, identity: identityById[tenantKey.identityId])
                        onProgress(outcome)
                        outcomes.append(outcome)
                    }
                    return outcomes
                }
            }
            var all: [ActivationOutcome] = []
            for await batch in group { all += batch }
            return all
        }
    }

    private func activateOne(_ request: ActivationRequest, identity: Identity?) async -> ActivationOutcome {
        guard let identity else {
            return ActivationOutcome(roleKey: request.roleKey, result: .failed(.unexpected(status: 0, body: "Unknown identity \(request.roleKey.identityId)")))
        }
        guard let provider = providers[request.roleKey.scope.kind] else {
            return ActivationOutcome(roleKey: request.roleKey, result: .failed(.unexpected(status: 501, body: "No provider for \(request.roleKey.scope.kind)")))
        }
        do {
            let assignment = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: request.roleKey.tenantId, scopes: provider.scopes) {
                try await provider.activate(request, identity: identity)
            }
            switch assignment.status {
            case .pendingApproval: return ActivationOutcome(roleKey: request.roleKey, result: .pendingApproval)
            case .failed(let m): return ActivationOutcome(roleKey: request.roleKey, result: .failed(.unexpected(status: 0, body: m)))
            default: return ActivationOutcome(roleKey: request.roleKey, result: .activated(assignment))
            }
        } catch let e as PIMError {
            return ActivationOutcome(roleKey: request.roleKey, result: .failed(e))
        } catch {
            return ActivationOutcome(roleKey: request.roleKey, result: .failed(.network(error.localizedDescription)))
        }
    }

    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard let provider = providers[assignment.roleKey.scope.kind] else {
            throw PIMError.unexpected(status: 501, body: "No provider for \(assignment.roleKey.scope.kind)")
        }
        try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: assignment.roleKey.tenantId, scopes: provider.scopes) {
            try await provider.deactivate(assignment, identity: identity)
        }
    }
}
```

- [ ] **Step 5: Run tests**

```bash
swift test 2>&1 | tail -3
```
Expected: all pass. If the compiler rejects the non-`Sendable` closure in `InteractionRetry.run`, mark the `operation` parameter `@Sendable` in `TokenProviding.swift` and the call sites still compile because they capture only `Sendable` values.

- [ ] **Step 6: Commit**

```bash
git add Sources/PimTrayCore/Coordination Tests/PimTrayCoreTests
git commit -m "Add activation coordinator with bulk grouping and interactive retry"
```

---

### Task 11: App target, MSAL token provider, XcodeGen project

**Files:**
- Create: `project.yml`, `Sources/PimTrayApp/App/AppConfig.swift`, `Sources/PimTrayApp/App/AppDelegate.swift`, `Sources/PimTrayApp/App/PimTrayApp.swift`, `Sources/PimTrayApp/MSAL/AuthAnchorWindow.swift`, `Sources/PimTrayApp/MSAL/MSALTokenProvider.swift`
- Modify: `.gitignore` (add `build/`)

**Interfaces:**
- Consumes: `TokenProviding`, `Identity`, `PIMError` from Core.
- Produces:
  - `enum AppConfig { static func load() throws -> AppConfig; let clientId: String; static let bundleId = "no.frodehus.pimtray"; var redirectUri: String }`
  - `final class MSALTokenProvider: TokenProviding, @unchecked Sendable { init(clientId:redirectUri:) throws }`
  - `@MainActor final class AuthAnchorWindow { func present() -> NSViewController; func dismiss() }`
  - `PimTrayApp` scene placeholder (the real panel arrives in Task 13; this task shows a "PimTray" text so the build is verifiable).

- [ ] **Step 1: Write project.yml**

```yaml
name: PimTray
options:
  bundleIdPrefix: no.frodehus
  deploymentTarget:
    macOS: "26.0"
  createIntermediateGroups: true
packages:
  PimTrayCore:
    path: .
  MSAL:
    url: https://github.com/AzureAD/microsoft-authentication-library-for-objc
    from: 2.15.0
targets:
  PimTrayApp:
    type: application
    platform: macOS
    sources:
      - path: Sources/PimTrayApp
      - path: PimTrayConfig.plist
        optional: true
        buildPhase: resources
    dependencies:
      - package: PimTrayCore
        product: PimTrayCore
      - package: MSAL
        product: MSAL
    info:
      path: Sources/PimTrayApp/Info.plist
      properties:
        CFBundleDisplayName: PimTray
        CFBundleName: PimTray
        LSUIElement: true
        LSMinimumSystemVersion: "26.0"
        NSHumanReadableCopyright: ""
        CFBundleURLTypes:
          - CFBundleURLName: no.frodehus.pimtray.auth
            CFBundleURLSchemes:
              - msauth.no.frodehus.pimtray
        LSApplicationQueriesSchemes:
          - msauthv2
          - msauthv3
    entitlements:
      path: Sources/PimTrayApp/PimTray.entitlements
      properties:
        keychain-access-groups:
          - $(AppIdentifierPrefix)com.microsoft.identity.universalstorage
    settings:
      base:
        PRODUCT_BUNDLE_IDENTIFIER: no.frodehus.pimtray
        PRODUCT_NAME: PimTray
        DEVELOPMENT_TEAM: VLJKN96D7N
        CODE_SIGN_STYLE: Automatic
        SWIFT_VERSION: "6.0"
        SWIFT_STRICT_CONCURRENCY: complete
        MACOSX_DEPLOYMENT_TARGET: "26.0"
        ENABLE_APP_SANDBOX: false
        GENERATE_INFOPLIST_FILE: false
schemes:
  PimTrayApp:
    build:
      targets:
        PimTrayApp: all
    run:
      config: Debug
    test:
      config: Debug
      targets: []
```

Append `build/` to `.gitignore`.

- [ ] **Step 2: Write AppConfig**

`Sources/PimTrayApp/App/AppConfig.swift`:
```swift
import Foundation

struct AppConfig: Sendable {
    static let bundleId = "no.frodehus.pimtray"
    let clientId: String
    var redirectUri: String { "msauth.\(Self.bundleId)://auth" }

    enum ConfigError: LocalizedError {
        case missing, invalid
        var errorDescription: String? {
            switch self {
            case .missing: "PimTrayConfig.plist not found. Copy PimTrayConfig.plist.example to PimTrayConfig.plist, set ClientId, and rebuild."
            case .invalid: "PimTrayConfig.plist has no ClientId string."
            }
        }
    }

    static func load(bundle: Bundle = .main) throws -> AppConfig {
        guard let url = bundle.url(forResource: "PimTrayConfig", withExtension: "plist") else { throw ConfigError.missing }
        let data = try Data(contentsOf: url)
        guard let dict = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              let clientId = dict["ClientId"] as? String, !clientId.isEmpty,
              clientId != "00000000-0000-0000-0000-000000000000" else { throw ConfigError.invalid }
        return AppConfig(clientId: clientId)
    }
}
```

- [ ] **Step 3: Write the auth anchor window**

`Sources/PimTrayApp/MSAL/AuthAnchorWindow.swift`:
```swift
import AppKit

/// A small always-available window MSAL can use as the presentation anchor for the browser sign-in sheet.
@MainActor
final class AuthAnchorWindow {
    private var window: NSWindow?
    private let controller = NSViewController()

    func present() -> NSViewController {
        if window == nil {
            controller.view = NSView(frame: NSRect(x: 0, y: 0, width: 420, height: 120))
            let label = NSTextField(labelWithString: "Complete sign-in in your browser…")
            label.frame = NSRect(x: 20, y: 50, width: 380, height: 20)
            label.alignment = .center
            controller.view.addSubview(label)
            let w = NSWindow(contentViewController: controller)
            w.title = "PimTray sign-in"
            w.styleMask = [.titled, .closable]
            w.isReleasedWhenClosed = false
            w.level = .floating
            w.center()
            window = w
        }
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        return controller
    }

    func dismiss() {
        window?.orderOut(nil)
    }
}
```

- [ ] **Step 4: Write the MSAL token provider**

`Sources/PimTrayApp/MSAL/MSALTokenProvider.swift`:
```swift
import Foundation
import MSAL
import PimTrayCore

/// Wraps MSAL for macOS behind `TokenProviding`. All interactive calls hop to the main actor.
final class MSALTokenProvider: TokenProviding, @unchecked Sendable {
    private let app: MSALPublicClientApplication
    private let anchor: AuthAnchorWindow

    init(clientId: String, redirectUri: String, anchor: AuthAnchorWindow) throws {
        let authority = try MSALAADAuthority(url: URL(string: "https://login.microsoftonline.com/organizations")!)
        let config = MSALPublicClientApplicationConfig(clientId: clientId, redirectUri: redirectUri, authority: authority)
        app = try MSALPublicClientApplication(configuration: config)
        self.anchor = anchor
    }

    // MARK: TokenProviding

    func signIn() async throws -> Identity {
        let result = try await interactive(account: nil, tenantId: nil, scopes: [GraphScopes.userRead], claims: nil, prompt: .selectAccount)
        return Self.identity(from: result.account)
    }

    func signOut(_ identity: Identity) async throws {
        guard let account = try? app.account(forIdentifier: identity.id) else { return }
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            Task { @MainActor in
                let web = MSALWebviewParameters(authPresentationViewController: anchor.present())
                let params = MSALSignoutParameters(webviewParameters: web)
                params.signoutFromBrowser = false
                app.signout(with: account, signoutParameters: params) { _, error in
                    Task { @MainActor in self.anchor.dismiss() }
                    if let error { cont.resume(throwing: Self.map(error)) } else { cont.resume() }
                }
            }
        }
    }

    func identities() async throws -> [Identity] {
        try app.allAccounts().map(Self.identity(from:))
    }

    func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String {
        guard let account = try? app.account(forIdentifier: identity.id) else { throw PIMError.interactionRequired }
        let params = MSALSilentTokenParameters(scopes: scopes, account: account)
        params.authority = try MSALAADAuthority(url: URL(string: "https://login.microsoftonline.com/\(tenantId)")!)
        return try await withCheckedThrowingContinuation { cont in
            app.acquireTokenSilent(with: params) { result, error in
                if let result { cont.resume(returning: result.accessToken) } else { cont.resume(throwing: Self.map(error)) }
            }
        }
    }

    @discardableResult
    func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String {
        let account = try? app.account(forIdentifier: identity.id)
        let result = try await interactive(account: account, tenantId: tenantId, scopes: scopes, claims: claims, prompt: .promptIfNecessary)
        return result.accessToken
    }

    // MARK: Helpers

    private func interactive(account: MSALAccount?, tenantId: String?, scopes: [String], claims: String?, prompt: MSALPromptType) async throws -> MSALResult {
        try await withCheckedThrowingContinuation { cont in
            Task { @MainActor in
                do {
                    let web = MSALWebviewParameters(authPresentationViewController: anchor.present())
                    web.webviewType = .authenticationSession
                    web.prefersEphemeralWebBrowserSession = false
                    let params = MSALInteractiveTokenParameters(scopes: scopes, webviewParameters: web)
                    params.account = account
                    params.promptType = prompt
                    if let tenantId {
                        params.authority = try MSALAADAuthority(url: URL(string: "https://login.microsoftonline.com/\(tenantId)")!)
                    }
                    if let claims {
                        var err: NSError?
                        params.claimsRequest = MSALClaimsRequest(jsonString: claims, error: &err)
                        if let err { throw err }
                    }
                    app.acquireToken(with: params) { result, error in
                        Task { @MainActor in self.anchor.dismiss() }
                        if let result { cont.resume(returning: result) } else { cont.resume(throwing: Self.map(error)) }
                    }
                } catch {
                    anchor.dismiss()
                    cont.resume(throwing: Self.map(error))
                }
            }
        }
    }

    static func identity(from account: MSALAccount) -> Identity {
        let claims = account.accountClaims ?? [:]
        return Identity(id: account.identifier ?? account.username ?? UUID().uuidString,
                        upn: account.username ?? "unknown",
                        displayName: (claims["name"] as? String) ?? account.username ?? "unknown",
                        homeTenantId: account.homeAccountId?.tenantId ?? (claims["tid"] as? String) ?? "")
    }

    static func map(_ error: Error?) -> PIMError {
        guard let error else { return .network("Unknown MSAL failure") }
        let ns = error as NSError
        guard ns.domain == MSALErrorDomain else { return .network(ns.localizedDescription) }
        if ns.code == MSALError.interactionRequired.rawValue { return .interactionRequired }
        if ns.code == MSALError.userCanceled.rawValue { return .network("Sign-in cancelled") }
        let desc = (ns.userInfo[MSALErrorDescriptionKey] as? String) ?? ns.localizedDescription
        if desc.contains("AADSTS65001") || desc.contains("AADSTS65004") || desc.contains("consent_required") || desc.contains("AADSTS90094") {
            return .consentRequired
        }
        return .network(desc)
    }
}
```

- [ ] **Step 5: Write the app entry point and delegate (placeholder panel)**

`Sources/PimTrayApp/App/AppDelegate.swift`:
```swift
import AppKit
import MSAL

final class AppDelegate: NSObject, NSApplicationDelegate {
    func application(_ application: NSApplication, open urls: [URL]) {
        for url in urls {
            MSALPublicClientApplication.handleMSALResponse(url, sourceApplication: nil)
        }
    }
}
```

`Sources/PimTrayApp/App/PimTrayApp.swift` (placeholder; replaced in Task 13):
```swift
import SwiftUI

@main
struct PimTrayApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        MenuBarExtra("PimTray", systemImage: "shield") {
            Text("PimTray").padding()
        }
        .menuBarExtraStyle(.window)
    }
}
```

- [ ] **Step 6: Generate the project and build**

```bash
cp -n PimTrayConfig.plist.example PimTrayConfig.plist
xcodegen generate
xcodebuild -project PimTray.xcodeproj -scheme PimTrayApp -configuration Debug -derivedDataPath build build 2>&1 | grep -E "error:|warning: unre|BUILD" | head -20
```
Expected: `** BUILD SUCCEEDED **`. Resolving the MSAL package the first time can take a minute. If Xcode reports a signing error, open `PimTray.xcodeproj` once in Xcode, select the target, and confirm the team under Signing & Capabilities, then re-run.

- [ ] **Step 7: Launch to confirm the status item appears**

```bash
open build/Build/Products/Debug/PimTray.app
```
Expected: a shield icon in the menu bar; clicking it shows "PimTray". Quit with:
```bash
pkill -x PimTray
```

- [ ] **Step 8: Commit**

```bash
git add project.yml Sources/PimTrayApp .gitignore
git commit -m "Add macOS app target with MSAL token provider and XcodeGen project"
```

---

### Task 12: AppModel

**Files:**
- Create: `Sources/PimTrayApp/App/AppModel.swift`, `Sources/PimTrayApp/App/PanelRoute.swift`

**Interfaces:**
- Consumes: everything in Core, `MSALTokenProvider`, `AuthAnchorWindow`, `AppConfig`, `ExpiryNotifying` (protocol defined here, implemented in Task 15).
- Produces `@MainActor @Observable final class AppModel` with:
  - state: `state: AppState`, `roles: [TenantKey: [EligibleRole]]`, `active: [RoleKey: ActiveAssignment]`, `busy: Set<TenantKey>`, `tenantErrors: [TenantKey: String]`, `selectMode: Bool`, `selection: Set<RoleKey>`, `fatalError: String?`, `progress: [RoleKey: ActivationOutcome.Result]`, `pendingExtend: RoleKey?`
  - derived: `activeCount`, `identities`, `tenants(for:)`, `roles(for:)`, `role(for:)`, `assignment(for:)`, `remembered(for:)`, `adminConsentURL(tenantId:)`
  - actions: `bootstrap()`, `addAccount()`, `signOut(_:)`, `refreshAll()`, `refresh(_ tenantKey:)`, `activate(_:)`, `deactivate(_:)`, `setManualRoles(_:for:)`, `addTenant(identityId:domainOrId:)`, `discoverTenants(identityId:)`, `trackTenants(identityId:tenants:)`, `removeTenant(_:)`, `retryDiscovery(_:)`, `toggleSelection(_:)`
  - `enum PanelRoute: Codable, Hashable { case activate([RoleKey]), configureRoles(TenantKey), addTenant(String), discoverTenants(String) }`
  - `protocol ExpiryNotifying: Sendable { func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String]) async }`

- [ ] **Step 1: Write PanelRoute**

`Sources/PimTrayApp/App/PanelRoute.swift`:
```swift
import Foundation
import PimTrayCore

enum PanelRoute: Codable, Hashable {
    case activate([RoleKey])
    case configureRoles(TenantKey)
    case addTenant(String)        // identity id
    case discoverTenants(String)  // identity id
}

protocol ExpiryNotifying: Sendable {
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String]) async
}

struct NoopNotifier: ExpiryNotifying {
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String]) async {}
}
```

- [ ] **Step 2: Write AppModel**

`Sources/PimTrayApp/App/AppModel.swift`:
```swift
import Foundation
import Observation
import PimTrayCore

@MainActor
@Observable
final class AppModel {
    // Persisted
    private(set) var state = AppState()
    // Session
    private(set) var roles: [TenantKey: [EligibleRole]] = [:]
    private(set) var active: [RoleKey: ActiveAssignment] = [:]
    private(set) var busy: Set<TenantKey> = []
    private(set) var tenantErrors: [TenantKey: String] = [:]
    private(set) var progress: [RoleKey: ActivationOutcome.Result] = [:]
    var selectMode = false { didSet { if !selectMode { selection.removeAll() } } }
    var selection: Set<RoleKey> = []
    var fatalError: String?
    var pendingExtend: RoleKey?

    private let tokens: any TokenProviding
    private let coordinator: ActivationCoordinator
    private let discovery: TenantDiscovery
    private let store: AppStateStore
    private let notifier: any ExpiryNotifying
    private var refreshTimer: Task<Void, Never>?

    init(tokens: any TokenProviding, http: any HTTPClient, store: AppStateStore, notifier: any ExpiryNotifying) {
        self.tokens = tokens
        self.store = store
        self.notifier = notifier
        coordinator = ActivationCoordinator(providers: [EntraDirectoryProvider(http: http, tokens: tokens), AzureResourceProvider(), GroupProvider()], tokens: tokens)
        discovery = TenantDiscovery(http: http, tokens: tokens)
    }

    /// Production wiring. Errors surface through `fatalError` so the panel can show them.
    static func live() -> AppModel {
        let anchor = AuthAnchorWindow()
        do {
            let config = try AppConfig.load()
            let tokens = try MSALTokenProvider(clientId: config.clientId, redirectUri: config.redirectUri, anchor: anchor)
            return AppModel(tokens: tokens, http: URLSessionHTTPClient(), store: AppStateStore(), notifier: ExpiryNotifier())
        } catch {
            let model = AppModel(tokens: UnavailableTokenProvider(), http: URLSessionHTTPClient(), store: AppStateStore(), notifier: NoopNotifier())
            model.fatalError = error.localizedDescription
            return model
        }
    }

    // MARK: Derived

    var identities: [Identity] { state.identities }
    var activeCount: Int { active.values.filter { $0.status == .active }.count }
    func tenants(for identityId: String) -> [TenantContext] { state.tenants(for: identityId) }
    func roles(for tenantKey: TenantKey) -> [EligibleRole] { roles[tenantKey] ?? [] }
    func role(for key: RoleKey) -> EligibleRole? { roles[key.tenantKey]?.first { $0.key == key } }
    func assignment(for key: RoleKey) -> ActiveAssignment? { active[key] }
    func remembered(for key: RoleKey) -> RoleMemory? { state.memory(for: key) }
    func identity(_ id: String) -> Identity? { state.identities.first { $0.id == id } }
    func tenant(_ key: TenantKey) -> TenantContext? { state.tenants.first { $0.id == key } }

    func adminConsentURL(tenantId: String) -> URL? {
        guard let config = try? AppConfig.load() else { return nil }
        return URL(string: "https://login.microsoftonline.com/\(tenantId)/adminconsent?client_id=\(config.clientId)")
    }

    // MARK: Lifecycle

    func bootstrap() async {
        if let loaded = try? await store.load() { state = loaded }
        // Reconcile with MSAL's cache: drop identities MSAL no longer knows.
        if let known = try? await tokens.identities() {
            let ids = Set(known.map(\.id))
            for identity in state.identities where !ids.contains(identity.id) { state.removeIdentity(identity.id) }
        }
        persist()
        await refreshAll()
        startTimer()
    }

    private func startTimer() {
        refreshTimer?.cancel()
        refreshTimer = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(60))
                guard let self, self.activeCount > 0 else { continue }
                await self.refreshAll()
            }
        }
    }

    private func persist() {
        let snapshot = state
        Task { try? await store.save(snapshot) }
    }

    // MARK: Accounts

    func addAccount() async {
        do {
            let identity = try await tokens.signIn()
            if !state.identities.contains(where: { $0.id == identity.id }) {
                state.identities.append(identity)
            }
            let homeKey = TenantKey(identityId: identity.id, tenantId: identity.homeTenantId)
            if tenant(homeKey) == nil {
                let name = (try? await discovery.tenantDisplayName(identity: identity, tenantId: identity.homeTenantId)) ?? identity.homeTenantId
                state.upsertTenant(TenantContext(identityId: identity.id, tenantId: identity.homeTenantId, displayName: name, source: .home))
            }
            persist()
            await refresh(homeKey)
        } catch {
            fatalError = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }

    func signOut(_ identity: Identity) {
        Task {
            try? await tokens.signOut(identity)
            state.removeIdentity(identity.id)
            for key in roles.keys where key.identityId == identity.id { roles[key] = nil }
            active = active.filter { $0.key.identityId != identity.id }
            persist()
        }
    }

    // MARK: Tenants

    func addTenant(identityId: String, domainOrId: String) async throws {
        guard let identity = self.identity(identityId) else { throw PIMError.unexpected(status: 0, body: "Unknown identity") }
        let tenantId = try await discovery.resolveTenantId(domainOrId: domainOrId)
        let key = TenantKey(identityId: identityId, tenantId: tenantId)
        guard tenant(key) == nil else { return }
        let name = (try? await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: tenantId, scopes: [GraphScopes.userRead]) {
            try await discovery.tenantDisplayName(identity: identity, tenantId: tenantId)
        }) ?? domainOrId
        state.upsertTenant(TenantContext(identityId: identityId, tenantId: tenantId, displayName: name, source: .manual))
        persist()
        await refresh(key)
    }

    func discoverTenants(identityId: String) async throws -> [DiscoveredTenant] {
        guard let identity = self.identity(identityId) else { return [] }
        return try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: identity.homeTenantId, scopes: ArmScopes.all) {
            try await discovery.discoverTenants(identity: identity)
        }
    }

    func trackTenants(identityId: String, tenants: [DiscoveredTenant]) async {
        for t in tenants {
            let key = TenantKey(identityId: identityId, tenantId: t.tenantId)
            guard tenant(key) == nil else { continue }
            state.upsertTenant(TenantContext(identityId: identityId, tenantId: t.tenantId, displayName: t.displayName, source: .discovered))
        }
        persist()
        await withTaskGroup(of: Void.self) { group in
            for t in tenants { group.addTask { await self.refresh(TenantKey(identityId: identityId, tenantId: t.tenantId)) } }
        }
    }

    func removeTenant(_ key: TenantKey) {
        state.removeTenant(key)
        roles[key] = nil
        active = active.filter { $0.key.tenantKey != key }
        persist()
    }

    func retryDiscovery(_ key: TenantKey) async {
        guard var t = self.tenant(key) else { return }
        t.discoveryMode = .automatic
        t.lastDiscoveryError = nil
        state.upsertTenant(t)
        persist()
        await refresh(key)
    }

    // MARK: Refresh

    func refreshAll() async {
        await withTaskGroup(of: Void.self) { group in
            for t in state.tenants { group.addTask { await self.refresh(t.id) } }
        }
    }

    func refresh(_ key: TenantKey) async {
        guard let identity = self.identity(key.identityId), var tenant = self.tenant(key),
              let provider = coordinator.provider(for: .entraDirectory) else { return }
        busy.insert(key)
        defer { busy.remove(key) }
        tenantErrors[key] = nil

        var discovered: [EligibleRole] = []
        if tenant.discoveryMode == .automatic {
            do {
                discovered = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) {
                    try await provider.eligibleRoles(identity: identity, tenant: tenant)
                }
                discovered = await withTaskGroup(of: EligibleRole.self) { group in
                    for role in discovered {
                        group.addTask {
                            var r = role
                            r.policy = (try? await provider.policy(for: role, identity: identity)) ?? .manualDefault
                            return r
                        }
                    }
                    var out: [EligibleRole] = []
                    for await r in group { out.append(r) }
                    return out.sorted { $0.displayName < $1.displayName }
                }
            } catch PIMError.consentRequired {
                tenant.discoveryMode = .manualRoles
                tenant.lastDiscoveryError = "Role discovery not permitted in this tenant. Configure known roles or ask an admin to consent."
                state.upsertTenant(tenant)
                persist()
            } catch {
                tenantErrors[key] = (error as? PIMError)?.userMessage ?? error.localizedDescription
            }
        }
        let manual = ManualRoleSource.eligibleRoles(from: state.manualRoles, tenantKey: key)
        roles[key] = ManualRoleSource.merge(discovered: discovered, manual: manual)

        do {
            let current = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) {
                try await provider.activeAssignments(identity: identity, tenant: tenant)
            }
            active = active.filter { $0.key.tenantKey != key }
            for a in current { active[a.roleKey] = a }
            await rescheduleNotifications()
        } catch {
            if tenantErrors[key] == nil { tenantErrors[key] = (error as? PIMError)?.userMessage ?? error.localizedDescription }
        }
    }

    private func rescheduleNotifications() async {
        var names: [RoleKey: String] = [:]
        for list in roles.values { for r in list { names[r.key] = r.displayName } }
        await notifier.reschedule(assignments: Array(active.values), names: names)
    }

    // MARK: Activation

    /// Activates the requests. Roles that are already active are deactivated first so "Extend" works.
    func activate(_ requests: [ActivationRequest]) async {
        for r in requests { progress[r.roleKey] = nil }
        for r in requests {
            if let existing = active[r.roleKey], existing.status == .active, let identity = self.identity(r.roleKey.identityId) {
                try? await coordinator.deactivate(existing, identity: identity)
            }
        }
        let outcomes = await coordinator.activate(requests, identities: state.identities) { outcome in
            Task { @MainActor in self.progress[outcome.roleKey] = outcome.result }
        }
        for outcome in outcomes {
            progress[outcome.roleKey] = outcome.result
            guard let request = requests.first(where: { $0.roleKey == outcome.roleKey }) else { continue }
            switch outcome.result {
            case .activated(let a):
                active[a.roleKey] = a
                state.remember(roleKey: request.roleKey, justification: request.justification, duration: request.duration)
            case .pendingApproval:
                active[request.roleKey] = ActiveAssignment(roleKey: request.roleKey, assignmentId: nil, startDateTime: .now, endDateTime: nil, status: .pendingApproval)
                state.remember(roleKey: request.roleKey, justification: request.justification, duration: request.duration)
            case .failed:
                break
            }
        }
        persist()
        selectMode = false
        await rescheduleNotifications()
    }

    func deactivate(_ key: RoleKey) async {
        guard let a = active[key], let identity = self.identity(key.identityId) else { return }
        do {
            try await coordinator.deactivate(a, identity: identity)
            active[key] = nil
            await rescheduleNotifications()
        } catch {
            tenantErrors[key.tenantKey] = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }

    func clearProgress(_ keys: [RoleKey]) { for k in keys { progress[k] = nil } }

    func toggleSelection(_ key: RoleKey) {
        if selection.contains(key) { selection.remove(key) } else { selection.insert(key) }
    }

    // MARK: Manual roles

    func setManualRoles(_ manual: [ManualRole], for key: TenantKey) {
        state.manualRoles.removeAll { $0.tenantKey == key }
        state.manualRoles += manual
        persist()
        let discovered = roles(for: key).filter { $0.source == .discovered }
        roles[key] = ManualRoleSource.merge(discovered: discovered, manual: ManualRoleSource.eligibleRoles(from: manual, tenantKey: key))
    }

    func manualRoles(for key: TenantKey) -> [ManualRole] { state.manualRoles.filter { $0.tenantKey == key } }
}

/// Used only when configuration failed to load; every call fails with a clear message.
struct UnavailableTokenProvider: TokenProviding {
    func signIn() async throws -> Identity { throw PIMError.unexpected(status: 0, body: "Configuration missing") }
    func signOut(_ identity: Identity) async throws {}
    func identities() async throws -> [Identity] { [] }
    func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String { throw PIMError.unexpected(status: 0, body: "Configuration missing") }
    func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String { throw PIMError.unexpected(status: 0, body: "Configuration missing") }
}
```

`ExpiryNotifier` is created in Task 15. Until then add a temporary line at the bottom of `PanelRoute.swift` so this task compiles:
```swift
typealias ExpiryNotifier = NoopNotifier
```
Task 15 removes it.

- [ ] **Step 3: Build**

```bash
xcodegen generate && xcodebuild -project PimTray.xcodeproj -scheme PimTrayApp -configuration Debug -derivedDataPath build build 2>&1 | grep -E "error:|BUILD" | head -20
```
Expected: `** BUILD SUCCEEDED **`. Fix any strict-concurrency diagnostics by adding `@MainActor` hops rather than `@unchecked Sendable` in the model.

- [ ] **Step 4: Commit**

```bash
git add Sources/PimTrayApp/App
git commit -m "Add observable AppModel wiring core services to the app"
```

---

### Task 13: Menu bar panel views

**Files:**
- Modify: `Sources/PimTrayApp/App/PimTrayApp.swift` (replace placeholder)
- Create: `Sources/PimTrayApp/Views/PanelView.swift`, `Sources/PimTrayApp/Views/IdentitySection.swift`, `Sources/PimTrayApp/Views/TenantSection.swift`, `Sources/PimTrayApp/Views/RoleRow.swift`, `Sources/PimTrayApp/Views/RouteWindow.swift`

**Interfaces:**
- Consumes: `AppModel`, `PanelRoute`, `Countdown`.
- Produces: `PanelView`, `IdentitySection(identity:)`, `TenantSection(tenant:)`, `RoleRow(role:)`, `RouteWindow(route:)` (stub bodies for Task 14 routes), `MenuBarLabel`.

- [ ] **Step 1: Replace PimTrayApp.swift**

```swift
import SwiftUI
import PimTrayCore

@main
struct PimTrayApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @State private var model = AppModel.live()

    var body: some Scene {
        MenuBarExtra {
            PanelView()
                .environment(model)
                .task { await model.bootstrap() }
        } label: {
            MenuBarLabel()
                .environment(model)
        }
        .menuBarExtraStyle(.window)

        WindowGroup("PimTray", for: PanelRoute.self) { $route in
            if let route {
                RouteWindow(route: route).environment(model)
            }
        }
        .windowResizability(.contentSize)
        .defaultLaunchBehavior(.suppressed)
    }
}

struct MenuBarLabel: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        HStack(spacing: 3) {
            Image(systemName: model.activeCount > 0 ? "checkmark.shield.fill" : "shield")
            if model.activeCount > 0 { Text("\(model.activeCount)").monospacedDigit() }
        }
        .onChange(of: model.pendingExtend) { _, key in
            guard let key else { return }
            openWindow(value: PanelRoute.activate([key]))
            NSApp.activate(ignoringOtherApps: true)
            model.pendingExtend = nil
        }
    }
}
```

- [ ] **Step 2: Write PanelView**

`Sources/PimTrayApp/Views/PanelView.swift`:
```swift
import SwiftUI
import PimTrayCore

struct PanelView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        @Bindable var model = model
        VStack(spacing: 0) {
            header
            Divider()
            if let fatal = model.fatalError {
                ContentUnavailableView("PimTray cannot start", systemImage: "exclamationmark.triangle", description: Text(fatal))
                    .frame(height: 200)
            } else if model.identities.isEmpty {
                ContentUnavailableView("No accounts", systemImage: "person.crop.circle.badge.plus",
                                       description: Text("Add an account to see your PIM roles."))
                    .frame(height: 160)
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 8) {
                        ForEach(model.identities) { identity in
                            IdentitySection(identity: identity)
                        }
                    }
                    .padding(10)
                }
                .frame(maxHeight: 520)
            }
            if model.selectMode {
                Divider()
                Button {
                    openWindow(value: PanelRoute.activate(Array(model.selection).sorted { "\($0)" < "\($1)" }))
                    NSApp.activate(ignoringOtherApps: true)
                } label: {
                    Text("Activate \(model.selection.count) role\(model.selection.count == 1 ? "" : "s")")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .disabled(model.selection.isEmpty)
                .padding(10)
            }
            Divider()
            footer
        }
        .frame(width: 380)
    }

    private var header: some View {
        @Bindable var model = model
        return HStack {
            Text("PimTray").font(.headline)
            Spacer()
            Toggle(isOn: $model.selectMode) { Image(systemName: "checklist") }
                .toggleStyle(.button)
                .help("Select several roles to activate together")
            Button { Task { await model.refreshAll() } } label: { Image(systemName: "arrow.clockwise") }
                .help("Refresh")
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private var footer: some View {
        HStack {
            Button("Add account…") { Task { await model.addAccount() } }
            Spacer()
            Button("Quit") { NSApp.terminate(nil) }
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }
}
```

- [ ] **Step 3: Write IdentitySection and TenantSection**

`Sources/PimTrayApp/Views/IdentitySection.swift`:
```swift
import SwiftUI
import PimTrayCore

struct IdentitySection: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let identity: Identity

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Image(systemName: "person.crop.circle")
                Text(identity.upn).font(.subheadline.weight(.semibold))
                Spacer()
                Menu {
                    Button("Discover tenants…") { open(.discoverTenants(identity.id)) }
                    Button("Add tenant…") { open(.addTenant(identity.id)) }
                    Divider()
                    Button("Sign out", role: .destructive) { model.signOut(identity) }
                } label: { Image(systemName: "ellipsis.circle") }
                .menuStyle(.borderlessButton)
                .fixedSize()
            }
            ForEach(model.tenants(for: identity.id)) { tenant in
                TenantSection(tenant: tenant)
            }
        }
        .padding(8)
        .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 10))
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }
}
```

`Sources/PimTrayApp/Views/TenantSection.swift`:
```swift
import SwiftUI
import PimTrayCore

struct TenantSection: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    @State private var expanded = true
    let tenant: TenantContext

    private var roles: [EligibleRole] { model.roles(for: tenant.id) }
    private var activeCount: Int { roles.filter { model.assignment(for: $0.key)?.status == .active }.count }

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            if roles.isEmpty {
                Text(tenant.discoveryMode == .manualRoles ? "No roles configured." : "No eligible roles.")
                    .font(.caption).foregroundStyle(.secondary).padding(.leading, 4)
            }
            ForEach(roles) { role in RoleRow(role: role) }
            if let err = model.tenantErrors[tenant.id] ?? tenant.lastDiscoveryError {
                Label(err, systemImage: "exclamationmark.circle").font(.caption).foregroundStyle(.orange)
            }
        } label: {
            HStack(spacing: 6) {
                Text(tenant.displayName).font(.subheadline)
                if tenant.source == .home { Text("home").font(.caption2).foregroundStyle(.secondary) }
                if tenant.discoveryMode == .manualRoles {
                    Text("manual roles").font(.caption2).padding(.horizontal, 5).padding(.vertical, 1)
                        .background(.orange.opacity(0.2), in: Capsule())
                }
                if model.busy.contains(tenant.id) { ProgressView().controlSize(.mini) }
                Spacer()
                if activeCount > 0 { Text("\(activeCount) active").font(.caption).foregroundStyle(.green) }
                Menu {
                    Button("Configure known PIM roles…") { open(.configureRoles(tenant.id)) }
                    Button("Retry discovery") { Task { await model.retryDiscovery(tenant.id) } }
                    if tenant.discoveryMode == .manualRoles, let url = model.adminConsentURL(tenantId: tenant.tenantId) {
                        Button("Open admin consent link…") { NSWorkspace.shared.open(url) }
                    }
                    Divider()
                    Button("Remove tenant", role: .destructive) { model.removeTenant(tenant.id) }
                        .disabled(tenant.source == .home)
                } label: { Image(systemName: "ellipsis.circle") }
                .menuStyle(.borderlessButton)
                .fixedSize()
            }
        }
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }
}
```

- [ ] **Step 4: Write RoleRow**

`Sources/PimTrayApp/Views/RoleRow.swift`:
```swift
import SwiftUI
import PimTrayCore

struct RoleRow: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let role: EligibleRole

    private var assignment: ActiveAssignment? { model.assignment(for: role.key) }

    var body: some View {
        HStack(spacing: 8) {
            if model.selectMode {
                Toggle("", isOn: Binding(get: { model.selection.contains(role.key) }, set: { _ in model.toggleSelection(role.key) }))
                    .labelsHidden()
                    .disabled(assignment?.status == .active)
            }
            statusDot
            VStack(alignment: .leading, spacing: 1) {
                Text(role.displayName).font(.body)
                if role.source == .manual { Text("manual").font(.caption2).foregroundStyle(.secondary) }
            }
            Spacer()
            trailing
        }
        .padding(.vertical, 3)
        .padding(.leading, 4)
    }

    @ViewBuilder private var statusDot: some View {
        switch assignment?.status {
        case .active: Circle().fill(.green).frame(width: 8, height: 8)
        case .pendingApproval, .pendingProvisioning: Circle().fill(.yellow).frame(width: 8, height: 8)
        case .failed: Circle().fill(.red).frame(width: 8, height: 8)
        case nil: Circle().stroke(.secondary).frame(width: 8, height: 8)
        }
    }

    @ViewBuilder private var trailing: some View {
        switch assignment?.status {
        case .active:
            if let end = assignment?.endDateTime {
                TimelineView(.periodic(from: .now, by: 1)) { ctx in
                    Text(Countdown.remaining(until: end, now: ctx.date).map(Countdown.label) ?? "expired")
                        .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                }
            }
            Button("Deactivate") { Task { await model.deactivate(role.key) } }
                .controlSize(.small)
        case .pendingApproval:
            Text("awaiting approval").font(.caption).foregroundStyle(.secondary)
        case .pendingProvisioning:
            Text("provisioning").font(.caption).foregroundStyle(.secondary)
        case .failed(let m):
            Text(m).font(.caption).foregroundStyle(.red).lineLimit(1)
        case nil:
            if !model.selectMode {
                Button("Activate") {
                    openWindow(value: PanelRoute.activate([role.key]))
                    NSApp.activate(ignoringOtherApps: true)
                }
                .controlSize(.small)
            }
        }
    }
}
```

- [ ] **Step 5: Write RouteWindow with placeholders for Task 14**

`Sources/PimTrayApp/Views/RouteWindow.swift`:
```swift
import SwiftUI
import PimTrayCore

struct RouteWindow: View {
    let route: PanelRoute

    var body: some View {
        switch route {
        case .activate(let keys): ActivationView(keys: keys)
        case .configureRoles(let tenantKey): ConfigureRolesView(tenantKey: tenantKey)
        case .addTenant(let identityId): AddTenantView(identityId: identityId)
        case .discoverTenants(let identityId): DiscoverTenantsView(identityId: identityId)
        }
    }
}

// Temporary placeholders, replaced in Task 14.
struct ActivationView: View { let keys: [RoleKey]; var body: some View { Text("Activate \(keys.count)").padding() } }
struct ConfigureRolesView: View { let tenantKey: TenantKey; var body: some View { Text("Configure").padding() } }
struct AddTenantView: View { let identityId: String; var body: some View { Text("Add tenant").padding() } }
struct DiscoverTenantsView: View { let identityId: String; var body: some View { Text("Discover").padding() } }
```

- [ ] **Step 6: Build and run**

```bash
xcodegen generate && xcodebuild -project PimTray.xcodeproj -scheme PimTrayApp -configuration Debug -derivedDataPath build build 2>&1 | grep -E "error:|BUILD" | head -20
open build/Build/Products/Debug/PimTray.app
```
Expected: build succeeds; with a real `ClientId` in `PimTrayConfig.plist`, "Add account…" opens the browser sign-in, and after sign-in the home tenant appears with roles or the "manual roles" badge. Quit with `pkill -x PimTray`.

- [ ] **Step 7: Commit**

```bash
git add Sources/PimTrayApp
git commit -m "Add menu bar panel with identity, tenant and role rows"
```

---

### Task 14: Activation window, configure-roles window, tenant sheets

**Files:**
- Modify: `Sources/PimTrayApp/Views/RouteWindow.swift` (delete the four placeholders)
- Create: `Sources/PimTrayApp/Views/ActivationView.swift`, `Sources/PimTrayApp/Views/ConfigureRolesView.swift`, `Sources/PimTrayApp/Views/TenantSheets.swift`

**Interfaces:**
- Consumes: `AppModel`, `RoleCatalogue`, `ManualRole`, `DiscoveredTenant`.
- Produces: `ActivationView(keys:)`, `ConfigureRolesView(tenantKey:)`, `AddTenantView(identityId:)`, `DiscoverTenantsView(identityId:)`.

- [ ] **Step 1: Delete the placeholders from RouteWindow.swift**

Remove the four `struct ... View` lines under the "Temporary placeholders" comment.

- [ ] **Step 2: Write ActivationView (single and bulk share one view)**

`Sources/PimTrayApp/Views/ActivationView.swift`:
```swift
import SwiftUI
import PimTrayCore

struct ActivationView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let keys: [RoleKey]

    struct Item: Identifiable {
        let role: EligibleRole
        var duration: Duration
        var id: RoleKey { role.key }
    }

    @State private var items: [Item] = []
    @State private var justification = ""
    @State private var ticketNumber = ""
    @State private var ticketSystem = ""
    @State private var running = false

    private var isBulk: Bool { keys.count > 1 }
    private var needsTicket: Bool { items.contains { $0.role.policy.requiresTicket } }
    private var justificationRequired: Bool { items.contains { $0.role.policy.requiresJustification } }
    private var canSubmit: Bool { !running && !items.isEmpty && (!justificationRequired || !justification.trimmingCharacters(in: .whitespaces).isEmpty) }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(isBulk ? "Activate \(keys.count) roles" : (items.first?.role.displayName ?? "Activate role")).font(.title3.weight(.semibold))
            if isBulk { bulkTable } else { singleDuration }
            TextField("Reason", text: $justification, axis: .vertical).lineLimit(2...4)
            if needsTicket {
                HStack {
                    TextField("Ticket number", text: $ticketNumber)
                    TextField("Ticket system", text: $ticketSystem)
                }
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button(isBulk ? "Activate all" : "Activate") { Task { await submit() } }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent).disabled(!canSubmit)
            }
        }
        .padding(16)
        .frame(width: isBulk ? 560 : 380)
        .onAppear(perform: load)
    }

    private var singleDuration: some View {
        Group {
            if let item = items.first {
                DurationPicker(duration: Binding(get: { items[0].duration }, set: { items[0].duration = $0 }), maximum: item.role.policy.maximumDuration)
                if item.role.policy.requiresApproval { Label("This role requires approval", systemImage: "person.badge.clock").font(.caption) }
            }
        }
    }

    private var bulkTable: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(groupedTenantKeys, id: \.self) { tk in
                Text("\(model.identity(tk.identityId)?.upn ?? tk.identityId) · \(model.tenant(tk)?.displayName ?? tk.tenantId)")
                    .font(.caption.weight(.semibold)).foregroundStyle(.secondary).padding(.top, 4)
                ForEach($items) { $item in
                    if item.role.key.tenantKey == tk { HStack {
                        Text(item.role.displayName)
                        Spacer()
                        DurationPicker(duration: $item.duration, maximum: item.role.policy.maximumDuration).frame(width: 150)
                        progressLabel(for: item.role.key).frame(width: 120, alignment: .trailing)
                    } }
                }
            }
        }
    }

    private var groupedTenantKeys: [TenantKey] {
        var seen: [TenantKey] = []
        for i in items where !seen.contains(i.role.key.tenantKey) { seen.append(i.role.key.tenantKey) }
        return seen
    }

    @ViewBuilder private func progressLabel(for key: RoleKey) -> some View {
        switch model.progress[key] {
        case .activated: Label("Active", systemImage: "checkmark.circle.fill").foregroundStyle(.green).font(.caption)
        case .pendingApproval: Label("Pending", systemImage: "clock").foregroundStyle(.yellow).font(.caption)
        case .failed(let e): Text(e.userMessage).foregroundStyle(.red).font(.caption).lineLimit(1).help(e.userMessage)
        case nil: running ? AnyView(ProgressView().controlSize(.small)) : AnyView(EmptyView())
        }
    }

    private func load() {
        items = keys.compactMap { key in
            guard let role = model.role(for: key) else { return nil }
            let remembered = model.remembered(for: key)?.lastDuration
            let d = min(remembered ?? role.policy.defaultDuration, role.policy.maximumDuration)
            return Item(role: role, duration: d)
        }
        justification = keys.compactMap { model.remembered(for: $0)?.justification }.first ?? ""
        model.clearProgress(keys)
    }

    private func submit() async {
        running = true
        let ticket = needsTicket && !ticketNumber.isEmpty ? TicketInfo(number: ticketNumber, system: ticketSystem) : nil
        let requests = items.map { ActivationRequest(roleKey: $0.role.key, duration: $0.duration, justification: justification, ticket: ticket) }
        await model.activate(requests)
        running = false
        let allOk = requests.allSatisfy { if case .failed = model.progress[$0.roleKey] { false } else { true } }
        if allOk { dismiss() }
    }
}

/// 30-minute steps from 30 minutes up to `maximum`.
struct DurationPicker: View {
    @Binding var duration: Duration
    let maximum: Duration

    private var options: [Duration] {
        let maxMinutes = max(30, Int(maximum.components.seconds / 60))
        return stride(from: 30, through: maxMinutes, by: 30).map { .seconds($0 * 60) }
    }

    var body: some View {
        Picker("Duration", selection: $duration) {
            ForEach(options, id: \.self) { d in Text(label(d)).tag(d) }
        }
        .onAppear { if !options.contains(duration) { duration = options.last ?? .seconds(1800) } }
    }

    private func label(_ d: Duration) -> String {
        let m = Int(d.components.seconds / 60)
        return m % 60 == 0 ? "\(m / 60) h" : "\(m / 60) h \(m % 60) min"
    }
}
```

- [ ] **Step 3: Write ConfigureRolesView**

`Sources/PimTrayApp/Views/ConfigureRolesView.swift`:
```swift
import SwiftUI
import PimTrayCore

struct ConfigureRolesView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let tenantKey: TenantKey

    @State private var catalogue: [CatalogueRole] = []
    @State private var search = ""
    @State private var selectedEntra: Set<String> = []          // template ids
    @State private var azure: [AzureRow] = []
    @State private var groups: [GroupRow] = []

    struct AzureRow: Identifiable { let id = UUID(); var scope = ""; var roleName = "Contributor" }
    struct GroupRow: Identifiable { let id = UUID(); var groupId = ""; var displayName = ""; var access: GroupAccess = .member }

    static let azureRoleNames = ["Owner", "Contributor", "Reader", "User Access Administrator", "Key Vault Administrator", "Storage Blob Data Contributor"]

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Known PIM roles in \(model.tenant(tenantKey)?.displayName ?? tenantKey.tenantId)").font(.title3.weight(.semibold))
            Text("Roles you believe you are eligible for. Activation is still validated by Entra.").font(.caption).foregroundStyle(.secondary)
            TabView {
                entraTab.tabItem { Text("Entra roles") }
                azureTab.tabItem { Text("Azure resources") }
                groupsTab.tabItem { Text("Groups") }
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Save") { save() }.keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
            }
        }
        .padding(16)
        .frame(width: 560, height: 520)
        .onAppear(perform: load)
    }

    private var entraTab: some View {
        VStack {
            TextField("Search roles", text: $search)
            List(filtered) { role in
                Toggle(isOn: Binding(get: { selectedEntra.contains(role.templateId) },
                                     set: { on in if on { selectedEntra.insert(role.templateId) } else { selectedEntra.remove(role.templateId) } })) {
                    VStack(alignment: .leading) {
                        HStack { Text(role.displayName); if role.isPrivileged { Text("privileged").font(.caption2).foregroundStyle(.orange) } }
                        Text(role.description).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    }
                }
            }
        }
    }

    private var filtered: [CatalogueRole] {
        search.isEmpty ? catalogue : catalogue.filter { $0.displayName.localizedCaseInsensitiveContains(search) }
    }

    private var azureTab: some View {
        VStack(alignment: .leading) {
            Text("Scope is the full resource id, e.g. /subscriptions/<id> or /subscriptions/<id>/resourceGroups/<name>.").font(.caption).foregroundStyle(.secondary)
            List($azure) { $row in
                HStack {
                    TextField("Scope", text: $row.scope)
                    Picker("", selection: $row.roleName) {
                        ForEach(Self.azureRoleNames, id: \.self) { Text($0).tag($0) }
                    }.frame(width: 220)
                    Button { azure.removeAll { $0.id == row.id } } label: { Image(systemName: "minus.circle") }.buttonStyle(.borderless)
                }
            }
            Button("Add row") { azure.append(AzureRow()) }
            Text("Activation for Azure resource roles arrives in phase 2.").font(.caption2).foregroundStyle(.secondary)
        }
    }

    private var groupsTab: some View {
        VStack(alignment: .leading) {
            List($groups) { $row in
                HStack {
                    TextField("Group id", text: $row.groupId)
                    TextField("Display name", text: $row.displayName)
                    Picker("", selection: $row.access) { Text("Member").tag(GroupAccess.member); Text("Owner").tag(GroupAccess.owner) }.frame(width: 110)
                    Button { groups.removeAll { $0.id == row.id } } label: { Image(systemName: "minus.circle") }.buttonStyle(.borderless)
                }
            }
            Button("Add row") { groups.append(GroupRow()) }
            Text("Activation for PIM for Groups arrives in phase 3.").font(.caption2).foregroundStyle(.secondary)
        }
    }

    private func load() {
        catalogue = (try? RoleCatalogue.entraBuiltInRoles()) ?? []
        for m in model.manualRoles(for: tenantKey) {
            switch m.scope {
            case .entraDirectory(let id, _): selectedEntra.insert(id)
            case .azureResource(let scope, let roleDefinitionId): azure.append(AzureRow(scope: scope, roleName: roleDefinitionId))
            case .group(let gid, let access): groups.append(GroupRow(groupId: gid, displayName: m.displayName, access: access))
            }
        }
    }

    private func save() {
        var manual: [ManualRole] = []
        for role in catalogue where selectedEntra.contains(role.templateId) {
            manual.append(ManualRole(tenantKey: tenantKey, scope: .entraDirectory(roleDefinitionId: role.templateId, directoryScopeId: "/"), displayName: role.displayName))
        }
        for row in azure where !row.scope.trimmingCharacters(in: .whitespaces).isEmpty {
            manual.append(ManualRole(tenantKey: tenantKey, scope: .azureResource(scope: row.scope.trimmingCharacters(in: .whitespaces), roleDefinitionId: row.roleName), displayName: "\(row.roleName) · \(row.scope)"))
        }
        for row in groups where !row.groupId.trimmingCharacters(in: .whitespaces).isEmpty {
            manual.append(ManualRole(tenantKey: tenantKey, scope: .group(groupId: row.groupId.trimmingCharacters(in: .whitespaces), accessId: row.access),
                                     displayName: row.displayName.isEmpty ? row.groupId : row.displayName))
        }
        model.setManualRoles(manual, for: tenantKey)
        dismiss()
    }
}
```

Note: built-in Entra role template IDs are the same as the tenant's `roleDefinitionId` for built-in roles, so `.entraDirectory(roleDefinitionId: templateId, ...)` is what Graph expects.

- [ ] **Step 4: Write the tenant sheets**

`Sources/PimTrayApp/Views/TenantSheets.swift`:
```swift
import SwiftUI
import PimTrayCore

struct AddTenantView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let identityId: String
    @State private var input = ""
    @State private var error: String?
    @State private var working = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Add tenant for \(model.identity(identityId)?.upn ?? identityId)").font(.title3.weight(.semibold))
            TextField("Tenant id or verified domain (e.g. fabrikam.com)", text: $input)
            if let error { Text(error).font(.caption).foregroundStyle(.red) }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Add") { Task { await add() } }.keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                    .disabled(working || input.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(16).frame(width: 420)
    }

    private func add() async {
        working = true
        defer { working = false }
        do {
            try await model.addTenant(identityId: identityId, domainOrId: input)
            dismiss()
        } catch {
            self.error = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }
}

struct DiscoverTenantsView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let identityId: String
    @State private var found: [DiscoveredTenant] = []
    @State private var chosen: Set<String> = []
    @State private var error: String?
    @State private var loading = true

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Tenants for \(model.identity(identityId)?.upn ?? identityId)").font(.title3.weight(.semibold))
            if loading { ProgressView("Asking Azure Resource Manager…") }
            else if let error { Text(error).font(.caption).foregroundStyle(.red) }
            else {
                List(found) { t in
                    let tracked = model.tenant(TenantKey(identityId: identityId, tenantId: t.tenantId)) != nil
                    Toggle(isOn: Binding(get: { tracked || chosen.contains(t.tenantId) },
                                         set: { on in if on { chosen.insert(t.tenantId) } else { chosen.remove(t.tenantId) } })) {
                        VStack(alignment: .leading) {
                            Text(t.displayName)
                            Text(t.defaultDomain ?? t.tenantId).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .disabled(tracked)
                }
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Track selected") { Task { await track() } }.keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                    .disabled(chosen.isEmpty)
            }
        }
        .padding(16).frame(width: 460, height: 380)
        .task {
            do { found = try await model.discoverTenants(identityId: identityId) }
            catch { self.error = (error as? PIMError)?.userMessage ?? error.localizedDescription }
            loading = false
        }
    }

    private func track() async {
        await model.trackTenants(identityId: identityId, tenants: found.filter { chosen.contains($0.tenantId) })
        dismiss()
    }
}
```

- [ ] **Step 5: Build and exercise**

```bash
xcodegen generate && xcodebuild -project PimTray.xcodeproj -scheme PimTrayApp -configuration Debug -derivedDataPath build build 2>&1 | grep -E "error:|BUILD" | head -20
open build/Build/Products/Debug/PimTray.app
```
Manual checks: Activate on a role opens the window pre-filled; bulk selection opens the grouped table; Configure known PIM roles lists 136 Entra roles with search; Add tenant accepts a domain. Quit with `pkill -x PimTray`.

- [ ] **Step 6: Commit**

```bash
git add Sources/PimTrayApp
git commit -m "Add activation, configure-roles and tenant windows"
```

---

### Task 15: Expiry notifications and README

**Files:**
- Create: `Sources/PimTrayApp/Notifications/ExpiryNotifier.swift`, `README.md`
- Modify: `Sources/PimTrayApp/App/PanelRoute.swift` (remove the `typealias ExpiryNotifier = NoopNotifier` line), `Sources/PimTrayApp/App/AppModel.swift` (wire the notifier)

**Interfaces:**
- Consumes: `ExpiryNotifying`, `AppModel.pendingExtend`.
- Produces: `final class ExpiryNotifier: NSObject, ExpiryNotifying, UNUserNotificationCenterDelegate` with `static let shared`, `reschedule(assignments:names:)`, and an `onExtend: @MainActor (RoleKey) -> Void` hook.

- [ ] **Step 1: Write ExpiryNotifier**

`Sources/PimTrayApp/Notifications/ExpiryNotifier.swift`:
```swift
import Foundation
import UserNotifications
import PimTrayCore

final class ExpiryNotifier: NSObject, ExpiryNotifying, UNUserNotificationCenterDelegate, @unchecked Sendable {
    static let categoryId = "PIMTRAY_EXPIRY"
    static let extendAction = "PIMTRAY_EXTEND"
    static let leadTime: TimeInterval = 5 * 60

    /// Set by the app on launch; receives the role to re-activate.
    @MainActor var onExtend: ((RoleKey) -> Void)?

    override init() {
        super.init()
        let center = UNUserNotificationCenter.current()
        center.delegate = self
        let extend = UNNotificationAction(identifier: Self.extendAction, title: "Extend", options: [.foreground])
        center.setNotificationCategories([UNNotificationCategory(identifier: Self.categoryId, actions: [extend], intentIdentifiers: [])])
        center.requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String]) async {
        let center = UNUserNotificationCenter.current()
        center.removeAllPendingNotificationRequests()
        for a in assignments where a.status == .active {
            guard let end = a.endDateTime else { continue }
            let fireAt = end.addingTimeInterval(-Self.leadTime)
            let delay = fireAt.timeIntervalSinceNow
            guard delay > 1 else { continue }
            let content = UNMutableNotificationContent()
            content.title = "\(names[a.roleKey] ?? "PIM role") expires in 5 minutes"
            content.body = "Tenant \(a.roleKey.tenantId)"
            content.categoryIdentifier = Self.categoryId
            content.sound = .default
            if let data = try? JSONEncoder().encode(a.roleKey) { content.userInfo = ["roleKey": String(decoding: data, as: UTF8.self)] }
            let trigger = UNTimeIntervalNotificationTrigger(timeInterval: delay, repeats: false)
            let id = "expiry-" + (a.assignmentId ?? UUID().uuidString)
            try? await center.add(UNNotificationRequest(identifier: id, content: content, trigger: trigger))
        }
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        guard response.actionIdentifier == Self.extendAction || response.actionIdentifier == UNNotificationDefaultActionIdentifier,
              let json = response.notification.request.content.userInfo["roleKey"] as? String,
              let key = try? JSONDecoder().decode(RoleKey.self, from: Data(json.utf8)) else { return }
        await MainActor.run { onExtend?(key) }
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .sound]
    }
}
```

- [ ] **Step 2: Wire the extend hook**

In `AppModel.live()` keep a reference and connect it:
```swift
            let notifier = ExpiryNotifier()
            let model = AppModel(tokens: tokens, http: URLSessionHTTPClient(), store: AppStateStore(), notifier: notifier)
            notifier.onExtend = { [weak model] key in model?.pendingExtend = key }
            return model
```
Replace the previous `return AppModel(... notifier: ExpiryNotifier())` line with those four lines. Delete `typealias ExpiryNotifier = NoopNotifier` from `PanelRoute.swift`.

- [ ] **Step 3: Write README.md**

```markdown
# PimTray

macOS 26 menu bar app for activating Microsoft Entra PIM roles across several accounts and tenants.

## Prerequisites

1. Xcode 26.6 or newer, `brew install xcodegen`.
2. An Entra app registration (multi-tenant, public client):
   - Authentication → Add a platform → iOS/macOS, bundle ID `no.frodehus.pimtray`.
     The redirect URI becomes `msauth.no.frodehus.pimtray://auth`.
   - Authentication → Advanced settings → Allow public client flows: Yes.
   - API permissions (delegated, Microsoft Graph): `User.Read`,
     `RoleEligibilitySchedule.Read.Directory`, `RoleAssignmentSchedule.ReadWrite.Directory`,
     `RoleManagementPolicy.Read.Directory`. All of these need admin consent per tenant.
   - Optional: Azure Service Management → `user_impersonation` for tenant discovery.
3. `cp PimTrayConfig.plist.example PimTrayConfig.plist` and put the application (client) ID in `ClientId`.

## Build and run

```bash
xcodegen generate
xcodebuild -project PimTray.xcodeproj -scheme PimTrayApp -configuration Debug -derivedDataPath build build
open build/Build/Products/Debug/PimTray.app
```

Core tests: `swift test`.

## Tenants that refuse consent

If a tenant admin has not consented, discovery fails and the tenant switches to
"manual roles". Use the tenant menu → Configure known PIM roles… to pick the roles
you hold. Activation still requires `RoleAssignmentSchedule.ReadWrite.Directory`;
the tenant menu offers an admin-consent link you can forward.

## Manual smoke test

- Add account → browser sign-in → home tenant appears with eligible roles.
- Activate a role → reason pre-fills on the next activation → green dot and countdown.
- Deactivate → dot clears.
- Select mode → pick roles in two tenants → Activate all → grouped progress.
- Discover tenants… lists other tenants; Add tenant… accepts a domain.
- A role expiring within 5 minutes produces a notification with Extend.

## Regenerating the role catalogue

Save the markdown of https://learn.microsoft.com/entra/identity/role-based-access-control/permissions-reference
and run `perl Scripts/update-role-catalogue.pl page.md > Sources/PimTrayCore/Resources/EntraBuiltInRoles.json`.
```

- [ ] **Step 4: Build, run the full test suite, launch**

```bash
swift test 2>&1 | tail -3
xcodegen generate && xcodebuild -project PimTray.xcodeproj -scheme PimTrayApp -configuration Debug -derivedDataPath build build 2>&1 | grep -E "error:|BUILD" | head -20
open build/Build/Products/Debug/PimTray.app
```
Expected: all Core tests pass, build succeeds, first activation triggers the notification permission prompt.

- [ ] **Step 5: Commit**

```bash
git add Sources/PimTrayApp README.md
git commit -m "Add expiry notifications with Extend action and README"
```

---

## Self-review notes

- Spec §6 tenant discovery, §7.1 provider, §7.4 manual source, §8 coordinator, §9 panel/sheets/notifications, §10 error mapping, §11 tests: each has a task above. Spec §7.2 and §7.3 are stubs by design (phase 2 and 3). Spec's "offline pill" is folded into per-tenant error text rather than a separate header state.
- The `TokenProviding` shape gained `acquireInteractively` compared to the spec's single `accessToken(claims:)` so silent and interactive paths are distinct and testable.
- Extend on an active role deactivates then re-activates, because Graph rejects `selfActivate` on an already-active assignment.
