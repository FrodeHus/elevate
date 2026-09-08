# Access Packages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a signed-in user discover, request and follow Entra entitlement management access packages per tenant from the macOS menu bar app, with notifications for approvals, denials, revocations, expiries and newly eligible roles.

**Architecture:** A new `AccessPackageProvider` in `ElevateCore` talks to Graph v1.0 with the self-service entitlement scope and returns plain models; two pure value types (`AccessPackageDiff`, `NewRoleTracker`) turn polled data into notification events. The app layer adds an `AppModel+AccessPackages` extension for polling on a slow cadence, persists snapshots in `AppState`, and adds one tenant-scoped window (`AccessPackagesView`) reached through a new `PanelRoute` case, plus a box glyph and menu item on the tenant header.

**Tech Stack:** Swift 6.2, SwiftUI on macOS 26, Swift Testing (`@Test`/`#expect`), SwiftPM for `ElevateCore`, XcodeGen + xcodebuild for the app and its hosted test bundle, Microsoft Graph v1.0.

**Spec:** `docs/superpowers/specs/2026-09-08-elevate-access-packages-design.md`

## Global Constraints

- Work on branch `access-packages` (it exists and already carries the spec, the design canvas and the tutorial). `main` is protected; merge by PR.
- Core tests: run from `macos/` with `swift test`. Every Core task must leave the whole suite green.
- App (hosted) tests: from `macos/`, `xcodegen generate` once, then `xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates test`. Relaunch after UI tasks with `open macos/build/Build/Products/Debug/Elevate.app` after a `build`.
- Swift language mode 6: every new type crossing an `await` is `Sendable`; provider structs are `Sendable`; app model code is `@MainActor`.
- Graph base: `GraphTransport.graphBase` (v1.0). Do not use the beta base for anything in this plan.
- The only new Graph scope is `https://graph.microsoft.com/EntitlementMgmt-SubjectAccess.ReadWrite` (permission id `e9fdcbbb-8807-410f-b9ec-8d5468c7c2ac`). Never add `EntitlementManagement.Read.All` or `EntitlementManagement.ReadWrite.All`.
- Poll cadence: panel-open throttle 15 minutes (`900` seconds), background tick 8 hours (`8 * 3600` seconds). Access package polls never run from the one-minute active timer.
- New-role marker clears when `NewRoleTracker.panelOpened()` has been called twice while `new` is non-empty.
- Persisted state stays Windows-interop friendly: arrays of records with a `tenantKey` field, never dictionaries keyed by a struct.
- Sample data in tests and docs uses the fictional `alex.rivera@contoso.com`, never the maintainer's address.
- No attribution lines in commit messages.

---

## File map

Core (`macos/Sources/ElevateCore`):

- Modify `Auth/TokenProviding.swift`: add `EntitlementScopes`.
- Modify `Auth/AccessTokenClaims.swift`: add `permitsEntitlementSelfService`.
- Modify `Models/Identity.swift`: `TenantContext.accessPackagesAvailable`.
- Create `Models/AccessPackages.swift`: `AccessPackage`, `AccessPackageRequest`, `AccessPackageRequestState`, `AccessPackageAssignment`, `AccessPackageAssignmentState`, `PolicyRequirement`, `AccessPackageSnapshot`.
- Create `Providers/AccessPackageProvider.swift`: the Graph calls.
- Create `Coordination/AccessPackageDiff.swift`: `AccessPackageEvent`, `AccessPackageDiff`.
- Create `Coordination/NewRoleTracker.swift`: `NewRoleTracker`.
- Modify `Storage/AppState.swift`: `accessPackages: [AccessPackageRecord]`, `roleTracking: [RoleTrackingRecord]`.

Core tests (`macos/Tests/ElevateCoreTests`): `AccessPackageModelsTests.swift`, `AccessPackageProviderTests.swift`, `AccessPackageDiffTests.swift`, `NewRoleTrackerTests.swift`, additions to `AccessTokenClaimsTests.swift` and `AppStateStoreTests.swift`; fixtures `Fixtures/ap-packages.json`, `Fixtures/ap-packages-page2.json`, `Fixtures/ap-requests.json`, `Fixtures/ap-assignments.json`, `Fixtures/ap-requirements-one.json`, `Fixtures/ap-requirements-two.json`, `Fixtures/ap-requirements-questions.json`, `Fixtures/ap-request-created.json`.

App (`macos/Sources/ElevateApp`):

- Modify `App/PanelRoute.swift`: `PanelRoute.accessPackages(TenantKey)`, `PackageExpiry`, `ExpiryNotifying.setPackageExpiries`.
- Modify `Notifications/ExpiryNotifier.swift`: keep package expiries and re-add them on reschedule.
- Modify `App/AppModel.swift`: `accessPackageProvider`, `accessPackageTimer`, `accessPackageErrors`, rebuild in `applyClientId`.
- Create `App/AppModel+AccessPackages.swift`: polling, requests, notifications, expiry scheduling.
- Modify `App/AppModel+Activation.swift`: `probeAccessPackages`.
- Modify `App/AppModel+Refresh.swift`: set the tenant flag, observe new roles, poll on panel open.
- Modify `Views/TenantSection.swift`: glyph and menu item.
- Modify `Views/RoleRow.swift`: new badge and tint.
- Modify `Views/RouteWindow.swift`: route the new window.
- Create `Views/AccessPackagesView.swift`: window with four tabs.
- Create `Views/RequestPackageSheet.swift`: the request sheet.

App tests (`macos/Tests/ElevateAppTests`): `AppModelAccessPackagesTests.swift`.

Docs: `docs/entra-app-registration.md`, `docs/entra-app/required-resource-access.json`, `CHANGELOG.md`.

---

### Task 1: Scope, token claim and tenant flag

**Files:**
- Modify: `macos/Sources/ElevateCore/Auth/TokenProviding.swift:17-24`
- Modify: `macos/Sources/ElevateCore/Auth/AccessTokenClaims.swift`
- Modify: `macos/Sources/ElevateCore/Models/Identity.swift:41-90`
- Modify: `docs/entra-app-registration.md:139-147`
- Modify: `docs/entra-app/required-resource-access.json`
- Test: `macos/Tests/ElevateCoreTests/AccessTokenClaimsTests.swift`

**Interfaces:**
- Produces: `EntitlementScopes.all: [String]`; `AccessTokenClaims.permitsEntitlementSelfService(_ accessToken: String) -> Bool?`; `TenantContext.accessPackagesAvailable: Bool?`.

- [ ] **Step 1: Write the failing tests**

Append to `AccessTokenClaimsTests.swift` inside the existing suite (it already has a `token(scp:)` helper that builds a JWT-shaped string from a payload; reuse it):

```swift
    @Test func entitlementSelfServiceScopeIsDetected() {
        #expect(AccessTokenClaims.permitsEntitlementSelfService(token(scp: "User.Read EntitlementMgmt-SubjectAccess.ReadWrite")) == true)
        #expect(AccessTokenClaims.permitsEntitlementSelfService(token(scp: "User.Read RoleAssignmentSchedule.ReadWrite.Directory")) == false)
        #expect(AccessTokenClaims.permitsEntitlementSelfService("opaque-token") == nil)
    }

    @Test func entitlementScopeConstant() {
        #expect(EntitlementScopes.all == ["https://graph.microsoft.com/EntitlementMgmt-SubjectAccess.ReadWrite"])
    }

    @Test func tenantContextAccessPackagesFlagDefaultsToNilAndRoundTrips() throws {
        var t = TenantContext(identityId: "i", tenantId: "t", displayName: "Contoso", source: .home)
        #expect(t.accessPackagesAvailable == nil)
        t.accessPackagesAvailable = true
        let data = try JSONEncoder().encode(t)
        #expect(try JSONDecoder().decode(TenantContext.self, from: data).accessPackagesAvailable == true)
        // A state file written before this field existed still loads.
        let legacy = Data(#"{"identityId":"i","tenantId":"t","displayName":"Contoso","source":"home","discoveryMode":"automatic"}"#.utf8)
        #expect(try JSONDecoder().decode(TenantContext.self, from: legacy).accessPackagesAvailable == nil)
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run from `macos/`: `swift test --filter AccessTokenClaimsTests`
Expected: compile errors naming `permitsEntitlementSelfService`, `EntitlementScopes`, `accessPackagesAvailable`.

- [ ] **Step 3: Add the scope group**

In `TokenProviding.swift`, after `GroupScopes`:

```swift
/// Delegated Graph permission for self-service entitlement management (access packages).
/// User-consentable: no admin consent is needed, unlike the PIM scopes above.
public enum EntitlementScopes {
    public static let all = ["https://graph.microsoft.com/EntitlementMgmt-SubjectAccess.ReadWrite"]
    /// The bare scope name as it appears in a token's `scp` claim.
    public static let claim = "EntitlementMgmt-SubjectAccess.ReadWrite"
}
```

- [ ] **Step 4: Add the claim check**

In `AccessTokenClaims.swift`, after `permitsEntraActivation`:

```swift
    /// Whether a Graph token carries the self-service entitlement management scope.
    /// nil when the token does not expose its scopes.
    public static func permitsEntitlementSelfService(_ accessToken: String) -> Bool? {
        guard let scopes = grantedScopes(accessToken) else { return nil }
        return scopes.contains(EntitlementScopes.claim)
    }
```

- [ ] **Step 5: Add the tenant flag**

In `Identity.swift`, inside `TenantContext` after `groupsUnavailableReason`:

```swift
    /// Whether the Graph token in this tenant carries the entitlement management scope, so the
    /// access packages entry point is shown. nil until a refresh has looked at a token.
    public var accessPackagesAvailable: Bool?
```

Extend the memberwise `init` with a trailing parameter `accessPackagesAvailable: Bool? = nil` and assign it. `TenantContext` uses synthesized `Codable`, so an absent key already decodes as nil; no custom decoder is needed.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `swift test --filter AccessTokenClaimsTests`
Expected: PASS, including the three new tests.

- [ ] **Step 7: Document the permission**

In `docs/entra-app-registration.md`, add this row at the end of the permission table (after the `RoleManagementPolicy.Read.AzureADGroup` row):

```markdown
| `EntitlementMgmt-SubjectAccess.ReadWrite` | Microsoft Graph | Lists, requests and cancels the user's own entitlement management access packages, for the Access packages window. | No |
```

Immediately after the table, add:

```markdown
`EntitlementMgmt-SubjectAccess.ReadWrite` is the one permission a user can consent to
themselves. Tenants that consented before it was added see one incremental consent prompt on
the next interactive sign-in; an administrator can also grant it for everyone with the consent
link in the tenant menu.
```

In `docs/entra-app/required-resource-access.json`, add to the Microsoft Graph (`00000003-…`) `resourceAccess` array:

```json
      { "id": "e9fdcbbb-8807-410f-b9ec-8d5468c7c2ac", "type": "Scope" }
```

- [ ] **Step 8: Include the scope in the admin consent URL**

In `macos/Sources/ElevateApp/App/AppModel.swift:308`, change the `scope` query item to:

```swift
            URLQueryItem(name: "scope", value: (GraphScopes.all + GroupScopes.all + EntitlementScopes.all).joined(separator: " ")),
```

- [ ] **Step 9: Run the whole Core suite and commit**

Run: `swift test`
Expected: all green.

```bash
git add macos/Sources/ElevateCore/Auth/TokenProviding.swift macos/Sources/ElevateCore/Auth/AccessTokenClaims.swift macos/Sources/ElevateCore/Models/Identity.swift macos/Sources/ElevateApp/App/AppModel.swift macos/Tests/ElevateCoreTests/AccessTokenClaimsTests.swift docs/entra-app-registration.md docs/entra-app/required-resource-access.json
git commit -m "Core: entitlement management scope and tenant flag"
```

---

### Task 2: Access package models and fixtures

**Files:**
- Create: `macos/Sources/ElevateCore/Models/AccessPackages.swift`
- Create: `macos/Tests/ElevateCoreTests/Fixtures/ap-packages.json`, `ap-packages-page2.json`, `ap-requests.json`, `ap-assignments.json`, `ap-requirements-one.json`, `ap-requirements-two.json`, `ap-requirements-questions.json`, `ap-request-created.json`
- Test: `macos/Tests/ElevateCoreTests/AccessPackageModelsTests.swift`

**Interfaces:**
- Produces the public models below. Later tasks use exactly these names and initializers.

- [ ] **Step 1: Write the fixtures**

`ap-packages.json`:

```json
{
  "@odata.nextLink": "https://graph.microsoft.com/v1.0/identityGovernance/entitlementManagement/accessPackages/filterByCurrentUser(on='allowedRequestor')?$skiptoken=page2",
  "value": [
    { "id": "pkg-finance", "displayName": "Finance Reporting Tools", "description": "Power BI workspace, Finance SharePoint site and the FinOps security group.", "isHidden": false },
    { "id": "pkg-exchange", "displayName": "Exchange Operations", "description": "Eligible Exchange Administrator and Teams Administrator PIM roles for on-call staff.", "isHidden": false }
  ]
}
```

`ap-packages-page2.json`:

```json
{
  "value": [
    { "id": "pkg-sandbox", "displayName": "Azure Sandbox Contributor", "description": "Contributor on the sandbox subscription for 30 days.", "isHidden": false },
    { "id": "pkg-hidden", "displayName": "Legacy VPN", "description": null, "isHidden": true }
  ]
}
```

`ap-requests.json`:

```json
{
  "value": [
    {
      "id": "req-1", "requestType": "userAdd", "state": "pendingApproval", "status": "PendingApproval",
      "justification": "Need a sandbox for the cost-alerting spike (INC-4412).",
      "createdDateTime": "2026-09-08T07:12:00Z", "completedDateTime": null,
      "accessPackage": { "id": "pkg-sandbox", "displayName": "Azure Sandbox Contributor" },
      "assignment": { "id": "asg-req-1", "assignmentPolicyId": "pol-eng" }
    },
    {
      "id": "req-2", "requestType": "userAdd", "state": "delivered", "status": "Delivered",
      "justification": "On-call rotation from September.",
      "createdDateTime": "2026-09-07T14:00:00Z", "completedDateTime": "2026-09-07T14:40:00Z",
      "accessPackage": { "id": "pkg-exchange", "displayName": "Exchange Operations" },
      "assignment": null
    },
    {
      "id": "req-3", "requestType": "userAdd", "state": "denied", "status": "Denied",
      "justification": "Debugging the rotation job.",
      "createdDateTime": "2026-09-02T09:00:00Z", "completedDateTime": "2026-09-02T09:03:00Z",
      "accessPackage": { "id": "pkg-kv", "displayName": "Production Key Vault Reader" }
    },
    {
      "id": "req-4", "requestType": "userAdd", "state": "somethingNew", "status": "Weird",
      "justification": null, "createdDateTime": "2026-09-01T09:00:00Z",
      "accessPackage": null
    }
  ]
}
```

`ap-assignments.json`:

```json
{
  "value": [
    {
      "id": "asg-1", "state": "delivered", "status": "Delivered", "expiredDateTime": null,
      "schedule": { "startDateTime": "2026-09-07T14:40:00Z", "expiration": { "type": "afterDateTime", "endDateTime": "2027-03-07T14:40:00Z" } },
      "accessPackage": { "id": "pkg-exchange", "displayName": "Exchange Operations" },
      "assignmentPolicy": { "id": "pol-oncall", "displayName": "On-call staff" }
    },
    {
      "id": "asg-2", "state": "delivered", "status": "Delivered", "expiredDateTime": null,
      "schedule": { "startDateTime": "2026-01-01T00:00:00Z", "expiration": { "type": "noExpiration" } },
      "accessPackage": { "id": "pkg-dev", "displayName": "Developer Baseline" },
      "assignmentPolicy": { "id": "pol-all", "displayName": "All employees" }
    },
    {
      "id": "asg-3", "state": "expired", "status": "Expired", "expiredDateTime": "2026-08-01T00:00:00Z",
      "schedule": { "startDateTime": "2026-07-01T00:00:00Z", "expiration": { "type": "afterDateTime", "endDateTime": "2026-08-01T00:00:00Z" } },
      "accessPackage": { "id": "pkg-old", "displayName": "Old Project" },
      "assignmentPolicy": null
    }
  ]
}
```

`ap-requirements-one.json`:

```json
{
  "value": [
    { "policyId": "pol-eng", "policyDisplayName": "Engineers", "policyDescription": "30 days, approval by the platform team.",
      "isApprovalRequired": true, "isApprovalRequiredForExtension": false, "isRequestorJustificationRequired": true,
      "questions": [], "existingAnswers": [], "schedule": null }
  ]
}
```

`ap-requirements-two.json`:

```json
{
  "value": [
    { "policyId": "pol-eng", "policyDisplayName": "Engineers", "policyDescription": "30 days.", "isApprovalRequired": true, "questions": [] },
    { "policyId": "pol-lead", "policyDisplayName": "Team leads", "policyDescription": "90 days, no approval.", "isApprovalRequired": false, "questions": [] }
  ]
}
```

`ap-requirements-questions.json`:

```json
{
  "value": [
    { "policyId": "pol-ext", "policyDisplayName": "External contractors", "policyDescription": null, "isApprovalRequired": true,
      "questions": [ { "@odata.type": "#microsoft.graph.accessPackageTextInputQuestion", "id": "q1", "isRequired": true, "text": { "defaultText": "Cost center" } } ] }
  ]
}
```

`ap-request-created.json`:

```json
{
  "id": "req-new", "requestType": "userAdd", "state": "submitted", "status": "Accepted",
  "justification": "Need a sandbox for the cost-alerting spike (INC-4412).",
  "createdDateTime": "2026-09-08T07:12:00Z", "completedDateTime": null,
  "assignment": { "id": "asg-new", "accessPackageId": "pkg-sandbox", "assignmentPolicyId": "pol-eng" }
}
```

- [ ] **Step 2: Write the failing model tests**

`AccessPackageModelsTests.swift`:

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct AccessPackageModelsTests {
    @Test func requestStateParsesCaseInsensitivelyAndFallsBackToUnknown() {
        #expect(AccessPackageRequestState.parse("PendingApproval") == .pendingApproval)
        #expect(AccessPackageRequestState.parse("delivered") == .delivered)
        #expect(AccessPackageRequestState.parse("partiallyDelivered") == .partiallyDelivered)
        #expect(AccessPackageRequestState.parse("somethingNew") == .unknown)
        #expect(AccessPackageRequestState.parse(nil) == .unknown)
    }

    @Test func requestStateGroupsIntoTabs() {
        #expect(AccessPackageRequestState.pendingApproval.isOpen)
        #expect(AccessPackageRequestState.scheduled.isOpen)
        #expect(!AccessPackageRequestState.delivered.isOpen)
        #expect(AccessPackageRequestState.denied.isDeclined)
        #expect(AccessPackageRequestState.canceled.isDeclined)
        #expect(AccessPackageRequestState.deliveryFailed.isDeclined)
        #expect(!AccessPackageRequestState.submitted.isDeclined)
        #expect(AccessPackageRequestState.submitted.isCancellable)
        #expect(AccessPackageRequestState.pendingApproval.isCancellable)
        #expect(!AccessPackageRequestState.delivering.isCancellable)
    }

    @Test func assignmentStateParses() {
        #expect(AccessPackageAssignmentState.parse("Delivered") == .delivered)
        #expect(AccessPackageAssignmentState.parse("expired") == .expired)
        #expect(AccessPackageAssignmentState.parse("nope") == .unknown)
    }

    @Test func modelsRoundTripThroughCodable() throws {
        let request = AccessPackageRequest(id: "r", packageId: "p", packageName: "Pkg", requestType: "userAdd",
                                           state: .denied, status: "Denied", justification: "why",
                                           createdAt: GraphJSON.parseDate("2026-09-02T09:00:00Z"),
                                           completedAt: GraphJSON.parseDate("2026-09-02T09:03:00Z"), policyId: "pol")
        let assignment = AccessPackageAssignment(id: "a", packageId: "p", packageName: "Pkg", state: .delivered,
                                                 policyName: "All", expiresAt: GraphJSON.parseDate("2027-01-01T00:00:00Z"))
        let snapshot = AccessPackageSnapshot(requests: [request], assignments: [assignment])
        let data = try GraphJSON.encoder.encode(snapshot)
        #expect(try GraphJSON.decoder.decode(AccessPackageSnapshot.self, from: data) == snapshot)
        let requirement = PolicyRequirement(id: "pol", displayName: "Engineers", description: nil, isApprovalRequired: true, requiresAnswers: false)
        #expect(try JSONDecoder().decode(PolicyRequirement.self, from: JSONEncoder().encode(requirement)) == requirement)
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `swift test --filter AccessPackageModelsTests`
Expected: compile errors for the missing types.

- [ ] **Step 4: Write the models**

`Models/AccessPackages.swift`:

```swift
import Foundation

/// An entitlement management access package the signed-in user may request.
public struct AccessPackage: Codable, Hashable, Sendable, Identifiable {
    public let id: String
    public var displayName: String
    public var description: String?
    public var isHidden: Bool

    public init(id: String, displayName: String, description: String? = nil, isHidden: Bool = false) {
        self.id = id
        self.displayName = displayName
        self.description = description
        self.isHidden = isHidden
    }
}

/// Graph's `accessPackageAssignmentRequestState`, plus `unknown` for values this build has not seen.
public enum AccessPackageRequestState: String, Codable, Hashable, Sendable, CaseIterable {
    case submitted, pendingApproval, delivering, delivered, deliveryFailed, denied, scheduled, canceled, partiallyDelivered, unknown

    /// Case-insensitive; anything unrecognised is `unknown` so one new value never fails a page.
    public static func parse(_ raw: String?) -> AccessPackageRequestState {
        guard let raw else { return .unknown }
        return allCases.first { $0.rawValue.caseInsensitiveCompare(raw) == .orderedSame } ?? .unknown
    }

    /// Requested tab: the request has not reached a final state.
    public var isOpen: Bool {
        switch self {
        case .submitted, .pendingApproval, .delivering, .scheduled, .partiallyDelivered: true
        default: false
        }
    }

    /// Declined tab: the request ended without access.
    public var isDeclined: Bool {
        switch self {
        case .denied, .deliveryFailed, .canceled: true
        default: false
        }
    }

    /// Graph accepts a cancel only before delivery starts.
    public var isCancellable: Bool { self == .submitted || self == .pendingApproval }
}

/// One of the signed-in user's own access package requests.
public struct AccessPackageRequest: Codable, Hashable, Sendable, Identifiable {
    public let id: String
    public var packageId: String
    public var packageName: String
    public var requestType: String
    public var state: AccessPackageRequestState
    /// Graph's free-text status, shown for failed and canceled requests.
    public var status: String?
    public var justification: String?
    public var createdAt: Date?
    public var completedAt: Date?
    public var policyId: String?

    public init(id: String, packageId: String, packageName: String, requestType: String, state: AccessPackageRequestState,
                status: String? = nil, justification: String? = nil, createdAt: Date? = nil, completedAt: Date? = nil, policyId: String? = nil) {
        self.id = id
        self.packageId = packageId
        self.packageName = packageName
        self.requestType = requestType
        self.state = state
        self.status = status
        self.justification = justification
        self.createdAt = createdAt
        self.completedAt = completedAt
        self.policyId = policyId
    }
}

public enum AccessPackageAssignmentState: String, Codable, Hashable, Sendable, CaseIterable {
    case delivering, delivered, expired, unknown

    public static func parse(_ raw: String?) -> AccessPackageAssignmentState {
        guard let raw else { return .unknown }
        return allCases.first { $0.rawValue.caseInsensitiveCompare(raw) == .orderedSame } ?? .unknown
    }
}

/// An access package currently (or formerly) assigned to the signed-in user.
public struct AccessPackageAssignment: Codable, Hashable, Sendable, Identifiable {
    public let id: String
    public var packageId: String
    public var packageName: String
    public var state: AccessPackageAssignmentState
    public var policyName: String?
    public var expiresAt: Date?

    public init(id: String, packageId: String, packageName: String, state: AccessPackageAssignmentState,
                policyName: String? = nil, expiresAt: Date? = nil) {
        self.id = id
        self.packageId = packageId
        self.packageName = packageName
        self.state = state
        self.policyName = policyName
        self.expiresAt = expiresAt
    }
}

/// One policy the signed-in user may request a package under, from `getApplicablePolicyRequirements`.
public struct PolicyRequirement: Codable, Hashable, Sendable, Identifiable {
    /// The policy id.
    public let id: String
    public var displayName: String
    public var description: String?
    public var isApprovalRequired: Bool
    /// True when the policy asks questions; Elevate hands such requests to the My Access portal.
    public var requiresAnswers: Bool

    public init(id: String, displayName: String, description: String? = nil, isApprovalRequired: Bool, requiresAnswers: Bool) {
        self.id = id
        self.displayName = displayName
        self.description = description
        self.isApprovalRequired = isApprovalRequired
        self.requiresAnswers = requiresAnswers
    }
}

/// What one poll of a tenant returned. Persisted so the next poll can diff against it.
public struct AccessPackageSnapshot: Codable, Hashable, Sendable {
    public var requests: [AccessPackageRequest]
    public var assignments: [AccessPackageAssignment]

    public init(requests: [AccessPackageRequest] = [], assignments: [AccessPackageAssignment] = []) {
        self.requests = requests
        self.assignments = assignments
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `swift test --filter AccessPackageModelsTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add macos/Sources/ElevateCore/Models/AccessPackages.swift macos/Tests/ElevateCoreTests/AccessPackageModelsTests.swift macos/Tests/ElevateCoreTests/Fixtures/ap-*.json
git commit -m "Core: access package models and fixtures"
```

---

### Task 3: Provider reads (packages, requests, assignments)

**Files:**
- Create: `macos/Sources/ElevateCore/Providers/AccessPackageProvider.swift`
- Test: `macos/Tests/ElevateCoreTests/AccessPackageProviderTests.swift`

**Interfaces:**
- Consumes: `GraphTransport` (`graphURL`, `listAll`, `get`, `post`), `EntitlementScopes.all`, the Task 2 models, `StubHTTPClient`, `FakeTokenProvider`, `Fixtures.data`.
- Produces: `AccessPackageProvider.init(http:tokens:)`, `requestablePackages(identity:tenantId:)`, `myRequests(identity:tenantId:)`, `myAssignments(identity:tenantId:)`.

- [ ] **Step 1: Write the failing tests**

`AccessPackageProviderTests.swift`:

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct AccessPackageProviderTests {
    let identity = Identity(id: "id1", upn: "alex.rivera@contoso.com", displayName: "Alex", homeTenantId: "t1")

    func makeProvider() -> (AccessPackageProvider, StubHTTPClient) {
        let http = StubHTTPClient()
        return (AccessPackageProvider(http: http, tokens: FakeTokenProvider()), http)
    }

    @Test func listsRequestablePackagesAcrossPages() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "accessPackages/filterByCurrentUser", body: Fixtures.data("ap-packages"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("ap-packages-page2"))
        let packages = try await p.requestablePackages(identity: identity, tenantId: "t1")
        #expect(packages.map(\.id) == ["pkg-finance", "pkg-exchange", "pkg-sandbox", "pkg-hidden"])
        #expect(packages[3].isHidden)
        #expect(packages[3].description == nil)
        let first = await http.requests.first!
        #expect(first.headers["Authorization"] == "Bearer token-t1")
        #expect(first.url.absoluteString.contains("identityGovernance/entitlementManagement/accessPackages/filterByCurrentUser(on='allowedRequestor')"))
    }

    @Test func listsMyRequestsWithPackageNamesAndStates() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "assignmentRequests/filterByCurrentUser", body: Fixtures.data("ap-requests"))
        let requests = try await p.myRequests(identity: identity, tenantId: "t1")
        #expect(requests.map(\.id) == ["req-1", "req-2", "req-3", "req-4"])
        #expect(requests[0].state == .pendingApproval)
        #expect(requests[0].packageName == "Azure Sandbox Contributor")
        #expect(requests[0].packageId == "pkg-sandbox")
        #expect(requests[0].policyId == "pol-eng")
        #expect(requests[0].justification == "Need a sandbox for the cost-alerting spike (INC-4412).")
        #expect(requests[1].state == .delivered)
        #expect(requests[1].completedAt == GraphJSON.parseDate("2026-09-07T14:40:00Z"))
        #expect(requests[2].state == .denied)
        // Unknown state and a missing package still decode; the name falls back to the id.
        #expect(requests[3].state == .unknown)
        #expect(requests[3].packageName == "req-4")
        #expect(requests[3].packageId == "")
        let url = await http.requests.first!.url.absoluteString
        #expect(url.contains("assignmentRequests/filterByCurrentUser(on='target')"))
        #expect(url.contains("expand=accessPackage,assignment"))
    }

    @Test func listsMyAssignmentsWithExpiryAndPolicy() async throws {
        let (p, http) = makeProvider()
        await http.on("GET", "assignments/filterByCurrentUser", body: Fixtures.data("ap-assignments"))
        let assignments = try await p.myAssignments(identity: identity, tenantId: "t1")
        #expect(assignments.map(\.id) == ["asg-1", "asg-2", "asg-3"])
        #expect(assignments[0].state == .delivered)
        #expect(assignments[0].expiresAt == GraphJSON.parseDate("2027-03-07T14:40:00Z"))
        #expect(assignments[0].policyName == "On-call staff")
        #expect(assignments[1].expiresAt == nil)
        #expect(assignments[2].state == .expired)
        #expect(assignments[2].policyName == nil)
        let url = await http.requests.first!.url.absoluteString
        #expect(url.contains("assignments/filterByCurrentUser(on='target')"))
        #expect(url.contains("expand=accessPackage,assignmentPolicy"))
    }

    @Test func forbiddenMapsToConsentRequiredForOwnApp() async {
        let (p, http) = makeProvider()
        await http.on("GET", "accessPackages/filterByCurrentUser", status: 403, body: Data(#"{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}"#.utf8))
        await #expect(throws: PIMError.consentRequired) {
            _ = try await p.requestablePackages(identity: identity, tenantId: "t1")
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `swift test --filter AccessPackageProviderTests`
Expected: compile error, `AccessPackageProvider` not found.

- [ ] **Step 3: Write the provider reads**

`Providers/AccessPackageProvider.swift`:

```swift
import Foundation

/// Self-service entitlement management for the signed-in user: the access packages they may
/// request, their own requests and assignments. Graph v1.0 only.
public struct AccessPackageProvider: Sendable {
    public let scopes = EntitlementScopes.all
    let transport: GraphTransport

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        transport = GraphTransport(http: http, tokens: tokens)
    }

    static let base = "/identityGovernance/entitlementManagement"

    // MARK: Wire shapes

    struct Named: Decodable { let id: String?; let displayName: String? }
    struct PackageDTO: Decodable { let id: String; let displayName: String?; let description: String?; let isHidden: Bool? }
    struct AssignmentRef: Decodable { let id: String?; let accessPackageId: String?; let assignmentPolicyId: String? }
    struct RequestDTO: Decodable {
        let id: String
        let requestType: String?
        let state: String?
        let status: String?
        let justification: String?
        let createdDateTime: Date?
        let completedDateTime: Date?
        let accessPackage: Named?
        let assignment: AssignmentRef?
    }
    struct Expiration: Decodable { let type: String?; let endDateTime: Date? }
    struct Schedule: Decodable { let startDateTime: Date?; let expiration: Expiration? }
    struct AssignmentDTO: Decodable {
        let id: String
        let state: String?
        let status: String?
        let schedule: Schedule?
        let accessPackage: Named?
        let assignmentPolicy: Named?
    }

    static func request(from r: RequestDTO) -> AccessPackageRequest {
        AccessPackageRequest(id: r.id,
                             packageId: r.accessPackage?.id ?? r.assignment?.accessPackageId ?? "",
                             packageName: r.accessPackage?.displayName ?? r.id,
                             requestType: r.requestType ?? "userAdd",
                             state: AccessPackageRequestState.parse(r.state),
                             status: r.status, justification: r.justification,
                             createdAt: r.createdDateTime, completedAt: r.completedDateTime,
                             policyId: r.assignment?.assignmentPolicyId)
    }

    static func assignment(from a: AssignmentDTO) -> AccessPackageAssignment {
        AccessPackageAssignment(id: a.id,
                                packageId: a.accessPackage?.id ?? "",
                                packageName: a.accessPackage?.displayName ?? a.id,
                                state: AccessPackageAssignmentState.parse(a.state),
                                policyName: a.assignmentPolicy?.displayName,
                                expiresAt: a.schedule?.expiration?.endDateTime)
    }

    // MARK: Reads

    public func requestablePackages(identity: Identity, tenantId: String) async throws -> [AccessPackage] {
        let url = try transport.graphURL("\(Self.base)/accessPackages/filterByCurrentUser(on='allowedRequestor')")
        let items = try await transport.listAll(PackageDTO.self, identity: identity, tenantId: tenantId, url: url, scopes: scopes)
        return items.map { AccessPackage(id: $0.id, displayName: $0.displayName ?? $0.id, description: $0.description, isHidden: $0.isHidden ?? false) }
    }

    public func myRequests(identity: Identity, tenantId: String) async throws -> [AccessPackageRequest] {
        let url = try transport.graphURL("\(Self.base)/assignmentRequests/filterByCurrentUser(on='target')?$expand=accessPackage,assignment")
        let items = try await transport.listAll(RequestDTO.self, identity: identity, tenantId: tenantId, url: url, scopes: scopes)
        return items.map(Self.request(from:))
    }

    public func myAssignments(identity: Identity, tenantId: String) async throws -> [AccessPackageAssignment] {
        let url = try transport.graphURL("\(Self.base)/assignments/filterByCurrentUser(on='target')?$expand=accessPackage,assignmentPolicy")
        let items = try await transport.listAll(AssignmentDTO.self, identity: identity, tenantId: tenantId, url: url, scopes: scopes)
        return items.map(Self.assignment(from:))
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `swift test --filter AccessPackageProviderTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add macos/Sources/ElevateCore/Providers/AccessPackageProvider.swift macos/Tests/ElevateCoreTests/AccessPackageProviderTests.swift
git commit -m "Core: access package provider reads"
```

---

### Task 4: Provider writes (requirements, request, cancel)

**Files:**
- Modify: `macos/Sources/ElevateCore/Providers/AccessPackageProvider.swift`
- Test: `macos/Tests/ElevateCoreTests/AccessPackageProviderTests.swift`

**Interfaces:**
- Produces: `requirements(packageId:identity:tenantId:) -> [PolicyRequirement]`, `request(packageId:policyId:justification:identity:tenantId:) -> AccessPackageRequest`, `cancel(requestId:identity:tenantId:)`, `AccessPackageProvider.myAccessURL(tenantId:packageId:) -> URL`.

- [ ] **Step 1: Write the failing tests**

Append to the suite in `AccessPackageProviderTests.swift`:

```swift
    @Test func requirementsWithOnePolicy() async throws {
        let (p, http) = makeProvider()
        await http.on("POST", "getApplicablePolicyRequirements", body: Fixtures.data("ap-requirements-one"))
        let reqs = try await p.requirements(packageId: "pkg-sandbox", identity: identity, tenantId: "t1")
        #expect(reqs.count == 1)
        #expect(reqs[0].id == "pol-eng")
        #expect(reqs[0].displayName == "Engineers")
        #expect(reqs[0].description == "30 days, approval by the platform team.")
        #expect(reqs[0].isApprovalRequired)
        #expect(!reqs[0].requiresAnswers)
        let sent = await http.requests.first!
        #expect(sent.method == "POST")
        #expect(sent.url.absoluteString.hasSuffix("/entitlementManagement/accessPackages/pkg-sandbox/getApplicablePolicyRequirements"))
    }

    @Test func requirementsWithTwoPoliciesAndQuestions() async throws {
        let (p, http) = makeProvider()
        await http.on("POST", "pkg-two/getApplicablePolicyRequirements", body: Fixtures.data("ap-requirements-two"))
        await http.on("POST", "pkg-q/getApplicablePolicyRequirements", body: Fixtures.data("ap-requirements-questions"))
        let two = try await p.requirements(packageId: "pkg-two", identity: identity, tenantId: "t1")
        #expect(two.map(\.id) == ["pol-eng", "pol-lead"])
        #expect(two[1].isApprovalRequired == false)
        let q = try await p.requirements(packageId: "pkg-q", identity: identity, tenantId: "t1")
        #expect(q.count == 1 && q[0].requiresAnswers)
    }

    @Test func requestPostsUserAddBodyWithOptionalPolicy() async throws {
        let (p, http) = makeProvider()
        await http.on("POST", "assignmentRequests", status: 201, body: Fixtures.data("ap-request-created"))
        let created = try await p.request(packageId: "pkg-sandbox", policyId: "pol-eng", justification: "Need it", identity: identity, tenantId: "t1")
        #expect(created.id == "req-new")
        #expect(created.state == .submitted)
        #expect(created.packageId == "pkg-sandbox")
        #expect(created.policyId == "pol-eng")
        let body = try JSONSerialization.jsonObject(with: await http.requests.first!.body!) as! [String: Any]
        #expect(body["requestType"] as? String == "userAdd")
        #expect(body["justification"] as? String == "Need it")
        let assignment = body["assignment"] as! [String: Any]
        #expect(assignment["accessPackageId"] as? String == "pkg-sandbox")
        #expect(assignment["assignmentPolicyId"] as? String == "pol-eng")

        _ = try await p.request(packageId: "pkg-sandbox", policyId: nil, justification: "Again", identity: identity, tenantId: "t1")
        let second = try JSONSerialization.jsonObject(with: await http.requests.last!.body!) as! [String: Any]
        let secondAssignment = second["assignment"] as! [String: Any]
        #expect(secondAssignment["assignmentPolicyId"] == nil)
    }

    @Test func cancelPostsToTheCancelAction() async throws {
        let (p, http) = makeProvider()
        await http.on("POST", "assignmentRequests/req-1/cancel", status: 204)
        try await p.cancel(requestId: "req-1", identity: identity, tenantId: "t1")
        let sent = await http.requests.first!
        #expect(sent.method == "POST")
        #expect(sent.url.absoluteString.hasSuffix("/assignmentRequests/req-1/cancel"))
    }

    @Test func myAccessURLPointsAtThePackageInTheTenant() {
        let url = AccessPackageProvider.myAccessURL(tenantId: "11111111-2222-3333-4444-555555555555", packageId: "pkg-sandbox")
        #expect(url.absoluteString == "https://myaccess.microsoft.com/@11111111-2222-3333-4444-555555555555#/access-packages/pkg-sandbox")
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `swift test --filter AccessPackageProviderTests`
Expected: compile errors for `requirements`, `request`, `cancel`, `myAccessURL`.

- [ ] **Step 3: Add the writes**

Append inside `AccessPackageProvider`:

```swift
    // MARK: Requirements and requests

    struct QuestionDTO: Decodable { let id: String?; let isRequired: Bool? }
    struct RequirementDTO: Decodable {
        let policyId: String?
        let policyDisplayName: String?
        let policyDescription: String?
        let isApprovalRequired: Bool?
        let questions: [QuestionDTO]?
    }

    /// One entry per policy the caller may request `packageId` under.
    public func requirements(packageId: String, identity: Identity, tenantId: String) async throws -> [PolicyRequirement] {
        let url = try transport.graphURL("\(Self.base)/accessPackages/\(packageId)/getApplicablePolicyRequirements")
        let r = try await transport.post(identity: identity, tenantId: tenantId, url: url, scopes: scopes, body: Data())
        let page = try GraphJSON.decoder.decode(GraphTransport.Page<RequirementDTO>.self, from: r.body)
        return page.value.compactMap { dto in
            guard let id = dto.policyId else { return nil }
            return PolicyRequirement(id: id, displayName: dto.policyDisplayName ?? id, description: dto.policyDescription,
                                     isApprovalRequired: dto.isApprovalRequired ?? false,
                                     requiresAnswers: !(dto.questions ?? []).isEmpty)
        }
    }

    /// Submits a `userAdd` request for the caller. `policyId` is required by Graph only when
    /// several policies apply; omitted otherwise.
    public func request(packageId: String, policyId: String?, justification: String, identity: Identity, tenantId: String) async throws -> AccessPackageRequest {
        var assignment: [String: Any] = ["accessPackageId": packageId]
        if let policyId { assignment["assignmentPolicyId"] = policyId }
        let body = try JSONSerialization.data(withJSONObject: ["requestType": "userAdd", "justification": justification, "assignment": assignment])
        let url = try transport.graphURL("\(Self.base)/assignmentRequests")
        let r = try await transport.post(identity: identity, tenantId: tenantId, url: url, scopes: scopes, body: body)
        let dto = try GraphJSON.decoder.decode(RequestDTO.self, from: r.body)
        var created = Self.request(from: dto)
        if created.packageId.isEmpty { created.packageId = packageId }
        return created
    }

    public func cancel(requestId: String, identity: Identity, tenantId: String) async throws {
        let url = try transport.graphURL("\(Self.base)/assignmentRequests/\(requestId)/cancel")
        _ = try await transport.post(identity: identity, tenantId: tenantId, url: url, scopes: scopes, body: Data())
    }

    /// The My Access portal page for one package, for policies whose questions Elevate does not collect.
    public static func myAccessURL(tenantId: String, packageId: String) -> URL {
        URL(string: "https://myaccess.microsoft.com/@\(tenantId)#/access-packages/\(packageId)")!
    }
```

`GraphTransport.Page` has an internal `nextLink`; it is in the same module, so decoding it here is fine.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `swift test --filter AccessPackageProviderTests`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add macos/Sources/ElevateCore/Providers/AccessPackageProvider.swift macos/Tests/ElevateCoreTests/AccessPackageProviderTests.swift
git commit -m "Core: access package requirements, request and cancel"
```

---

### Task 5: AccessPackageDiff

**Files:**
- Create: `macos/Sources/ElevateCore/Coordination/AccessPackageDiff.swift`
- Test: `macos/Tests/ElevateCoreTests/AccessPackageDiffTests.swift`

**Interfaces:**
- Consumes: `AccessPackageSnapshot`, `AccessPackageRequest`, `AccessPackageAssignment`.
- Produces: `AccessPackageEvent` and `AccessPackageDiff.events(previous:current:now:) -> [AccessPackageEvent]`.

- [ ] **Step 1: Write the failing tests**

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct AccessPackageDiffTests {
    let now = GraphJSON.parseDate("2026-09-08T12:00:00Z")!

    func request(_ id: String, _ state: AccessPackageRequestState) -> AccessPackageRequest {
        AccessPackageRequest(id: id, packageId: "p-\(id)", packageName: "Package \(id)", requestType: "userAdd", state: state)
    }
    func assignment(_ id: String, _ state: AccessPackageAssignmentState = .delivered, expires: String? = nil) -> AccessPackageAssignment {
        AccessPackageAssignment(id: id, packageId: "p-\(id)", packageName: "Package \(id)", state: state,
                                expiresAt: expires.flatMap(GraphJSON.parseDate))
    }

    @Test func nilPreviousIsBaselineAndYieldsNothing() {
        let current = AccessPackageSnapshot(requests: [request("r", .delivered)], assignments: [assignment("a")])
        #expect(AccessPackageDiff.events(previous: nil, current: current, now: now).isEmpty)
    }

    @Test func unchangedYieldsNothing() {
        let s = AccessPackageSnapshot(requests: [request("r", .pendingApproval)], assignments: [assignment("a")])
        #expect(AccessPackageDiff.events(previous: s, current: s, now: now).isEmpty)
    }

    @Test func requestTransitionsProduceEvents() {
        let before = AccessPackageSnapshot(requests: [request("a", .pendingApproval), request("b", .submitted), request("c", .delivering), request("d", .pendingApproval)])
        let after = AccessPackageSnapshot(requests: [request("a", .delivered), request("b", .denied), request("c", .deliveryFailed), request("d", .canceled)])
        let events = AccessPackageDiff.events(previous: before, current: after, now: now)
        #expect(events == [.approved(request("a", .delivered)), .denied(request("b", .denied)), .deliveryFailed(request("c", .deliveryFailed))])
    }

    @Test func aRequestFirstSeenAlreadyDeliveredDoesNotNotify() {
        let before = AccessPackageSnapshot(requests: [])
        let after = AccessPackageSnapshot(requests: [request("a", .delivered)])
        #expect(AccessPackageDiff.events(previous: before, current: after, now: now).isEmpty)
    }

    @Test func revokedBeforeExpiryAndExpiredAtExpiry() {
        let before = AccessPackageSnapshot(assignments: [assignment("keep", expires: "2027-01-01T00:00:00Z"),
                                                         assignment("gone-early", expires: "2027-01-01T00:00:00Z"),
                                                         assignment("gone-late", expires: "2026-09-08T11:00:00Z"),
                                                         assignment("marked", expires: "2026-09-01T00:00:00Z"),
                                                         assignment("open-ended")])
        let after = AccessPackageSnapshot(assignments: [assignment("keep", expires: "2027-01-01T00:00:00Z"),
                                                        assignment("marked", .expired, expires: "2026-09-01T00:00:00Z")])
        let events = AccessPackageDiff.events(previous: before, current: after, now: now)
        #expect(events.contains(.revoked(before.assignments[1])))
        #expect(events.contains(.expired(before.assignments[2])))
        #expect(events.contains(.expired(before.assignments[3])))
        #expect(events.contains(.revoked(before.assignments[4])))
        #expect(events.count == 4)
    }

    @Test func eventsCarryTheirPackageName() {
        let e = AccessPackageEvent.approved(request("x", .delivered))
        #expect(e.packageName == "Package x")
        #expect(AccessPackageEvent.expired(assignment("y")).packageName == "Package y")
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `swift test --filter AccessPackageDiffTests`
Expected: compile errors for `AccessPackageDiff` and `AccessPackageEvent`.

- [ ] **Step 3: Write the diff**

`Coordination/AccessPackageDiff.swift`:

```swift
import Foundation

/// Something worth telling the user about between two polls of a tenant's access packages.
public enum AccessPackageEvent: Hashable, Sendable {
    case approved(AccessPackageRequest)
    case denied(AccessPackageRequest)
    case deliveryFailed(AccessPackageRequest)
    case revoked(AccessPackageAssignment)
    case expired(AccessPackageAssignment)

    public var packageName: String {
        switch self {
        case .approved(let r), .denied(let r), .deliveryFailed(let r): r.packageName
        case .revoked(let a), .expired(let a): a.packageName
        }
    }
}

/// Pure comparison of two snapshots. A nil `previous` is the first sight of a tenant and never
/// notifies: everything present then is baseline, not news.
public enum AccessPackageDiff {
    public static func events(previous: AccessPackageSnapshot?, current: AccessPackageSnapshot, now: Date) -> [AccessPackageEvent] {
        guard let previous else { return [] }
        var out: [AccessPackageEvent] = []

        // Requests: only a request we already knew in an open state can transition into news.
        let previousStates = Dictionary(previous.requests.map { ($0.id, $0.state) }, uniquingKeysWith: { a, _ in a })
        for r in current.requests {
            guard let was = previousStates[r.id], was.isOpen, was != r.state else { continue }
            switch r.state {
            case .delivered: out.append(.approved(r))
            case .denied: out.append(.denied(r))
            case .deliveryFailed: out.append(.deliveryFailed(r))
            default: break
            }
        }

        // Assignments: a delivered one that is gone or expired is either revoked or expired,
        // decided by whether its end date has passed.
        let currentById = Dictionary(current.assignments.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        for a in previous.assignments where a.state == .delivered {
            let stillDelivered = currentById[a.id]?.state == .delivered
            guard !stillDelivered else { continue }
            if let end = a.expiresAt, end <= now {
                out.append(.expired(a))
            } else {
                out.append(.revoked(a))
            }
        }
        return out
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `swift test --filter AccessPackageDiffTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add macos/Sources/ElevateCore/Coordination/AccessPackageDiff.swift macos/Tests/ElevateCoreTests/AccessPackageDiffTests.swift
git commit -m "Core: access package diff to notification events"
```

---

### Task 6: NewRoleTracker

**Files:**
- Create: `macos/Sources/ElevateCore/Coordination/NewRoleTracker.swift`
- Test: `macos/Tests/ElevateCoreTests/NewRoleTrackerTests.swift`

**Interfaces:**
- Consumes: `RoleKey`.
- Produces: `NewRoleTracker` with `seen: Set<RoleKey>`, `new: Set<RoleKey>`, `shownOpens: Int`, `mutating func observe(discovered: Set<RoleKey>) -> [RoleKey]`, `mutating func panelOpened()`, `func isNew(_ key: RoleKey) -> Bool`.

- [ ] **Step 1: Write the failing tests**

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct NewRoleTrackerTests {
    func key(_ n: String) -> RoleKey { RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: n, directoryScopeId: "/")) }

    @Test func firstObserveBaselinesWithoutReportingAdditions() {
        var t = NewRoleTracker()
        let added = t.observe(discovered: [key("a"), key("b")])
        #expect(added.isEmpty)
        #expect(t.seen == [key("a"), key("b")])
        #expect(t.new.isEmpty)
    }

    @Test func additionsAreReportedOnceAndMarkedNew() {
        var t = NewRoleTracker()
        _ = t.observe(discovered: [key("a")])
        let added = t.observe(discovered: [key("a"), key("c"), key("b")])
        #expect(Set(added) == [key("b"), key("c")])
        #expect(t.isNew(key("b")) && t.isNew(key("c")) && !t.isNew(key("a")))
        #expect(t.observe(discovered: [key("a"), key("b"), key("c")]).isEmpty)
        #expect(t.new == [key("b"), key("c")])
    }

    @Test func removalsAreIgnoredAndAReturningRoleIsNewAgain() {
        var t = NewRoleTracker()
        _ = t.observe(discovered: [key("a"), key("b")])
        #expect(t.observe(discovered: [key("a")]).isEmpty)
        #expect(t.seen == [key("a")])
        #expect(t.observe(discovered: [key("a"), key("b")]) == [key("b")])
    }

    @Test func markerClearsOnTheSecondPanelOpen() {
        var t = NewRoleTracker()
        _ = t.observe(discovered: [key("a")])
        _ = t.observe(discovered: [key("a"), key("b")])
        t.panelOpened()
        #expect(t.isNew(key("b")))
        t.panelOpened()
        #expect(!t.isNew(key("b")))
        #expect(t.shownOpens == 0)
    }

    @Test func panelOpensWithNothingNewDoNotCount() {
        var t = NewRoleTracker()
        _ = t.observe(discovered: [key("a")])
        t.panelOpened(); t.panelOpened(); t.panelOpened()
        _ = t.observe(discovered: [key("a"), key("b")])
        t.panelOpened()
        #expect(t.isNew(key("b")))
    }

    @Test func emptyDiscoveryNeverBaselines() {
        var t = NewRoleTracker()
        #expect(t.observe(discovered: []).isEmpty)
        #expect(t.seen.isEmpty)
        #expect(t.observe(discovered: [key("a")]).isEmpty)   // still the first real sight
    }

    @Test func roundTripsThroughCodable() throws {
        var t = NewRoleTracker()
        _ = t.observe(discovered: [key("a")])
        _ = t.observe(discovered: [key("a"), key("b")])
        t.panelOpened()
        let data = try JSONEncoder().encode(t)
        #expect(try JSONDecoder().decode(NewRoleTracker.self, from: data) == t)
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `swift test --filter NewRoleTrackerTests`
Expected: compile error, `NewRoleTracker` not found.

- [ ] **Step 3: Write the tracker**

`Coordination/NewRoleTracker.swift`:

```swift
import Foundation

/// Remembers which eligible roles a tenant has shown before, so the panel can mark additions as
/// new and the app can notify about them. Pure state: the model calls `observe` after each
/// discovery and `panelOpened` each time the panel opens.
public struct NewRoleTracker: Codable, Hashable, Sendable {
    /// Every role key seen in the most recent discovery. Empty means "never baselined".
    public var seen: Set<RoleKey> = []
    /// Roles added since the baseline that the panel still marks.
    public var new: Set<RoleKey> = []
    /// Panel opens since `new` became non-empty; the marker clears at two.
    public var shownOpens: Int = 0

    public init() {}

    /// Records a discovery. Returns the additions, in no particular order. An empty discovery is
    /// ignored (a failed or consent-blocked read must not baseline away real roles), and the
    /// first non-empty discovery only baselines.
    @discardableResult
    public mutating func observe(discovered: Set<RoleKey>) -> [RoleKey] {
        guard !discovered.isEmpty else { return [] }
        defer { seen = discovered }
        guard !seen.isEmpty else { return [] }
        let added = discovered.subtracting(seen)
        new.formUnion(added)
        // A role that disappeared stops being "new"; it becomes new again if it returns.
        new.formIntersection(discovered)
        return Array(added)
    }

    /// Counts a panel open while something is marked; the second one clears the marker.
    public mutating func panelOpened() {
        guard !new.isEmpty else { return }
        shownOpens += 1
        if shownOpens >= 2 {
            new.removeAll()
            shownOpens = 0
        }
    }

    public func isNew(_ key: RoleKey) -> Bool { new.contains(key) }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `swift test --filter NewRoleTrackerTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add macos/Sources/ElevateCore/Coordination/NewRoleTracker.swift macos/Tests/ElevateCoreTests/NewRoleTrackerTests.swift
git commit -m "Core: new-role tracker"
```

---

### Task 7: Persisted state

**Files:**
- Modify: `macos/Sources/ElevateCore/Storage/AppState.swift`
- Test: `macos/Tests/ElevateCoreTests/AppStateStoreTests.swift`

**Interfaces:**
- Produces: `AccessPackageRecord { tenantKey, snapshot, polledAt }`, `RoleTrackingRecord { tenantKey, tracker }`, `AppState.accessPackages: [AccessPackageRecord]`, `AppState.roleTracking: [RoleTrackingRecord]`, `AppState.accessPackageRecord(_:) -> AccessPackageRecord?`, `AppState.setAccessPackages(_:snapshot:polledAt:)`, `AppState.roleTracker(_:) -> NewRoleTracker`, `AppState.setRoleTracker(_:_:)`; `removeTenant` clears both.

- [ ] **Step 1: Write the failing tests**

Append to the suite in `AppStateStoreTests.swift` (it already constructs `AppStateStore(directory:)` over a temp directory; copy the pattern used by the existing round-trip test for the directory setup):

```swift
    @Test func accessPackageRecordsRoundTripAndDefaultEmpty() async throws {
        let dir = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = AppStateStore(directory: dir)
        var s = AppState()
        let key = TenantKey(identityId: "i", tenantId: "t")
        let snapshot = AccessPackageSnapshot(requests: [AccessPackageRequest(id: "r", packageId: "p", packageName: "Pkg", requestType: "userAdd", state: .pendingApproval)])
        let polledAt = GraphJSON.parseDate("2026-09-08T07:00:00Z")!
        s.setAccessPackages(key, snapshot: snapshot, polledAt: polledAt)
        var tracker = NewRoleTracker()
        tracker.observe(discovered: [RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))])
        s.setRoleTracker(key, tracker)
        try await store.save(s)
        let loaded = try await store.load()
        #expect(loaded.accessPackageRecord(key)?.snapshot == snapshot)
        #expect(loaded.accessPackageRecord(key)?.polledAt == polledAt)
        #expect(loaded.roleTracker(key) == tracker)
        #expect(loaded.roleTracker(TenantKey(identityId: "i", tenantId: "other")) == NewRoleTracker())
    }

    @Test func legacyStateWithoutAccessPackagesLoads() throws {
        let legacy = Data(#"{"identities":[],"tenants":[],"manualRoles":[],"memory":[],"profiles":[]}"#.utf8)
        let s = try JSONDecoder().decode(AppState.self, from: legacy)
        #expect(s.accessPackages.isEmpty && s.roleTracking.isEmpty)
    }

    @Test func removingATenantDropsItsAccessPackageState() {
        var s = AppState()
        let key = TenantKey(identityId: "i", tenantId: "t")
        s.setAccessPackages(key, snapshot: AccessPackageSnapshot(), polledAt: .now)
        s.setRoleTracker(key, NewRoleTracker())
        s.removeTenant(key)
        #expect(s.accessPackageRecord(key) == nil)
        #expect(s.roleTracking.isEmpty)
    }
```

If `AppStateStore.load()` has a different name in the file, use the existing one; read `AppStateStoreTests.swift` first.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `swift test --filter AppStateStoreTests`
Expected: compile errors for the new members.

- [ ] **Step 3: Add the records and accessors**

In `AppState.swift`, before `public struct AppState`:

```swift
/// The last poll of one tenant's access packages, kept so the next poll can diff against it.
public struct AccessPackageRecord: Codable, Hashable, Sendable {
    public var tenantKey: TenantKey
    public var snapshot: AccessPackageSnapshot
    public var polledAt: Date

    public init(tenantKey: TenantKey, snapshot: AccessPackageSnapshot, polledAt: Date) {
        self.tenantKey = tenantKey
        self.snapshot = snapshot
        self.polledAt = polledAt
    }
}

/// Per-tenant new-role bookkeeping. An array of records, not a dictionary keyed by a struct, so
/// the Windows port can read the same file.
public struct RoleTrackingRecord: Codable, Hashable, Sendable {
    public var tenantKey: TenantKey
    public var tracker: NewRoleTracker

    public init(tenantKey: TenantKey, tracker: NewRoleTracker) {
        self.tenantKey = tenantKey
        self.tracker = tracker
    }
}
```

Inside `AppState`: add stored properties, coding keys, decoding, accessors, and removal.

```swift
    public var accessPackages: [AccessPackageRecord] = []
    public var roleTracking: [RoleTrackingRecord] = []

    private enum CodingKeys: String, CodingKey {
        case identities, tenants, manualRoles, memory, profiles, accessPackages, roleTracking
    }
```

In `init(from:)` append:

```swift
        accessPackages = try c.decodeIfPresent([AccessPackageRecord].self, forKey: .accessPackages) ?? []
        roleTracking = try c.decodeIfPresent([RoleTrackingRecord].self, forKey: .roleTracking) ?? []
```

Accessors, after `remember(roleKey:justification:duration:)`:

```swift
    public func accessPackageRecord(_ key: TenantKey) -> AccessPackageRecord? {
        accessPackages.first { $0.tenantKey == key }
    }

    public mutating func setAccessPackages(_ key: TenantKey, snapshot: AccessPackageSnapshot, polledAt: Date) {
        let record = AccessPackageRecord(tenantKey: key, snapshot: snapshot, polledAt: polledAt)
        if let i = accessPackages.firstIndex(where: { $0.tenantKey == key }) { accessPackages[i] = record } else { accessPackages.append(record) }
    }

    public func roleTracker(_ key: TenantKey) -> NewRoleTracker {
        roleTracking.first { $0.tenantKey == key }?.tracker ?? NewRoleTracker()
    }

    public mutating func setRoleTracker(_ key: TenantKey, _ tracker: NewRoleTracker) {
        let record = RoleTrackingRecord(tenantKey: key, tracker: tracker)
        if let i = roleTracking.firstIndex(where: { $0.tenantKey == key }) { roleTracking[i] = record } else { roleTracking.append(record) }
    }
```

In `removeTenant(_:)` add:

```swift
        accessPackages.removeAll { $0.tenantKey == key }
        roleTracking.removeAll { $0.tenantKey == key }
```

The `Hashable` conformance on `AppState` is synthesized and includes the new arrays automatically. Check that `AppState`'s custom `encode(to:)`, if one exists, encodes the two new keys; if the file relies on the synthesized encoder (it does today, only `init(from:)` is custom), nothing more is needed.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `swift test --filter AppStateStoreTests`
Expected: PASS.

- [ ] **Step 5: Run the whole suite and commit**

Run: `swift test`
Expected: all green.

```bash
git add macos/Sources/ElevateCore/Storage/AppState.swift macos/Tests/ElevateCoreTests/AppStateStoreTests.swift
git commit -m "Core: persist access package snapshots and role tracking"
```

---

### Task 8: Notifier support for package expiries

**Files:**
- Modify: `macos/Sources/ElevateApp/App/PanelRoute.swift`
- Modify: `macos/Sources/ElevateApp/Notifications/ExpiryNotifier.swift`

**Interfaces:**
- Produces: `PackageExpiry { id: String, packageName: String, tenantName: String, at: Date }`, `ExpiryNotifying.setPackageExpiries(_ expiries: [PackageExpiry]) async`, `PanelRoute.accessPackages(TenantKey)`.

No unit test drives `UNUserNotificationCenter`; this task is verified by compiling both the app and the hosted test bundle (the `NoopNotifier` conformance) and by the manual checks in Task 13.

- [ ] **Step 1: Extend the protocol and route**

In `PanelRoute.swift`:

```swift
enum PanelRoute: Codable, Hashable {
    case activate([RoleKey])
    case configureRoles(TenantKey)
    case addTenant(String)        // identity id
    case discoverTenants(String)  // identity id
    case addAccount
    case saveProfile([RoleKey])
    case runProfile(UUID)
    case manageProfiles
    case decide(requestId: String, approve: Bool)
    case accessPackages(TenantKey)
}

/// A scheduled "access package expired" notification: the assignment id keys it, so repeated
/// polls replace rather than duplicate.
struct PackageExpiry: Hashable, Sendable {
    let id: String
    let packageName: String
    let tenantName: String
    let at: Date
}

protocol ExpiryNotifying: Sendable {
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String], tenantNames: [TenantKey: String]) async
    /// Posts a notification immediately; used to report the outcome of a quick activation.
    func notify(title: String, body: String) async
    /// Replaces the set of access package expiries to notify about at their end dates.
    func setPackageExpiries(_ expiries: [PackageExpiry]) async
}

struct NoopNotifier: ExpiryNotifying {
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String], tenantNames: [TenantKey: String]) async {}
    func notify(title: String, body: String) async {}
    func setPackageExpiries(_ expiries: [PackageExpiry]) async {}
}
```

- [ ] **Step 2: Keep package expiries across reschedules**

`ExpiryNotifier.reschedule` removes every pending request before re-adding role expiries, so package expiries must be stored and re-added there. In `ExpiryNotifier.swift`:

Add a stored property after `onAuthorizationDenied`:

```swift
    /// Access package end dates, re-added after every `reschedule` (which clears the center).
    private let packageExpiries = PackageExpiryStore()
```

Add this actor at the bottom of the file:

```swift
/// Serialises access to the package expiry list from the notifier's nonisolated methods.
private actor PackageExpiryStore {
    private var items: [PackageExpiry] = []
    func replace(_ new: [PackageExpiry]) { items = new }
    func all() -> [PackageExpiry] { items }
}
```

Add the protocol method and a helper inside `ExpiryNotifier`:

```swift
    static let packageExpiredCategoryId = "PIMTRAY_PACKAGE_EXPIRED"

    func setPackageExpiries(_ expiries: [PackageExpiry]) async {
        await packageExpiries.replace(expiries)
        let center = UNUserNotificationCenter.current()
        for e in expiries { await addPackageExpiry(center, e) }
    }

    private func addPackageExpiry(_ center: UNUserNotificationCenter, _ e: PackageExpiry) async {
        let delay = e.at.addingTimeInterval(Self.expiredDelay).timeIntervalSinceNow
        guard delay > 1 else { return }
        let content = UNMutableNotificationContent()
        content.title = "\(e.packageName) expired"
        content.body = "Access package in \(e.tenantName)"
        content.categoryIdentifier = Self.packageExpiredCategoryId
        content.sound = .default
        let trigger = UNTimeIntervalNotificationTrigger(timeInterval: delay, repeats: false)
        try? await center.add(UNNotificationRequest(identifier: "package-expired-" + e.id, content: content, trigger: trigger))
    }
```

At the end of `reschedule(assignments:names:tenantNames:)`, after the `for a in assignments` loop, add:

```swift
        for e in await packageExpiries.all() { await addPackageExpiry(center, e) }
```

Register the category in `init` by adding `UNNotificationCategory(identifier: Self.packageExpiredCategoryId, actions: [], intentIdentifiers: [])` to the `setNotificationCategories` set.

`setPackageExpiries` adds requests with fixed identifiers, so calling it twice replaces the same ids rather than duplicating them; a package whose expiry was removed from the list is dropped at the next `reschedule`, which clears everything and re-adds only the current list.

- [ ] **Step 3: Build the app**

Run from `macos/`:

```bash
xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build
```

Expected: build succeeds. `RouteWindow` will fail to compile with "switch must be exhaustive" for the new route; add a temporary case there now:

```swift
        case .accessPackages(let tenantKey): Text("Access packages in \(tenantKey.tenantId)").padding()
```

Task 12 replaces it.

- [ ] **Step 4: Commit**

```bash
git add macos/Sources/ElevateApp/App/PanelRoute.swift macos/Sources/ElevateApp/Notifications/ExpiryNotifier.swift macos/Sources/ElevateApp/Views/RouteWindow.swift
git commit -m "App: access packages route and package expiry notifications"
```

---

### Task 9: AppModel polling, requests and notifications

**Files:**
- Modify: `macos/Sources/ElevateApp/App/AppModel.swift:20-30,225-260`
- Create: `macos/Sources/ElevateApp/App/AppModel+AccessPackages.swift`
- Modify: `macos/Sources/ElevateApp/App/AppModel+Activation.swift:26-33`
- Modify: `macos/Sources/ElevateApp/App/AppModel+Refresh.swift:9-15,93-99`
- Test: `macos/Tests/ElevateAppTests/AppModelAccessPackagesTests.swift`

**Interfaces:**
- Consumes: `AccessPackageProvider`, `AccessPackageDiff`, `AppState` accessors from Task 7, `PackageExpiry` and `ExpiryNotifying.setPackageExpiries` from Task 8, `InteractionRetry.run`, `AccessTokenClaims.permitsEntitlementSelfService`.
- Produces on `AppModel`: `accessPackageProvider: AccessPackageProvider`, `accessPackageErrors: [TenantKey: String]`, `accessPackagesPolling: Set<TenantKey>`, `static let accessPackagePanelThrottle: TimeInterval = 900`, `static let accessPackageBackgroundInterval: TimeInterval = 8 * 3600`, `func accessPackageSnapshot(_ key: TenantKey) -> AccessPackageSnapshot?`, `func accessPackagesPolledAt(_ key: TenantKey) -> Date?`, `func pollAccessPackages(_ key: TenantKey) async`, `func pollAccessPackagesIfDue(force: Bool = false) async`, `func requestablePackages(_ key: TenantKey) async throws -> [AccessPackage]`, `func packageRequirements(_ key: TenantKey, packageId: String) async throws -> [PolicyRequirement]`, `func requestPackage(_ key: TenantKey, packageId: String, policyId: String?, justification: String) async throws`, `func cancelPackageRequest(_ key: TenantKey, requestId: String) async throws`, `func probeAccessPackages(identity:tenantId:) async -> Bool?`.

- [ ] **Step 1: Write the failing hosted tests**

`AppModelAccessPackagesTests.swift`:

```swift
import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// Records what the model asked the notifier to do.
actor RecordingNotifier: ExpiryNotifying {
    private(set) var notifications: [(title: String, body: String)] = []
    private(set) var packageExpiries: [PackageExpiry] = []
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String], tenantNames: [TenantKey: String]) async {}
    func notify(title: String, body: String) async { notifications.append((title, body)) }
    func setPackageExpiries(_ expiries: [PackageExpiry]) async { packageExpiries = expiries }
}

@MainActor
struct AppModelAccessPackagesTests {
    private func modelWithTenant(http: StubHTTPClient, notifier: RecordingNotifier) async -> AppModel {
        var state = AppState()
        state.identities = [Sample.identity()]
        var tenant = Sample.tenant()
        tenant.accessPackagesAvailable = true
        state.tenants = [tenant]
        return await makeModel(state: state, http: http, online: true, notifier: notifier)
    }

    @Test func firstPollBaselinesWithoutNotifying() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        await http.on("GET", "assignmentRequests/filterByCurrentUser", body: Data(#"{"value":[{"id":"r1","state":"delivered","accessPackage":{"id":"p","displayName":"Pkg"}}]}"#.utf8))
        await http.on("GET", "assignments/filterByCurrentUser", body: Data(#"{"value":[]}"#.utf8))
        let model = await modelWithTenant(http: http, notifier: notifier)
        await model.pollAccessPackages(Sample.tenantKey)
        #expect(model.accessPackageSnapshot(Sample.tenantKey)?.requests.map(\.id) == ["r1"])
        #expect(await notifier.notifications.isEmpty)
        cleanup(model)
    }

    @Test func secondPollNotifiesApprovalAndSchedulesExpiry() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        var state = AppState()
        state.identities = [Sample.identity()]
        var tenant = Sample.tenant()
        tenant.accessPackagesAvailable = true
        state.tenants = [tenant]
        let pending = AccessPackageRequest(id: "r1", packageId: "p", packageName: "Pkg", requestType: "userAdd", state: .pendingApproval)
        state.setAccessPackages(Sample.tenantKey, snapshot: AccessPackageSnapshot(requests: [pending]), polledAt: .distantPast)
        await http.on("GET", "assignmentRequests/filterByCurrentUser", body: Data(#"{"value":[{"id":"r1","state":"delivered","accessPackage":{"id":"p","displayName":"Pkg"}}]}"#.utf8))
        await http.on("GET", "assignments/filterByCurrentUser", body: Data(#"{"value":[{"id":"a1","state":"delivered","schedule":{"expiration":{"endDateTime":"2030-01-01T00:00:00Z"}},"accessPackage":{"id":"p","displayName":"Pkg"}}]}"#.utf8))
        let model = await makeModel(state: state, http: http, online: true, notifier: notifier)
        await model.pollAccessPackages(Sample.tenantKey)
        let notes = await notifier.notifications
        #expect(notes.count == 1)
        #expect(notes[0].title == "Access package approved")
        #expect(notes[0].body == "Pkg in Contoso")
        let expiries = await notifier.packageExpiries
        #expect(expiries.map(\.id) == ["a1"])
        #expect(expiries[0].packageName == "Pkg" && expiries[0].tenantName == "Contoso")
        cleanup(model)
    }

    @Test func pollIfDueHonoursTheThrottleUnlessForced() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        await http.on("GET", "assignmentRequests/filterByCurrentUser", body: Data(#"{"value":[]}"#.utf8))
        await http.on("GET", "assignments/filterByCurrentUser", body: Data(#"{"value":[]}"#.utf8))
        let model = await modelWithTenant(http: http, notifier: notifier)
        await model.pollAccessPackagesIfDue()
        #expect(await http.requests(matching: "assignmentRequests").count == 1)
        await model.pollAccessPackagesIfDue()
        #expect(await http.requests(matching: "assignmentRequests").count == 1)
        await model.pollAccessPackagesIfDue(force: true)
        #expect(await http.requests(matching: "assignmentRequests").count == 2)
        cleanup(model)
    }

    @Test func tenantsWithoutTheScopeAreNeverPolled() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]   // accessPackagesAvailable nil
        let model = await makeModel(state: state, http: http, online: true, notifier: notifier)
        await model.pollAccessPackagesIfDue(force: true)
        #expect(await http.requests.isEmpty)
        cleanup(model)
    }

    @Test func consentFailureIsRecordedPerTenantAndKeepsTheOldSnapshot() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        await http.on("GET", "assignmentRequests/filterByCurrentUser", status: 403, body: Data(#"{"error":{"code":"Authorization_RequestDenied","message":"no"}}"#.utf8))
        let model = await modelWithTenant(http: http, notifier: notifier)
        await model.pollAccessPackages(Sample.tenantKey)
        #expect(model.accessPackageErrors[Sample.tenantKey] == PIMError.consentRequired.userMessage)
        #expect(model.accessPackageSnapshot(Sample.tenantKey) == nil)
        cleanup(model)
    }

    @Test func requestingAPackageRefreshesTheSnapshot() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        await http.on("POST", "assignmentRequests", status: 201, body: Data(#"{"id":"new","state":"submitted","assignment":{"accessPackageId":"p"}}"#.utf8))
        await http.on("GET", "assignmentRequests/filterByCurrentUser", body: Data(#"{"value":[{"id":"new","state":"submitted","accessPackage":{"id":"p","displayName":"Pkg"}}]}"#.utf8))
        await http.on("GET", "assignments/filterByCurrentUser", body: Data(#"{"value":[]}"#.utf8))
        let model = await modelWithTenant(http: http, notifier: notifier)
        try await model.requestPackage(Sample.tenantKey, packageId: "p", policyId: nil, justification: "Because")
        #expect(model.accessPackageSnapshot(Sample.tenantKey)?.requests.map(\.id) == ["new"])
        cleanup(model)
    }
}
```

`makeModel` in `Tests/ElevateAppTests/Support/TestModel.swift` takes no `notifier` parameter today. Add one: `notifier: any ExpiryNotifying = NoopNotifier()` and pass it to `AppModel(...)` in place of `NoopNotifier()`.

- [ ] **Step 2: Run the hosted tests to verify they fail**

Run from `macos/`: `xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates test -only-testing:ElevateAppTests/AppModelAccessPackagesTests`
Expected: compile errors for the missing model members.

- [ ] **Step 3: Add stored properties and provider wiring to AppModel**

In `AppModel.swift`, after the approvals block (`approvalErrors`), add:

```swift
    // MARK: Access packages — AppModel+AccessPackages

    /// Last poll failure per tenant, shown in the access packages window.
    var accessPackageErrors: [TenantKey: String] = [:]
    /// Tenants with a poll in flight, so two triggers cannot overlap.
    var accessPackagesPolling: Set<TenantKey> = []
    /// Self-service entitlement calls, rebuilt with the coordinator when the client id changes.
    private(set) var accessPackageProvider: AccessPackageProvider
    private var accessPackageTimer: Task<Void, Never>?
```

In `init`, after `discovery = TenantDiscovery(http: http, tokens: tokens)`:

```swift
        accessPackageProvider = AccessPackageProvider(http: http, tokens: tokens)
```

In `applyClientId`, after `discovery = TenantDiscovery(http: http, tokens: composite)`:

```swift
        accessPackageProvider = AccessPackageProvider(http: http, tokens: composite)
        accessPackageErrors = [:]
```

In `startTimer()`, after the existing `refreshTimer = Task { ... }` block:

```swift
        accessPackageTimer?.cancel()
        accessPackageTimer = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(Self.accessPackageBackgroundInterval))
                guard let self, self.isOnline else { continue }
                await self.pollAccessPackagesIfDue()
            }
        }
```

- [ ] **Step 4: Write the model extension**

`App/AppModel+AccessPackages.swift`:

```swift
import Foundation
import ElevateCore

@MainActor
extension AppModel {
    static let accessPackagePanelThrottle: TimeInterval = 15 * 60
    static let accessPackageBackgroundInterval: TimeInterval = 8 * 3600

    // MARK: Reads

    func accessPackageSnapshot(_ key: TenantKey) -> AccessPackageSnapshot? { state.accessPackageRecord(key)?.snapshot }
    func accessPackagesPolledAt(_ key: TenantKey) -> Date? { state.accessPackageRecord(key)?.polledAt }

    /// Tenants whose token carries the entitlement scope.
    private var accessPackageTenants: [TenantContext] { state.tenants.filter { $0.accessPackagesAvailable == true } }

    // MARK: Polling

    /// Polls every eligible tenant that has not been polled within the throttle window.
    func pollAccessPackagesIfDue(force: Bool = false) async {
        guard bootstrapped, isOnline else { return }
        let due = accessPackageTenants.filter { tenant in
            guard !force, let at = accessPackagesPolledAt(tenant.id) else { return true }
            return Date().timeIntervalSince(at) > Self.accessPackagePanelThrottle
        }
        await withTaskGroup(of: Void.self) { group in
            for tenant in due { group.addTask { await self.pollAccessPackages(tenant.id) } }
        }
    }

    /// Reads requests and assignments for one tenant, notifies about what changed, persists.
    func pollAccessPackages(_ key: TenantKey) async {
        guard let identity = identity(key.identityId), let tenant = tenant(key), !accessPackagesPolling.contains(key) else { return }
        guard !signInNeeded.contains(identity.id), !declinedTenants.contains(key) else { return }
        let generation = configGeneration
        accessPackagesPolling.insert(key)
        defer { accessPackagesPolling.remove(key) }
        let provider = accessPackageProvider
        do {
            let requests = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
                try await provider.myRequests(identity: identity, tenantId: key.tenantId)
            }
            let assignments = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
                try await provider.myAssignments(identity: identity, tenantId: key.tenantId)
            }
            guard generation == configGeneration, self.tenant(key) != nil else { return }
            let current = AccessPackageSnapshot(requests: requests, assignments: assignments)
            let previous = accessPackageSnapshot(key)
            let events = AccessPackageDiff.events(previous: previous, current: current, now: .now)
            state.setAccessPackages(key, snapshot: current, polledAt: .now)
            accessPackageErrors[key] = nil
            persist()
            for event in events { await notify(event, tenant: tenant) }
            await reschedulePackageExpiries()
        } catch is CancellationError {
            return
        } catch {
            guard generation == configGeneration else { return }
            let message = (error as? PIMError)?.userMessage ?? error.localizedDescription
            accessPackageErrors[key] = message
            logError("Access packages in \(tenant.displayName): \(message)")
        }
    }

    private func notify(_ event: AccessPackageEvent, tenant: TenantContext) async {
        let where_ = "\(event.packageName) in \(tenant.displayName)"
        switch event {
        case .approved: await notifier.notify(title: "Access package approved", body: where_)
        case .denied: await notifier.notify(title: "Access package denied", body: where_)
        case .deliveryFailed: await notifier.notify(title: "Access package delivery failed", body: where_)
        case .revoked: await notifier.notify(title: "Access package revoked", body: where_)
        case .expired: await notifier.notify(title: "Access package expired", body: where_)
        }
    }

    /// Every delivered assignment with an end date, across tenants, handed to the notifier so the
    /// expiry notification fires on time even between polls.
    // internal for AppModel (applyClientId) and tests
    func reschedulePackageExpiries() async {
        var expiries: [PackageExpiry] = []
        for record in state.accessPackages {
            let tenantName = tenant(record.tenantKey)?.displayName ?? record.tenantKey.tenantId
            for a in record.snapshot.assignments where a.state == .delivered {
                guard let end = a.expiresAt else { continue }
                expiries.append(PackageExpiry(id: a.id, packageName: a.packageName, tenantName: tenantName, at: end))
            }
        }
        await notifier.setPackageExpiries(expiries)
    }

    // MARK: Window data

    func requestablePackages(_ key: TenantKey) async throws -> [AccessPackage] {
        guard let identity = identity(key.identityId) else { throw PIMError.unexpected(status: 0, body: "That account is no longer signed in.") }
        let provider = accessPackageProvider
        return try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
            try await provider.requestablePackages(identity: identity, tenantId: key.tenantId)
        }
    }

    func packageRequirements(_ key: TenantKey, packageId: String) async throws -> [PolicyRequirement] {
        guard let identity = identity(key.identityId) else { throw PIMError.unexpected(status: 0, body: "That account is no longer signed in.") }
        let provider = accessPackageProvider
        return try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
            try await provider.requirements(packageId: packageId, identity: identity, tenantId: key.tenantId)
        }
    }

    /// Submits the request, then re-polls so the Requested tab shows it.
    func requestPackage(_ key: TenantKey, packageId: String, policyId: String?, justification: String) async throws {
        guard let identity = identity(key.identityId) else { throw PIMError.unexpected(status: 0, body: "That account is no longer signed in.") }
        let provider = accessPackageProvider
        _ = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
            try await provider.request(packageId: packageId, policyId: policyId, justification: justification, identity: identity, tenantId: key.tenantId)
        }
        await pollAccessPackages(key)
    }

    func cancelPackageRequest(_ key: TenantKey, requestId: String) async throws {
        guard let identity = identity(key.identityId) else { throw PIMError.unexpected(status: 0, body: "That account is no longer signed in.") }
        let provider = accessPackageProvider
        try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
            try await provider.cancel(requestId: requestId, identity: identity, tenantId: key.tenantId)
        }
        await pollAccessPackages(key)
    }
}
```

- [ ] **Step 5: Probe the scope during refresh**

In `AppModel+Activation.swift`, after `probeEntraActivation`:

```swift
    /// Whether this tenant's Graph token carries the entitlement scope. nil when no token is
    /// available or it is opaque, so the caller keeps the previous answer.
    func probeAccessPackages(identity: Identity, tenantId: String) async -> Bool? {
        guard identity.signInMethod.isPreauthorisedForEntraActivation else { return false }
        guard let token = try? await tokens.accessToken(identity: identity, tenantId: tenantId, scopes: EntitlementScopes.all) else { return nil }
        return AccessTokenClaims.permitsEntitlementSelfService(token)
    }
```

In `AppModel+Refresh.swift`, inside `refresh(_:kinds:)`, directly after the `if isEntra, let support = await probeEntraActivation(...)` block (line 93-99), add:

```swift
                    if isEntra, let available = await probeAccessPackages(identity: identity, tenantId: key.tenantId),
                       available != tenant.accessPackagesAvailable {
                        guard generation == configGeneration, self.tenant(key) != nil else { return }
                        tenant.accessPackagesAvailable = available
                        state.upsertTenant(tenant)
                        persist()
                    }
```

In `panelOpened()`, change the body to:

```swift
    func panelOpened() {
        searchQuery = ""
        for tenant in state.tenants {
            var tracker = state.roleTracker(tenant.id)
            let before = tracker
            tracker.panelOpened()
            if tracker != before { state.setRoleTracker(tenant.id, tracker) }
        }
        persist()
        guard bootstrapped, isOnline, !identities.isEmpty else { return }
        Task { await self.pollAccessPackagesIfDue() }
        guard Date().timeIntervalSince(lastRefresh) > 30 else { return }
        Task { await self.refreshAll() }
    }
```

The role-tracker part of that change is exercised in Task 10; it lands here so `panelOpened` is edited once.

- [ ] **Step 6: Run the hosted tests to verify they pass**

Run: `xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates test -only-testing:ElevateAppTests/AppModelAccessPackagesTests`
Expected: PASS (6 tests). If `makeModel`'s `online: true` triggers a refresh during `bootstrap()` that hits unstubbed URLs, the stub answers 599 and the refresh records a tenant error; that does not affect these assertions.

- [ ] **Step 7: Run the full hosted suite and commit**

Run: `xcodebuild ... test` (whole scheme).
Expected: all green.

```bash
git add macos/Sources/ElevateApp/App/AppModel.swift macos/Sources/ElevateApp/App/AppModel+AccessPackages.swift macos/Sources/ElevateApp/App/AppModel+Activation.swift macos/Sources/ElevateApp/App/AppModel+Refresh.swift macos/Tests/ElevateAppTests/AppModelAccessPackagesTests.swift macos/Tests/ElevateAppTests/Support/TestModel.swift
git commit -m "App: poll access packages, notify on changes, request and cancel"
```

---

### Task 10: New-role detection in refresh

**Files:**
- Modify: `macos/Sources/ElevateApp/App/AppModel+Refresh.swift` (the tail of `refresh`, after `roles[key] = ManualRoleSource.merge(...)`)
- Modify: `macos/Sources/ElevateApp/App/AppModel.swift` (one helper)
- Test: `macos/Tests/ElevateAppTests/AppModelAccessPackagesTests.swift`

**Interfaces:**
- Consumes: `NewRoleTracker`, `AppState.roleTracker/setRoleTracker`.
- Produces: `AppModel.isNewRole(_ key: RoleKey) -> Bool`, `AppModel.observeDiscoveredRoles(_ key: TenantKey, discovered: [EligibleRole]) async`.

- [ ] **Step 1: Write the failing tests**

Append to `AppModelAccessPackagesTests`:

```swift
    @Test func newRolesAreMarkedNotifiedAndClearedOnSecondOpen() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]
        let model = await makeModel(state: state, http: http, notifier: notifier)
        let reader = Sample.role(Sample.entraKey, name: "Global Reader")
        let exchange = Sample.role(Sample.key(.entraDirectory(roleDefinitionId: "exchange", directoryScopeId: "/")), name: "Exchange Administrator")

        await model.observeDiscoveredRoles(Sample.tenantKey, discovered: [reader])
        #expect(await notifier.notifications.isEmpty)
        #expect(!model.isNewRole(reader.key))

        await model.observeDiscoveredRoles(Sample.tenantKey, discovered: [reader, exchange])
        let notes = await notifier.notifications
        #expect(notes.count == 1)
        #expect(notes[0].title == "New roles available in Contoso")
        #expect(notes[0].body == "Exchange Administrator")
        #expect(model.isNewRole(exchange.key))

        model.panelOpened()
        #expect(model.isNewRole(exchange.key))
        model.panelOpened()
        #expect(!model.isNewRole(exchange.key))
        cleanup(model)
    }

    @Test func emptyDiscoveryDoesNotBaselineOrNotify() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]
        let model = await makeModel(state: state, http: http, notifier: notifier)
        await model.observeDiscoveredRoles(Sample.tenantKey, discovered: [])
        await model.observeDiscoveredRoles(Sample.tenantKey, discovered: [Sample.role(Sample.entraKey, name: "Global Reader")])
        #expect(await notifier.notifications.isEmpty)
        cleanup(model)
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run the hosted suite filtered to `AppModelAccessPackagesTests`.
Expected: compile errors for `observeDiscoveredRoles` and `isNewRole`.

- [ ] **Step 3: Implement**

In `AppModel.swift`, next to `assignment(for:)`:

```swift
    func isNewRole(_ key: RoleKey) -> Bool { state.roleTracker(key.tenantKey).isNew(key) }
```

In `AppModel+Refresh.swift`, add to the extension (near `rescheduleNotifications`):

```swift
    /// Feeds one tenant's discovered roles to its tracker; additions are announced once, named
    /// alphabetically, and marked in the panel until the second open.
    // internal for tests
    func observeDiscoveredRoles(_ key: TenantKey, discovered: [EligibleRole]) async {
        var tracker = state.roleTracker(key)
        let added = tracker.observe(discovered: Set(discovered.map(\.key)))
        state.setRoleTracker(key, tracker)
        persist()
        guard !added.isEmpty else { return }
        let names = discovered.filter { added.contains($0.key) }.map(\.displayName).sorted()
        let tenantName = tenant(key)?.displayName ?? key.tenantId
        await notifier.notify(title: "New roles available in \(tenantName)", body: names.joined(separator: ", "))
    }
```

In `refresh(_:kinds:)`, right after `roles[key] = ManualRoleSource.merge(discovered: discovered, manual: manual)`, add:

```swift
        // Only a full, error-free discovery may move the new-role baseline: a partial or failed
        // read would otherwise report the missing kinds as new when they come back.
        if requestedKinds == nil, errors.isEmpty, !consentBlocked {
            await observeDiscoveredRoles(key, discovered: discovered)
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the hosted suite filtered to `AppModelAccessPackagesTests`.
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add macos/Sources/ElevateApp/App/AppModel.swift macos/Sources/ElevateApp/App/AppModel+Refresh.swift macos/Tests/ElevateAppTests/AppModelAccessPackagesTests.swift
git commit -m "App: mark and announce newly eligible roles"
```

---

### Task 11: Panel entry point and new-role accent

**Files:**
- Modify: `macos/Sources/ElevateApp/Views/TenantSection.swift:19-48,156-172`
- Modify: `macos/Sources/ElevateApp/Views/RoleRow.swift`

**Interfaces:**
- Consumes: `TenantContext.accessPackagesAvailable`, `PanelRoute.accessPackages`, `AppModel.isNewRole`.

- [ ] **Step 1: Add the glyph and menu item**

In `TenantHeader.body`, between `TenantPills(tenant: tenant)` and `Spacer()`, nothing changes; after the `activeCount` text and before `HeaderMenu`, add:

```swift
                if tenant.accessPackagesAvailable == true {
                    Button { open(.accessPackages(tenant.id)) } label: {
                        Image(systemName: "shippingbox").font(.caption).foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                    .help("Access packages")
                    .accessibilityLabel("Access packages")
                }
```

`open(_:)` already exists on `TenantHeader`.

In `TenantMenuItems.body`, before `Button("Configure known PIM roles…")`:

```swift
        if tenant.accessPackagesAvailable == true {
            Button("Access packages…") {
                openWindow(value: PanelRoute.accessPackages(tenant.id))
                NSApp.activate(ignoringOtherApps: true)
            }
        }
```

- [ ] **Step 2: Add the badge and tint to RoleRow**

In `RoleRow.body`, inside the `VStack` after `Text(role.displayName)...`, wrap the name in an `HStack`:

```swift
                HStack(spacing: 6) {
                    Text(role.displayName).font(.body).lineLimit(1)
                    if model.isNewRole(role.key) {
                        Text("new").font(.caption2.weight(.medium))
                            .padding(.horizontal, 5).padding(.vertical, 1)
                            .background(Color.accentColor.opacity(0.18), in: Capsule())
                            .accessibilityLabel("New role")
                    }
                }
```

After `.padding(.trailing, PanelMetrics.trailingInset)` add:

```swift
        .background(model.isNewRole(role.key) ? Color.accentColor.opacity(0.08) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
```

- [ ] **Step 3: Build, relaunch, check by eye**

```bash
xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build && open build/Build/Products/Debug/Elevate.app
```

Expected: the app builds. The glyph and menu item appear only once a refresh has probed a tenant with the scope, so on a tenant without consent nothing changes. That is correct.

- [ ] **Step 4: Commit**

```bash
git add macos/Sources/ElevateApp/Views/TenantSection.swift macos/Sources/ElevateApp/Views/RoleRow.swift
git commit -m "App: access packages entry point and new-role accent"
```

---

### Task 12: Access packages window and request sheet

**Files:**
- Create: `macos/Sources/ElevateApp/Views/AccessPackagesView.swift`
- Create: `macos/Sources/ElevateApp/Views/RequestPackageSheet.swift`
- Modify: `macos/Sources/ElevateApp/Views/RouteWindow.swift`
- Test: `macos/Tests/ElevateAppTests/AppModelAccessPackagesTests.swift` (one filter test on the pure helper)

**Interfaces:**
- Consumes: the `AppModel` API from Task 9, `AccessPackageProvider.myAccessURL`, `StatusPill`, `PanelFilter.matches(query:text:)`.
- Produces: `AccessPackagesView(tenantKey:)`, `RequestPackageSheet(tenantKey:package:onSubmitted:)`, `AccessPackagesView.Tab`, `AccessPackagesView.filtered(_:query:)`.

- [ ] **Step 1: Write the failing filter test**

Append to `AppModelAccessPackagesTests`:

```swift
    @Test func windowSearchFiltersByNameAndDescription() {
        let rows = [
            AccessPackagesView.Row(id: "1", name: "Finance Reporting Tools", detail: "Power BI workspace"),
            AccessPackagesView.Row(id: "2", name: "Exchange Operations", detail: "PIM roles for on-call staff"),
        ]
        #expect(AccessPackagesView.filtered(rows, query: "").map(\.id) == ["1", "2"])
        #expect(AccessPackagesView.filtered(rows, query: "power").map(\.id) == ["1"])
        #expect(AccessPackagesView.filtered(rows, query: "ON-CALL").map(\.id) == ["2"])
        #expect(AccessPackagesView.filtered(rows, query: "nothing").isEmpty)
    }
```

- [ ] **Step 2: Run it to verify it fails**

Expected: compile error, `AccessPackagesView` not found.

- [ ] **Step 3: Write the window**

`Views/AccessPackagesView.swift`:

```swift
import SwiftUI
import ElevateCore

/// Per-tenant access packages: what can be requested, what is pending, held or declined.
struct AccessPackagesView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let tenantKey: TenantKey

    enum Tab: String, CaseIterable, Identifiable {
        case available = "Available", requested = "Requested", assigned = "Assigned", declined = "Declined"
        var id: String { rawValue }
    }

    /// One searchable line: a package, a request or an assignment.
    struct Row: Identifiable, Hashable {
        let id: String
        let name: String
        let detail: String?
    }

    @State private var tab: Tab = .available
    @State private var search = ""
    @State private var packages: [AccessPackage] = []
    @State private var packagesError: String?
    @State private var loadingPackages = false
    @State private var requesting: AccessPackage?
    @State private var cancelling: Set<String> = []
    @State private var actionError: String?

    private var tenantName: String { model.tenant(tenantKey)?.displayName ?? tenantKey.tenantId }
    private var snapshot: AccessPackageSnapshot { model.accessPackageSnapshot(tenantKey) ?? AccessPackageSnapshot() }
    private var consentError: String? {
        let e = model.accessPackageErrors[tenantKey] ?? packagesError
        return e == PIMError.consentRequired.userMessage ? e : nil
    }

    static func filtered(_ rows: [Row], query: String) -> [Row] {
        guard !query.trimmingCharacters(in: .whitespaces).isEmpty else { return rows }
        return rows.filter { PanelFilter.matches(query: query, text: $0.name) || ($0.detail.map { PanelFilter.matches(query: query, text: $0) } ?? false) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Packages you can request through Entra entitlement management. Approvals and delivery happen in Entra.")
                .font(.caption).foregroundStyle(.secondary)
            Picker("", selection: $tab) { ForEach(Tab.allCases) { Text($0.rawValue).tag($0) } }
                .pickerStyle(.segmented).labelsHidden()
            TextField("Search packages", text: $search)
            if let consentError {
                VStack(alignment: .leading, spacing: 6) {
                    Label("Access packages are not permitted in this tenant.", systemImage: "info.circle")
                    Text(consentError).font(.caption).foregroundStyle(.secondary)
                    if let url = model.adminConsentURL(identityId: tenantKey.identityId, tenantId: tenantKey.tenantId) {
                        Button("Open admin consent link…") { NSWorkspace.shared.open(url) }
                    }
                }
                Spacer()
            } else {
                content
            }
            if let actionError {
                Label(actionError, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack(spacing: 8) {
                Button { Task { await reload() } } label: { Image(systemName: "arrow.clockwise") }
                    .accessibilityLabel("Refresh")
                Text(updatedCaption).font(.caption).foregroundStyle(.secondary)
                if loadingPackages || !model.accessPackagesPolling.isDisjoint(with: [tenantKey]) { ProgressView().controlSize(.small) }
                Spacer()
                Button("Close") { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(16)
        .frame(width: 560, height: 520)
        .navigationTitle("Access packages in \(tenantName)")
        .task(id: tenantKey) { await reload() }
        .onChange(of: tab) { _, _ in Task { await model.pollAccessPackages(tenantKey) } }
        .sheet(item: $requesting) { package in
            RequestPackageSheet(tenantKey: tenantKey, package: package) {
                tab = .requested
            }
            .environment(model)
        }
    }

    @ViewBuilder private var content: some View {
        switch tab {
        case .available: availableList
        case .requested: requestedList
        case .assigned: assignedList
        case .declined: declinedList
        }
    }

    // MARK: Available

    private var availableRows: [(Row, AccessPackage)] {
        let rows = packages.map { Row(id: $0.id, name: $0.displayName, detail: $0.description) }
        let byId = Dictionary(packages.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        return Self.filtered(rows, query: search).compactMap { row in byId[row.id].map { (row, $0) } }
    }

    private var availableList: some View {
        List {
            if let packagesError, consentError == nil {
                Text(packagesError).font(.caption).foregroundStyle(.red)
            } else if packages.isEmpty, !loadingPackages {
                Text("No access packages are available for you to request.").font(.caption).foregroundStyle(.secondary)
            }
            ForEach(availableRows, id: \.0.id) { row, package in
                HStack(alignment: .top, spacing: 10) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(package.displayName)
                        if let d = package.description, !d.isEmpty { Text(d).font(.caption).foregroundStyle(.secondary).lineLimit(2) }
                    }
                    Spacer()
                    if let state = existingState(for: package.id) {
                        stateLabel(state.text, color: state.color)
                    } else {
                        Button("Request") { requesting = package }.buttonStyle(.borderedProminent).controlSize(.small)
                    }
                }
                .padding(.vertical, 2)
            }
        }
    }

    /// A pending request or delivered assignment for this package, shown instead of Request.
    private func existingState(for packageId: String) -> (text: String, color: Color)? {
        if let r = snapshot.requests.first(where: { $0.packageId == packageId && $0.state.isOpen }) {
            return (Self.label(r.state), Self.color(r.state))
        }
        if snapshot.assignments.contains(where: { $0.packageId == packageId && $0.state == .delivered }) {
            return ("assigned", .green)
        }
        return nil
    }

    // MARK: Requested

    private var requestedList: some View {
        let open = snapshot.requests.filter { $0.state.isOpen }.sorted { ($0.createdAt ?? .distantPast) > ($1.createdAt ?? .distantPast) }
        let rows = Self.filtered(open.map { Row(id: $0.id, name: $0.packageName, detail: $0.justification) }, query: search)
        let byId = Dictionary(open.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        return List {
            if open.isEmpty { Text("No requests in progress.").font(.caption).foregroundStyle(.secondary) }
            ForEach(rows) { row in
                if let r = byId[row.id] {
                    HStack(alignment: .top, spacing: 10) {
                        VStack(alignment: .leading, spacing: 2) {
                            HStack(spacing: 6) { Text(r.packageName); stateLabel(Self.label(r.state), color: Self.color(r.state)) }
                            Text(Self.requestCaption(r)).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                        }
                        Spacer()
                        if r.state.isCancellable {
                            Button(cancelling.contains(r.id) ? "Cancelling…" : "Cancel request") { Task { await cancel(r) } }
                                .controlSize(.small).disabled(cancelling.contains(r.id))
                        }
                    }
                    .padding(.vertical, 2)
                }
            }
        }
    }

    // MARK: Assigned

    private var assignedList: some View {
        let held = snapshot.assignments.filter { $0.state == .delivered }
        let rows = Self.filtered(held.map { Row(id: $0.id, name: $0.packageName, detail: $0.policyName) }, query: search)
        let byId = Dictionary(held.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        return List {
            if held.isEmpty { Text("No access packages are assigned to you.").font(.caption).foregroundStyle(.secondary) }
            ForEach(rows) { row in
                if let a = byId[row.id] {
                    let soon = Self.expiresSoon(a)
                    HStack(alignment: .top, spacing: 10) {
                        VStack(alignment: .leading, spacing: 2) {
                            HStack(spacing: 6) {
                                Text(a.packageName)
                                stateLabel(soon ? "expires soon" : "delivered", color: soon ? .orange : .green)
                            }
                            Text(Self.assignmentCaption(a)).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if soon, let package = packages.first(where: { $0.id == a.packageId }) {
                            Button("Request again") { requesting = package }.controlSize(.small)
                        }
                    }
                    .padding(.vertical, 2)
                }
            }
        }
    }

    // MARK: Declined

    private var declinedList: some View {
        let ended = snapshot.requests.filter { $0.state.isDeclined }.sorted { ($0.completedAt ?? $0.createdAt ?? .distantPast) > ($1.completedAt ?? $1.createdAt ?? .distantPast) }
        let rows = Self.filtered(ended.map { Row(id: $0.id, name: $0.packageName, detail: $0.justification) }, query: search)
        let byId = Dictionary(ended.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        return List {
            if ended.isEmpty { Text("No denied, failed or canceled requests.").font(.caption).foregroundStyle(.secondary) }
            ForEach(rows) { row in
                if let r = byId[row.id] {
                    HStack(alignment: .top, spacing: 10) {
                        VStack(alignment: .leading, spacing: 2) {
                            HStack(spacing: 6) { Text(r.packageName); stateLabel(Self.label(r.state), color: Self.color(r.state)) }
                            Text(Self.declinedCaption(r)).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                        }
                        Spacer()
                        if r.state != .canceled, let package = packages.first(where: { $0.id == r.packageId }) {
                            Button("Request again") { requesting = package }.controlSize(.small)
                        }
                    }
                    .padding(.vertical, 2)
                }
            }
        }
    }

    // MARK: Helpers

    private func stateLabel(_ text: String, color: Color) -> some View {
        HStack(spacing: 4) {
            Circle().fill(color).frame(width: 7, height: 7)
            Text(text).font(.caption).foregroundStyle(.secondary)
        }
        .accessibilityLabel(text)
    }

    static func label(_ s: AccessPackageRequestState) -> String {
        switch s {
        case .submitted: "submitted"
        case .pendingApproval: "pending approval"
        case .delivering: "delivering"
        case .delivered: "delivered"
        case .deliveryFailed: "delivery failed"
        case .denied: "denied"
        case .scheduled: "scheduled"
        case .canceled: "canceled"
        case .partiallyDelivered: "partially delivered"
        case .unknown: "unknown"
        }
    }

    static func color(_ s: AccessPackageRequestState) -> Color {
        switch s {
        case .submitted, .pendingApproval, .scheduled: .yellow
        case .delivering, .delivered, .partiallyDelivered: .green
        case .denied, .deliveryFailed: .red
        case .canceled, .unknown: .gray
        }
    }

    static func expiresSoon(_ a: AccessPackageAssignment, now: Date = .now) -> Bool {
        guard let end = a.expiresAt else { return false }
        return end.timeIntervalSince(now) < 7 * 24 * 3600
    }

    static func requestCaption(_ r: AccessPackageRequest) -> String {
        var parts: [String] = []
        if let d = r.createdAt { parts.append("Requested " + d.formatted(date: .abbreviated, time: .shortened)) }
        if let j = r.justification, !j.isEmpty { parts.append("\u{201C}\(j)\u{201D}") }
        return parts.joined(separator: " · ")
    }

    static func assignmentCaption(_ a: AccessPackageAssignment) -> String {
        var parts = [a.expiresAt.map { "Expires " + $0.formatted(date: .abbreviated, time: .omitted) } ?? "No expiry"]
        if let p = a.policyName { parts.append("Policy: \(p)") }
        return parts.joined(separator: " · ")
    }

    static func declinedCaption(_ r: AccessPackageRequest) -> String {
        var parts: [String] = []
        if let d = r.completedAt ?? r.createdAt {
            let verb = r.state == .canceled ? "Canceled" : r.state == .deliveryFailed ? "Failed" : "Decided"
            parts.append("\(verb) " + d.formatted(date: .abbreviated, time: .shortened))
        }
        if r.state == .denied, let j = r.justification, !j.isEmpty { parts.append("Your reason: \u{201C}\(j)\u{201D}") }
        if r.state != .denied, let s = r.status, !s.isEmpty { parts.append(s) }
        return parts.joined(separator: " · ")
    }

    private var updatedCaption: String {
        guard let at = model.accessPackagesPolledAt(tenantKey) else { return "Not updated yet" }
        return "Updated " + at.formatted(.relative(presentation: .named))
    }

    private func reload() async {
        actionError = nil
        loadingPackages = true
        defer { loadingPackages = false }
        async let poll: Void = model.pollAccessPackages(tenantKey)
        do {
            packages = try await model.requestablePackages(tenantKey)
            packagesError = nil
        } catch {
            packagesError = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
        await poll
    }

    private func cancel(_ r: AccessPackageRequest) async {
        cancelling.insert(r.id)
        defer { cancelling.remove(r.id) }
        do {
            try await model.cancelPackageRequest(tenantKey, requestId: r.id)
            actionError = nil
        } catch {
            actionError = "Could not cancel \(r.packageName): \((error as? PIMError)?.userMessage ?? error.localizedDescription)"
        }
    }
}
```

- [ ] **Step 4: Write the request sheet**

`Views/RequestPackageSheet.swift`:

```swift
import SwiftUI
import ElevateCore

/// Policy choice plus justification for one package; hands off to My Access when the chosen
/// policy asks questions Elevate does not collect.
struct RequestPackageSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let tenantKey: TenantKey
    let package: AccessPackage
    let onSubmitted: () -> Void

    @State private var requirements: [PolicyRequirement] = []
    @State private var selectedPolicyId: String?
    @State private var justification = ""
    @State private var loading = true
    @State private var loadError: String?
    @State private var submitting = false
    @State private var submitError: String?

    private var selected: PolicyRequirement? { requirements.first { $0.id == selectedPolicyId } ?? requirements.first }
    private var canSubmit: Bool {
        !submitting && selected != nil && !(selected?.requiresAnswers ?? false)
            && !justification.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                Text("Request \(package.displayName)").font(.headline)
                if let d = package.description, !d.isEmpty { Text(d).font(.caption).foregroundStyle(.secondary) }
            }
            if loading {
                ProgressView("Checking the request policy…").controlSize(.small)
            } else if let loadError {
                Label(loadError, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.red)
            } else if requirements.isEmpty {
                Text("No policy lets you request this package right now.").font(.caption).foregroundStyle(.secondary)
            } else if let selected, selected.requiresAnswers {
                questionsNotice(selected)
            } else {
                form
            }
            HStack {
                if submitting { ProgressView().controlSize(.small) }
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                if let selected, selected.requiresAnswers {
                    Button("Open in My Access") {
                        NSWorkspace.shared.open(AccessPackageProvider.myAccessURL(tenantId: tenantKey.tenantId, packageId: package.id))
                        dismiss()
                    }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                } else {
                    Button("Submit Request") { Task { await submit() } }
                        .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent).disabled(!canSubmit)
                }
            }
        }
        .padding(18)
        .frame(width: 440)
        .task { await load() }
    }

    @ViewBuilder private var form: some View {
        if requirements.count > 1 {
            VStack(alignment: .leading, spacing: 5) {
                Text("Policy").font(.caption.weight(.semibold))
                Picker("", selection: Binding(get: { selectedPolicyId ?? requirements[0].id }, set: { selectedPolicyId = $0 })) {
                    ForEach(requirements) { r in Text(Self.policyLabel(r)).tag(r.id) }
                }
                .labelsHidden()
                Text("\(requirements.count) policies let you request this package. The policy sets the duration and who approves.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        VStack(alignment: .leading, spacing: 5) {
            Text("Justification").font(.caption.weight(.semibold))
            TextField("Why you need this access", text: $justification, axis: .vertical).lineLimit(3...5)
            Text("Required. Approvers see this text.").font(.caption).foregroundStyle(.secondary)
        }
        if let submitError {
            Label(submitError, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.red)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func questionsNotice(_ policy: PolicyRequirement) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: "info.circle").foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 3) {
                Text("This package asks questions before it can be requested.")
                Text("Policy \u{201C}\(policy.displayName)\u{201D} requires answers that Elevate does not collect. Continue in the My Access portal, then check the Requested tab here.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(10)
        .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 8))
    }

    static func policyLabel(_ r: PolicyRequirement) -> String {
        var parts = [r.displayName]
        if let d = r.description, !d.isEmpty { parts.append(d) }
        parts.append(r.isApprovalRequired ? "approval required" : "no approval")
        return parts.joined(separator: " · ")
    }

    private func load() async {
        loading = true
        defer { loading = false }
        do {
            requirements = try await model.packageRequirements(tenantKey, packageId: package.id)
            selectedPolicyId = requirements.first?.id
            loadError = nil
        } catch {
            loadError = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }

    private func submit() async {
        guard let selected else { return }
        submitting = true
        defer { submitting = false }
        do {
            try await model.requestPackage(tenantKey, packageId: package.id,
                                           policyId: requirements.count > 1 ? selected.id : nil,
                                           justification: justification.trimmingCharacters(in: .whitespacesAndNewlines))
            submitError = nil
            onSubmitted()
            dismiss()
        } catch {
            submitError = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }
}
```

- [ ] **Step 5: Route the window**

In `RouteWindow.swift`, replace the temporary case from Task 8 with:

```swift
        case .accessPackages(let tenantKey): AccessPackagesView(tenantKey: tenantKey)
```

- [ ] **Step 6: Run the tests, build, relaunch**

Run the hosted suite filtered to `AppModelAccessPackagesTests`.
Expected: PASS (9 tests).

```bash
xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build && open build/Build/Products/Debug/Elevate.app
```

- [ ] **Step 7: Commit**

```bash
git add macos/Sources/ElevateApp/Views/AccessPackagesView.swift macos/Sources/ElevateApp/Views/RequestPackageSheet.swift macos/Sources/ElevateApp/Views/RouteWindow.swift macos/Tests/ElevateAppTests/AppModelAccessPackagesTests.swift
git commit -m "App: access packages window and request sheet"
```

---

### Task 13: Changelog, full verification, deferred checks

**Files:**
- Modify: `CHANGELOG.md:8`
- Modify: `docs/access-packages.md` (only if any label in the UI differs from the tutorial's wording)

- [ ] **Step 1: Changelog**

Under `## [Unreleased]` add:

```markdown
### Added

- Access packages: each tenant whose sign-in carries the `EntitlementMgmt-SubjectAccess.ReadWrite`
  permission shows a box glyph and an "Access packages…" menu item that open a window with
  Available, Requested, Assigned and Declined tabs. Request with a justification and, when several
  apply, a policy; packages that ask questions hand off to the My Access portal. Cancel pending
  requests. Notifications when a request is approved, denied or fails, and when an assignment is
  revoked or expires. Polled on panel open (at most every 15 minutes) and every 8 hours.
- New eligible roles are marked in the panel with a "new" badge and announced once per tenant;
  the marker clears the second time the panel opens.

### Changed

- The app registration gains one user-consentable Graph permission; see
  `docs/entra-app-registration.md`.
```

- [ ] **Step 2: Full test runs**

From `macos/`:

```bash
swift test
xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates test
```

Expected: both green. Record the Core test count in the PR description.

- [ ] **Step 3: Reconcile the tutorial with the shipped labels**

Read `docs/access-packages.md` against the UI strings in Task 11 and Task 12 (button titles, tab names, empty-state text, notification titles). Fix any wording that differs in the tutorial, not in the code.

- [ ] **Step 4: Commit and open the PR**

```bash
git add CHANGELOG.md docs/access-packages.md
git commit -m "Changelog and tutorial wording for access packages"
git push -u origin access-packages
gh pr create --title "Access packages" --body-file - <<'EOF'
Implements docs/superpowers/specs/2026-09-08-elevate-access-packages-design.md.

Deferred checks (need a real tenant):
- Incremental consent prompt on next sign-in in an already-consented tenant.
- Request round trip: request, approval in the portal, "Access package approved" notification after a forced or background poll, assignment on the Assigned tab.
- Revocation and expiry notifications.
- My Access hand-off URL opens the right package.
EOF
```

The deferred checks are the user's to run; list them in the final summary as untested.

---

## Self-review

**Spec coverage.** §1 entry point → Task 11; four tabs and search → Task 12; request flow with policy pick and My Access hand-off → Tasks 4, 12; cancel → Tasks 4, 9, 12; notifications for approved/denied/failed/revoked/expired → Tasks 5, 8, 9; new-role accent and notification → Tasks 6, 10, 11; quiet cadence (15 min throttle, 8 h tick, not on the active timer) → Task 9; §2 scope, tenant flag, docs → Task 1; §3 models, provider, diff, tracker → Tasks 2–6; §4 state → Task 7; model → Tasks 9–10; views → Tasks 11–12; consent error state in the window → Task 12; §5 tests → each task; deferred manual checks → Task 13; §6 delivery → Task 13.

**Placeholder scan.** No TBD/TODO. Every code step contains its code. The one "read the file first" note (Task 7 store load method name) names the exact file and what to look for.

**Type consistency.** `PolicyRequirement.id` is the policy id everywhere (Tasks 2, 4, 12). `AccessPackageSnapshot(requests:assignments:)` initializer used in Tasks 5, 7, 9. `ExpiryNotifying.setPackageExpiries` defined in Task 8, called in Task 9, stubbed in `RecordingNotifier` and `NoopNotifier`. `AppModel.pollAccessPackages(_:)`, `pollAccessPackagesIfDue(force:)`, `requestablePackages(_:)`, `packageRequirements(_:packageId:)`, `requestPackage(_:packageId:policyId:justification:)`, `cancelPackageRequest(_:requestId:)`, `accessPackageSnapshot(_:)`, `accessPackagesPolledAt(_:)`, `accessPackageErrors`, `accessPackagesPolling`, `isNewRole(_:)`, `observeDiscoveredRoles(_:discovered:)` are named identically in Tasks 9, 10 and 12. `makeModel(notifier:)` parameter added in Task 9 and used in Tasks 9–10.
