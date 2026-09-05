# PimTray Phase 2 (Azure resource roles) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Discover, activate, deactivate and cancel Azure resource PIM roles through ARM, shown as ordinary rows beside Entra roles.

**Architecture:** A real `AzureResourceProvider` in `PimTrayCore` behind the existing `PIMProvider` protocol, using the existing `GraphTransport` plumbing with an ARM error mapper and `nextLink` paging. `AppModel.refresh` becomes provider-agnostic. `EligibleRole` gains an optional `detail` caption.

**Tech Stack:** Swift 6.2 / Xcode 26.6, Swift Testing, ARM `Microsoft.Authorization` API 2020-10-01 (role definitions 2022-04-01), MSAL scope `https://management.azure.com/user_impersonation`.

**Spec:** `docs/superpowers/specs/2026-09-04-pimtray-phase2-azure-design.md` (extends `docs/superpowers/specs/2026-09-04-pimtray-design.md`).

## Global Constraints

- Swift language mode 6, strict concurrency complete; macOS 26.0 minimum.
- `PimTrayCore` imports only Foundation. Tests use Swift Testing; network is stubbed through `HTTPClient` (`StubHTTPClient`, `FakeTokenProvider`, `Fixtures.data(_:)` in `Tests/PimTrayCoreTests/Support`).
- ARM base `https://management.azure.com`, api-version `2020-10-01` (role definitions `2022-04-01`); token scopes `ArmScopes.all`.
- Existing public API stays source-compatible: `EligibleRole.detail` is optional with a default; `GraphTransport` keeps its current initializer.
- No `@unchecked Sendable` in `AppModel`. Never commit `PimTrayConfig.plist` or `PimTray.xcodeproj`.
- Commit after every task with the message given. Work on branch `phase-2` from `main`.

## File structure

```
Sources/PimTrayCore/Models/Roles.swift                 + EligibleRole.detail
Sources/PimTrayCore/Catalogue/ManualRoleSource.swift   manual azure detail + name-based dedup
Sources/PimTrayCore/Providers/GraphTransport.swift     + mapper, put(), mapArmError
Sources/PimTrayCore/Providers/AzureResourceProvider.swift   (new; stub removed from StubProviders.swift)
Sources/PimTrayCore/Providers/StubProviders.swift      GroupProvider only
Tests/PimTrayCoreTests/AzureResourceProviderTests.swift, Fixtures/arm-*.json
Sources/PimTrayApp/App/AppModel.swift                  multi-provider refresh
Sources/PimTrayApp/Views/RoleRow.swift                 detail caption
Sources/PimTrayApp/Views/ConfigureRolesView.swift      azure manual displayName = role name
README.md                                              Azure section
```

---

### Task 1: EligibleRole.detail and manual Azure role merge

**Files:**
- Modify: `Sources/PimTrayCore/Models/Roles.swift`, `Sources/PimTrayCore/Catalogue/ManualRoleSource.swift`
- Test: `Tests/PimTrayCoreTests/RoleCatalogueTests.swift` (append)

**Interfaces:**
- Produces: `EligibleRole.detail: String?` (init parameter `detail: String? = nil`, placed after `displayName`); `ManualRoleSource.eligibleRoles` sets `detail = scope` for `.azureResource`; `ManualRoleSource.merge` also drops a manual `.azureResource` entry when a discovered `.azureResource` role has the same scope and display name (both case-insensitive).

- [ ] **Step 1: Write the failing tests** (append to `RoleCatalogueTests`)

```swift
    @Test func manualAzureRoleCarriesScopeAsDetail() {
        let tk = TenantKey(identityId: "i", tenantId: "t")
        let manual = [ManualRole(tenantKey: tk, scope: .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: "Contributor"), displayName: "Contributor")]
        let roles = ManualRoleSource.eligibleRoles(from: manual, tenantKey: tk)
        #expect(roles[0].detail == "/subscriptions/sub-1")
        #expect(roles[0].displayName == "Contributor")
    }

    @Test func mergeDropsManualAzureRoleMatchingDiscoveredByScopeAndName() {
        let tk = TenantKey(identityId: "i", tenantId: "t")
        let discovered = EligibleRole(key: RoleKey(identityId: "i", tenantId: "t", scope: .azureResource(scope: "/subscriptions/SUB-1", roleDefinitionId: "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac")),
                                      displayName: "Contributor", detail: "Pay-As-You-Go · subscription", source: .discovered, policy: .manualDefault)
        let manualSame = EligibleRole(key: RoleKey(identityId: "i", tenantId: "t", scope: .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: "contributor")),
                                      displayName: "contributor", detail: "/subscriptions/sub-1", source: .manual, policy: .manualDefault)
        let manualOther = EligibleRole(key: RoleKey(identityId: "i", tenantId: "t", scope: .azureResource(scope: "/subscriptions/sub-2", roleDefinitionId: "Reader")),
                                       displayName: "Reader", detail: "/subscriptions/sub-2", source: .manual, policy: .manualDefault)
        let merged = ManualRoleSource.merge(discovered: [discovered], manual: [manualSame, manualOther])
        #expect(merged.map(\.displayName) == ["Contributor", "Reader"])
        _ = tk
    }

    @Test func detailRoundTripsAndDefaultsToNil() throws {
        let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
        let role = EligibleRole(key: key, displayName: "X", source: .discovered, policy: .manualDefault)
        #expect(role.detail == nil)
        let data = try JSONEncoder().encode(EligibleRole(key: key, displayName: "X", detail: "d", source: .manual, policy: .manualDefault))
        #expect(try JSONDecoder().decode(EligibleRole.self, from: data).detail == "d")
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
swift test --filter RoleCatalogueTests 2>&1 | grep -E "error:|failed" | head -5
```
Expected: compile errors on `detail:`.

- [ ] **Step 3: Implement**

In `Roles.swift`, `EligibleRole`:
```swift
public struct EligibleRole: Codable, Hashable, Sendable, Identifiable {
    public let key: RoleKey
    public var displayName: String
    /// Secondary caption, e.g. the Azure scope's display name and type. Nil for Entra roles.
    public var detail: String?
    public var source: RoleSource
    public var policy: RolePolicy
    public var id: RoleKey { key }

    public init(key: RoleKey, displayName: String, detail: String? = nil, source: RoleSource, policy: RolePolicy) {
        self.key = key
        self.displayName = displayName
        self.detail = detail
        self.source = source
        self.policy = policy
    }
}
```

In `ManualRoleSource.swift`:
```swift
    public static func eligibleRoles(from manual: [ManualRole], tenantKey: TenantKey) -> [EligibleRole] {
        manual.filter { $0.tenantKey == tenantKey }.map {
            let detail: String? = if case .azureResource(let scope, _) = $0.scope { scope } else { nil }
            return EligibleRole(key: RoleKey(identityId: tenantKey.identityId, tenantId: tenantKey.tenantId, scope: $0.scope),
                                displayName: $0.displayName, detail: detail, source: .manual, policy: .manualDefault)
        }
    }

    /// Discovered roles win over manual entries with the same key; a manual Azure entry is also dropped when a
    /// discovered Azure role has the same scope and display name (the manual entry names the role, ARM ids it).
    public static func merge(discovered: [EligibleRole], manual: [EligibleRole]) -> [EligibleRole] {
        let known = Set(discovered.map(\.key))
        let azureNames = Set(discovered.compactMap { role -> String? in
            guard case .azureResource(let scope, _) = role.key.scope else { return nil }
            return scope.lowercased() + "|" + role.displayName.lowercased()
        })
        return discovered + manual.filter { role in
            guard !known.contains(role.key) else { return false }
            if case .azureResource(let scope, _) = role.key.scope {
                return !azureNames.contains(scope.lowercased() + "|" + role.displayName.lowercased())
            }
            return true
        }
    }
```

- [ ] **Step 4: Run the full suite**

```bash
swift test 2>&1 | tail -1
```
Expected: all pass (existing `mergePrefersDiscovered` still passes).

- [ ] **Step 5: Commit**

```bash
git add Sources/PimTrayCore Tests/PimTrayCoreTests
git commit -m "Add EligibleRole.detail and name-based merge for manual Azure roles"
```

---

### Task 2: Transport additions: ARM error mapper, PUT, mapper injection

**Files:**
- Modify: `Sources/PimTrayCore/Providers/GraphTransport.swift`
- Test: `Tests/PimTrayCoreTests/SmokeTests.swift` (append to `GraphTransportErrorTests`)

**Interfaces:**
- Produces: `GraphTransport.init(http:tokens:mapper:)` with `mapper: @Sendable (HTTPResponse) -> PIMError = GraphTransport.mapError`; `put(identity:tenantId:url:scopes:body:)`; `static func mapArmError(_:) -> PIMError`; `static let armBase = URL(string: "https://management.azure.com")!`.

- [ ] **Step 1: Write the failing tests**

```swift
    @Test func armForbiddenIsAPermissionFailureNotConsent() {
        let r = HTTPResponse(status: 403, headers: [:], body: Data(#"{"error":{"code":"AuthorizationFailed","message":"no"}}"#.utf8))
        #expect(GraphTransport.mapArmError(r) == .policyViolation("Not permitted at this scope"))
    }

    @Test func armSharesClaimsAndThrottlingMapping() {
        let claims = #"{"access_token":{"acrs":{"essential":true,"values":["c1"]}}}"#
        let b64 = Data(claims.utf8).base64EncodedString()
        let r = HTTPResponse(status: 401, headers: ["WWW-Authenticate": #"Bearer error="insufficient_claims", claims="\#(b64)""#], body: Data())
        #expect(GraphTransport.mapArmError(r) == .claimsChallenge(claims))
        #expect(GraphTransport.mapArmError(HTTPResponse(status: 429, headers: ["Retry-After": "3"], body: Data())) == .network("Throttled by Microsoft Graph; retry in 3s"))
        let early = HTTPResponse(status: 400, headers: [:], body: Data(#"{"error":{"code":"ActiveDurationTooShort","message":"x"}}"#.utf8))
        #expect(GraphTransport.mapArmError(early) == .policyViolation("Entra requires a role to stay active for 5 minutes before it can be deactivated"))
    }

    @Test func transportUsesInjectedMapperAndSupportsPut() async throws {
        let http = StubHTTPClient()
        await http.on("PUT", "example.test", status: 403, body: Data("{}".utf8))
        let t = GraphTransport(http: http, tokens: FakeTokenProvider(), mapper: GraphTransport.mapArmError)
        let identity = Identity(id: "i", upn: "u", displayName: "U", homeTenantId: "t")
        await #expect(throws: PIMError.policyViolation("Not permitted at this scope")) {
            _ = try await t.put(identity: identity, tenantId: "t", url: URL(string: "https://example.test/x")!, scopes: ArmScopes.all, body: Data("{}".utf8))
        }
        let req = await http.requests.first!
        #expect(req.method == "PUT")
        #expect(req.headers["Content-Type"] == "application/json")
        #expect(req.headers["Authorization"] == "Bearer token-t")
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
swift test --filter GraphTransportErrorTests 2>&1 | grep -E "error:|failed" | head -5
```

- [ ] **Step 3: Implement**

Replace the stored properties and initializer of `GraphTransport` with:
```swift
public struct GraphTransport: Sendable {
    public static let graphBase = URL(string: "https://graph.microsoft.com/v1.0")!
    public static let armBase = URL(string: "https://management.azure.com")!
    let http: any HTTPClient
    let tokens: any TokenProviding
    let mapper: @Sendable (HTTPResponse) -> PIMError

    public init(http: any HTTPClient, tokens: any TokenProviding,
                mapper: @escaping @Sendable (HTTPResponse) -> PIMError = GraphTransport.mapError) {
        self.http = http
        self.tokens = tokens
        self.mapper = mapper
    }
```
Add after `post`:
```swift
    public func put(identity: Identity, tenantId: String, url: URL, scopes: [String], body: Data) async throws -> HTTPResponse {
        try await send(HTTPRequest(method: "PUT", url: url, headers: ["Content-Type": "application/json"], body: body),
                       identity: identity, tenantId: tenantId, scopes: scopes)
    }
```
In `send`, replace `throw Self.mapError(response)` with `throw mapper(response)`.

Add the ARM mapper (403 differs; everything else delegates):
```swift
    /// ARM variant: 403 is an RBAC denial at that scope, not a missing admin consent.
    public static func mapArmError(_ r: HTTPResponse) -> PIMError {
        if r.status == 403 { return .policyViolation("Not permitted at this scope") }
        return mapError(r)
    }
```

- [ ] **Step 4: Run the full suite, then commit**

```bash
swift test 2>&1 | tail -1
git add Sources/PimTrayCore Tests/PimTrayCoreTests
git commit -m "Add ARM error mapping, PUT and injectable mapper to the transport"
```

---

### Task 3: AzureResourceProvider reads (eligible, active, pending, paging)

**Files:**
- Create: `Sources/PimTrayCore/Providers/AzureResourceProvider.swift`
- Modify: `Sources/PimTrayCore/Providers/StubProviders.swift` (delete the `AzureResourceProvider` stub, keep `GroupProvider`)
- Create fixtures: `Tests/PimTrayCoreTests/Fixtures/arm-eligible.json`, `arm-eligible-page2.json`, `arm-active.json`, `arm-pending.json`
- Test: `Tests/PimTrayCoreTests/AzureResourceProviderTests.swift`

**Interfaces:**
- Produces: `struct AzureResourceProvider: PIMProvider { init(http:tokens:) }` with `kind = .azureResource`, `scopes = ArmScopes.all`; reads implemented; `policy`, `activate`, `deactivate`, `cancelPendingRequest` throw 501 until Task 4. Internal helpers used by Task 4: `func listAll<T: Decodable>(_ type: T.Type, identity:tenantId:url:) async throws -> [T]` (follows `nextLink`), `struct Instance` (wire model), `func armURL(_ path: String, apiVersion: String = "2020-10-01", query: [String: String] = [:]) throws -> URL`.

- [ ] **Step 1: Write fixtures**

`arm-eligible.json`:
```json
{
  "value": [
    {
      "name": "elig-1",
      "id": "/subscriptions/sub-1/providers/Microsoft.Authorization/RoleEligibilityScheduleInstances/elig-1",
      "properties": {
        "scope": "/subscriptions/sub-1",
        "roleDefinitionId": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c",
        "principalId": "user-obj-1",
        "status": "Provisioned",
        "roleEligibilityScheduleId": "/subscriptions/sub-1/providers/Microsoft.Authorization/RoleEligibilitySchedules/b1477448-2cc6-4ceb-93b4-54a202a89413",
        "startDateTime": "2026-01-01T00:00:00Z",
        "endDateTime": null,
        "memberType": "Direct",
        "expandedProperties": {
          "scope": { "id": "/subscriptions/sub-1", "displayName": "Pay-As-You-Go", "type": "subscription" },
          "roleDefinition": { "id": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c", "displayName": "Contributor", "type": "BuiltInRole" },
          "principal": { "id": "user-obj-1", "displayName": "U", "type": "User" }
        }
      }
    }
  ],
  "nextLink": "https://management.azure.com/providers/Microsoft.Authorization/roleEligibilityScheduleInstances?api-version=2020-10-01&$filter=asTarget()&$skiptoken=page2"
}
```

`arm-eligible-page2.json`:
```json
{
  "value": [
    {
      "name": "elig-2",
      "id": "/subscriptions/sub-1/resourceGroups/rg-ops/providers/Microsoft.Authorization/RoleEligibilityScheduleInstances/elig-2",
      "properties": {
        "scope": "/subscriptions/sub-1/resourceGroups/rg-ops",
        "roleDefinitionId": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7",
        "principalId": "user-obj-1",
        "status": "Provisioned",
        "roleEligibilityScheduleId": "/subscriptions/sub-1/resourceGroups/rg-ops/providers/Microsoft.Authorization/RoleEligibilitySchedules/22222222-2222-2222-2222-222222222222",
        "startDateTime": "2026-01-01T00:00:00Z",
        "memberType": "Group",
        "expandedProperties": {
          "scope": { "id": "/subscriptions/sub-1/resourceGroups/rg-ops", "displayName": "rg-ops", "type": "resourcegroup" },
          "roleDefinition": { "id": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7", "displayName": "Reader", "type": "BuiltInRole" },
          "principal": { "id": "user-obj-1", "displayName": "U", "type": "User" }
        }
      }
    }
  ]
}
```

`arm-active.json`:
```json
{
  "value": [
    {
      "name": "inst-1",
      "id": "/subscriptions/sub-1/providers/Microsoft.Authorization/RoleAssignmentScheduleInstances/inst-1",
      "properties": {
        "scope": "/subscriptions/sub-1",
        "roleDefinitionId": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c",
        "principalId": "user-obj-1",
        "status": "Provisioned",
        "assignmentType": "Activated",
        "linkedRoleEligibilityScheduleId": "/subscriptions/sub-1/providers/Microsoft.Authorization/RoleEligibilitySchedules/b1477448-2cc6-4ceb-93b4-54a202a89413",
        "startDateTime": "2026-09-04T08:00:00Z",
        "endDateTime": "2026-09-04T12:00:00Z",
        "expandedProperties": { "scope": { "id": "/subscriptions/sub-1", "displayName": "Pay-As-You-Go", "type": "subscription" }, "roleDefinition": { "id": "x", "displayName": "Contributor", "type": "BuiltInRole" } }
      }
    },
    {
      "name": "inst-2",
      "id": "/subscriptions/sub-1/providers/Microsoft.Authorization/RoleAssignmentScheduleInstances/inst-2",
      "properties": {
        "scope": "/subscriptions/sub-1",
        "roleDefinitionId": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635",
        "principalId": "user-obj-1",
        "status": "Provisioned",
        "assignmentType": "Assigned",
        "startDateTime": "2026-01-01T00:00:00Z",
        "endDateTime": null
      }
    }
  ]
}
```

`arm-pending.json`:
```json
{
  "value": [
    {
      "name": "req-77",
      "id": "/subscriptions/sub-1/resourceGroups/rg-ops/providers/Microsoft.Authorization/RoleAssignmentScheduleRequests/req-77",
      "properties": {
        "scope": "/subscriptions/sub-1/resourceGroups/rg-ops",
        "roleDefinitionId": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7",
        "principalId": "user-obj-1",
        "requestType": "SelfActivate",
        "status": "PendingApproval",
        "createdOn": "2026-09-04T09:00:00Z",
        "scheduleInfo": { "startDateTime": "2026-09-04T09:00:00Z", "expiration": { "type": "AfterDuration", "duration": "PT2H", "endDateTime": null } }
      }
    },
    {
      "name": "req-70",
      "id": "/subscriptions/sub-1/providers/Microsoft.Authorization/RoleAssignmentScheduleRequests/req-70",
      "properties": { "scope": "/subscriptions/sub-1", "roleDefinitionId": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c", "principalId": "user-obj-1", "requestType": "SelfActivate", "status": "Provisioned", "createdOn": "2026-09-04T08:00:00Z" }
    }
  ]
}
```

- [ ] **Step 2: Write the failing tests**

```swift
import Testing
import Foundation
@testable import PimTrayCore

@Suite struct AzureResourceProviderTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t1")
    let tenant = TenantContext(identityId: "id1", tenantId: "t1", displayName: "Contoso", source: .home)
    let contributorId = "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c"
    let readerId = "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7"

    func makeProvider() -> (AzureResourceProvider, StubHTTPClient, FakeTokenProvider) {
        let http = StubHTTPClient()
        let tokens = FakeTokenProvider()
        return (AzureResourceProvider(http: http, tokens: tokens), http, tokens)
    }

    @Test func listsEligibleRolesAcrossPagesWithScopeCaption() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        let roles = try await p.eligibleRoles(identity: identity, tenant: tenant)
        #expect(roles.map(\.displayName) == ["Contributor", "Reader"])
        #expect(roles[0].detail == "Pay-As-You-Go · subscription")
        #expect(roles[1].detail == "rg-ops · resource group")
        #expect(roles[0].key.scope == .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: contributorId))
        #expect(roles.allSatisfy { $0.source == .discovered && $0.key.tenantId == "t1" })
        let first = await http.requests.first!
        #expect(first.url.absoluteString.hasPrefix("https://management.azure.com/providers/Microsoft.Authorization/roleEligibilityScheduleInstances"))
        #expect(first.url.absoluteString.contains("api-version=2020-10-01"))
        #expect(first.url.absoluteString.contains("asTarget()"))
        #expect(first.headers["Authorization"] == "Bearer token-t1")
        #expect(await http.requests.count == 2)
    }

    @Test func listsActivatedAndPendingAssignments() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleAssignmentScheduleInstances", body: Fixtures.data("arm-active"))
        await http.on("GET", "roleAssignmentScheduleRequests", body: Fixtures.data("arm-pending"))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 2)
        let contributor = active.first { $0.roleKey.scope == .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: contributorId) }!
        #expect(contributor.status == .active)
        #expect(contributor.assignmentId == "inst-1")
        #expect(contributor.endDateTime == GraphJSON.parseDate("2026-09-04T12:00:00Z"))
        let reader = active.first { $0.roleKey.scope == .azureResource(scope: "/subscriptions/sub-1/resourceGroups/rg-ops", roleDefinitionId: readerId) }!
        #expect(reader.status == .pendingApproval)
        #expect(reader.assignmentId == "req-77")
    }

    @Test func forbiddenIsNotTreatedAsConsent() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances", status: 403, body: Data(#"{"error":{"code":"AuthorizationFailed","message":"x"}}"#.utf8))
        await #expect(throws: PIMError.policyViolation("Not permitted at this scope")) {
            _ = try await p.eligibleRoles(identity: identity, tenant: tenant)
        }
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
swift test --filter AzureResourceProviderTests 2>&1 | grep -E "error:|failed" | head -5
```
Expected: `AzureResourceProvider` has no `init(http:tokens:)`.

- [ ] **Step 4: Implement the provider reads**

Delete the `AzureResourceProvider` struct from `StubProviders.swift`. Create `AzureResourceProvider.swift`:
```swift
import Foundation

/// PIM for Azure resource roles through Azure Resource Manager.
public struct AzureResourceProvider: PIMProvider {
    public let kind: RoleScopeKind = .azureResource
    public let scopes = ArmScopes.all
    let transport: GraphTransport

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        transport = GraphTransport(http: http, tokens: tokens, mapper: GraphTransport.mapArmError)
    }

    // MARK: Wire models

    struct Named: Decodable { let displayName: String?; let type: String?; let id: String? }
    struct Expanded: Decodable { let scope: Named?; let roleDefinition: Named? }
    struct Expiration: Decodable { let type: String?; let duration: String?; let endDateTime: Date? }
    struct ScheduleInfo: Decodable { let startDateTime: Date?; let expiration: Expiration? }
    struct Properties: Decodable {
        let scope: String
        let roleDefinitionId: String
        let principalId: String?
        let status: String?
        let assignmentType: String?
        let roleEligibilityScheduleId: String?
        let linkedRoleEligibilityScheduleId: String?
        let startDateTime: Date?
        let endDateTime: Date?
        let createdOn: Date?
        let scheduleInfo: ScheduleInfo?
        let expandedProperties: Expanded?
    }
    struct Instance: Decodable { let name: String; let id: String; let properties: Properties }
    struct Page<T: Decodable>: Decodable { let value: [T]; let nextLink: String? }

    static let pendingStatuses: Set<String> = ["PendingApproval", "PendingAdminDecision", "PendingApprovalProvisioning"]

    func armURL(_ path: String, apiVersion: String = "2020-10-01", query: [String: String] = [:]) throws -> URL {
        guard var components = URLComponents(url: GraphTransport.armBase.appendingPathComponent(path), resolvingAgainstBaseURL: false) else {
            throw PIMError.unexpected(status: 0, body: "Bad ARM path \(path)")
        }
        var items = [URLQueryItem(name: "api-version", value: apiVersion)]
        for (k, v) in query.sorted(by: { $0.key < $1.key }) { items.append(URLQueryItem(name: k, value: v)) }
        components.queryItems = items
        guard let url = components.url else { throw PIMError.unexpected(status: 0, body: "Bad ARM URL \(path)") }
        return url
    }

    /// GET every page of an ARM list, following `nextLink`.
    func listAll<T: Decodable>(_ type: T.Type, identity: Identity, tenantId: String, url: URL) async throws -> [T] {
        var next: URL? = url
        var out: [T] = []
        while let current = next {
            let r = try await transport.get(identity: identity, tenantId: tenantId, url: current, scopes: scopes)
            let page = try GraphJSON.decoder.decode(Page<T>.self, from: r.body)
            out += page.value
            next = page.nextLink.flatMap(URL.init(string:))
        }
        return out
    }

    static func caption(_ e: Expanded?) -> String? {
        guard let scope = e?.scope, let name = scope.displayName else { return nil }
        let type = switch scope.type?.lowercased() {
        case "subscription": "subscription"
        case "resourcegroup": "resource group"
        case "managementgroup": "management group"
        case let other?: other
        case nil: nil
        }
        return type.map { "\(name) · \($0)" } ?? name
    }

    // MARK: Reads

    public func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] {
        let url = try armURL("providers/Microsoft.Authorization/roleEligibilityScheduleInstances", query: ["$filter": "asTarget()"])
        let items = try await listAll(Instance.self, identity: identity, tenantId: tenant.tenantId, url: url)
        var seen = Set<RoleScope>()
        var roles: [EligibleRole] = []
        for i in items {
            let scope = RoleScope.azureResource(scope: i.properties.scope, roleDefinitionId: i.properties.roleDefinitionId)
            guard seen.insert(scope).inserted else { continue }
            roles.append(EligibleRole(key: RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: scope),
                                      displayName: i.properties.expandedProperties?.roleDefinition?.displayName ?? i.properties.roleDefinitionId,
                                      detail: Self.caption(i.properties.expandedProperties),
                                      source: .discovered, policy: .manualDefault))
        }
        return roles.sorted { ($0.displayName, $0.detail ?? "") < ($1.displayName, $1.detail ?? "") }
    }

    public func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] {
        let instances = try await listAll(Instance.self, identity: identity, tenantId: tenant.tenantId,
                                          url: try armURL("providers/Microsoft.Authorization/roleAssignmentScheduleInstances", query: ["$filter": "asTarget()"]))
        let requests = try await listAll(Instance.self, identity: identity, tenantId: tenant.tenantId,
                                         url: try armURL("providers/Microsoft.Authorization/roleAssignmentScheduleRequests", query: ["$filter": "asTarget()"]))
        var result: [RoleKey: ActiveAssignment] = [:]
        for i in instances where i.properties.assignmentType == "Activated" {
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: .azureResource(scope: i.properties.scope, roleDefinitionId: i.properties.roleDefinitionId))
            result[key] = ActiveAssignment(roleKey: key, assignmentId: i.name, startDateTime: i.properties.startDateTime ?? .now,
                                           endDateTime: i.properties.endDateTime, status: .active)
        }
        for r in requests where Self.pendingStatuses.contains(r.properties.status ?? "") {
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: .azureResource(scope: r.properties.scope, roleDefinitionId: r.properties.roleDefinitionId))
            guard result[key] == nil else { continue }
            result[key] = ActiveAssignment(roleKey: key, assignmentId: r.name,
                                           startDateTime: r.properties.scheduleInfo?.startDateTime ?? r.properties.createdOn ?? .now,
                                           endDateTime: nil, status: .pendingApproval)
        }
        return Array(result.values)
    }

    // Task 4 replaces these.
    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy {
        throw PIMError.unexpected(status: 501, body: "policy: Task 4")
    }
    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        throw PIMError.unexpected(status: 501, body: "activate: Task 4")
    }
    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        throw PIMError.unexpected(status: 501, body: "deactivate: Task 4")
    }
    public func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws {
        throw PIMError.unexpected(status: 501, body: "cancel: Task 4")
    }
}
```

`AppModel.live()`/`init` currently constructs `AzureResourceProvider()`; change that call in `Sources/PimTrayApp/App/AppModel.swift` to `AzureResourceProvider(http: http, tokens: tokens)` so the app still builds (verify with the xcodebuild command in Task 5; `swift test` covers Core now).

- [ ] **Step 5: Run the suite, commit**

```bash
swift test 2>&1 | tail -1
git add Sources Tests
git commit -m "Add AzureResourceProvider eligibility, active and pending reads with paging"
```

---

### Task 4: AzureResourceProvider policy, activate, deactivate, cancel

**Files:**
- Modify: `Sources/PimTrayCore/Providers/AzureResourceProvider.swift`
- Create fixtures: `Tests/PimTrayCoreTests/Fixtures/arm-policy.json`, `arm-activate-response.json`, `arm-roledefinitions.json`
- Test: append to `AzureResourceProviderTests`

**Interfaces:**
- Produces: working `policy`, `activate`, `deactivate`, `cancelPendingRequest`; internal `resolveRoleDefinitionId(_:scope:identity:tenantId:)` and `eligibility(for:identity:tenantId:) -> (principalId: String, scheduleName: String)`.

- [ ] **Step 1: Write fixtures**

`arm-policy.json`:
```json
{
  "value": [
    {
      "name": "p1_b24988ac",
      "id": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleManagementPolicyAssignment/p1_b24988ac",
      "properties": {
        "scope": "/subscriptions/sub-1",
        "roleDefinitionId": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c",
        "policyId": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleManagementPolicies/p1",
        "effectiveRules": [
          { "id": "Expiration_Admin_Eligibility", "ruleType": "RoleManagementPolicyExpirationRule", "isExpirationRequired": false, "maximumDuration": "P365D" },
          { "id": "Expiration_EndUser_Assignment", "ruleType": "RoleManagementPolicyExpirationRule", "isExpirationRequired": true, "maximumDuration": "PT4H" },
          { "id": "Enablement_EndUser_Assignment", "ruleType": "RoleManagementPolicyEnablementRule", "enabledRules": ["MultiFactorAuthentication", "Justification", "Ticketing"] },
          { "id": "Approval_EndUser_Assignment", "ruleType": "RoleManagementPolicyApprovalRule", "setting": { "isApprovalRequired": true } }
        ]
      }
    },
    {
      "name": "p1_other",
      "id": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleManagementPolicyAssignment/p1_other",
      "properties": {
        "scope": "/subscriptions/sub-1",
        "roleDefinitionId": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7",
        "policyId": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleManagementPolicies/p1",
        "effectiveRules": [ { "id": "Expiration_EndUser_Assignment", "ruleType": "RoleManagementPolicyExpirationRule", "isExpirationRequired": true, "maximumDuration": "PT1H" } ]
      }
    }
  ]
}
```

`arm-activate-response.json`:
```json
{
  "name": "fea7a502-9a96-4806-a26f-eee560e52045",
  "id": "/subscriptions/sub-1/providers/Microsoft.Authorization/RoleAssignmentScheduleRequests/fea7a502-9a96-4806-a26f-eee560e52045",
  "properties": {
    "scope": "/subscriptions/sub-1",
    "roleDefinitionId": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c",
    "principalId": "user-obj-1",
    "requestType": "SelfActivate",
    "status": "Provisioned",
    "scheduleInfo": { "startDateTime": "2026-09-04T09:00:00Z", "expiration": { "type": "AfterDuration", "endDateTime": null, "duration": "PT2H" } },
    "createdOn": "2026-09-04T09:00:00Z"
  }
}
```

`arm-roledefinitions.json`:
```json
{
  "value": [
    { "id": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c", "name": "b24988ac-6180-42a0-ab88-20f7382dd24c", "properties": { "roleName": "Contributor", "type": "BuiltInRole" } }
  ]
}
```

- [ ] **Step 2: Write the failing tests** (append inside the suite)

```swift
    var contributor: EligibleRole {
        EligibleRole(key: RoleKey(identityId: "id1", tenantId: "t1", scope: .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: contributorId)),
                     displayName: "Contributor", detail: "Pay-As-You-Go · subscription", source: .discovered, policy: .manualDefault)
    }

    @Test func readsEndUserPolicyForTheMatchingRoleAtScope() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleManagementPolicyAssignments", body: Fixtures.data("arm-policy"))
        let policy = try await p.policy(for: contributor, identity: identity)
        #expect(policy.maximumDuration == .seconds(4 * 3600))
        #expect(policy.defaultDuration == .seconds(4 * 3600))
        #expect(policy.requiresJustification && policy.requiresMFA && policy.requiresTicket && policy.requiresApproval)
        let req = await http.requests.first!
        #expect(req.url.absoluteString.hasPrefix("https://management.azure.com/subscriptions/sub-1/providers/Microsoft.Authorization/roleManagementPolicyAssignments"))
    }

    @Test func activateLooksUpEligibilityAndPutsSelfActivate() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances", body: Fixtures.data("arm-eligible-page2"))
        await http.on("GET", "roleEligibilityScheduleInstances?", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        await http.on("PUT", "roleAssignmentScheduleRequests", status: 201, body: Fixtures.data("arm-activate-response"))
        let a = try await p.activate(ActivationRequest(roleKey: contributor.key, duration: .seconds(7200), justification: "INC-1", ticket: TicketInfo(number: "42", system: "Jira")), identity: identity)
        #expect(a.status == .active)
        #expect(a.assignmentId == "fea7a502-9a96-4806-a26f-eee560e52045")
        #expect(a.endDateTime == GraphJSON.parseDate("2026-09-04T11:00:00Z"))
        let put = await http.requests(matching: "roleAssignmentScheduleRequests").first!
        #expect(put.method == "PUT")
        #expect(put.url.absoluteString.hasPrefix("https://management.azure.com/subscriptions/sub-1/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/"))
        let name = put.url.lastPathComponent
        #expect(UUID(uuidString: name) != nil)
        let body = try JSONSerialization.jsonObject(with: put.body!) as! [String: Any]
        let props = body["properties"] as! [String: Any]
        #expect(props["requestType"] as? String == "SelfActivate")
        #expect(props["principalId"] as? String == "user-obj-1")
        #expect(props["roleDefinitionId"] as? String == contributorId)
        #expect(props["linkedRoleEligibilityScheduleId"] as? String == "b1477448-2cc6-4ceb-93b4-54a202a89413")
        #expect(props["justification"] as? String == "INC-1")
        #expect((props["ticketInfo"] as? [String: Any])?["ticketNumber"] as? String == "42")
        let exp = (props["scheduleInfo"] as! [String: Any])["expiration"] as! [String: Any]
        #expect(exp["type"] as? String == "AfterDuration")
        #expect(exp["duration"] as? String == "PT2H")
    }

    @Test func manualRoleNameIsResolvedBeforeActivation() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleDefinitions?", body: Fixtures.data("arm-roledefinitions"))
        await http.on("GET", "roleEligibilityScheduleInstances?", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        await http.on("PUT", "roleAssignmentScheduleRequests", status: 201, body: Fixtures.data("arm-activate-response"))
        let manualKey = RoleKey(identityId: "id1", tenantId: "t1", scope: .azureResource(scope: "/subscriptions/SUB-1", roleDefinitionId: "Contributor"))
        let a = try await p.activate(ActivationRequest(roleKey: manualKey, duration: .seconds(3600), justification: "x"), identity: identity)
        #expect(a.status == .active)
        let defs = await http.requests(matching: "roleDefinitions?").first!
        #expect(defs.url.absoluteString.contains("api-version=2022-04-01"))
        #expect(defs.url.absoluteString.lowercased().contains("rolename%20eq%20'contributor'") || defs.url.absoluteString.lowercased().contains("rolename eq 'contributor'"))
        let put = await http.requests(matching: "roleAssignmentScheduleRequests").first!
        let props = (try JSONSerialization.jsonObject(with: put.body!) as! [String: Any])["properties"] as! [String: Any]
        #expect(props["roleDefinitionId"] as? String == contributorId)
    }

    @Test func activateWithoutMatchingEligibilityIsNotEligible() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances?", body: Fixtures.data("arm-eligible-page2"))
        let key = RoleKey(identityId: "id1", tenantId: "t1", scope: .azureResource(scope: "/subscriptions/sub-9", roleDefinitionId: contributorId))
        await #expect(throws: PIMError.notEligible) {
            _ = try await p.activate(ActivationRequest(roleKey: key, duration: .seconds(3600), justification: "x"), identity: identity)
        }
    }

    @Test func deactivatePutsSelfDeactivate() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances?", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        await http.on("PUT", "roleAssignmentScheduleRequests", status: 201, body: Fixtures.data("arm-activate-response"))
        let a = ActiveAssignment(roleKey: contributor.key, assignmentId: "inst-1", startDateTime: .now, endDateTime: nil, status: .active)
        try await p.deactivate(a, identity: identity)
        let put = await http.requests(matching: "roleAssignmentScheduleRequests").first!
        let props = (try JSONSerialization.jsonObject(with: put.body!) as! [String: Any])["properties"] as! [String: Any]
        #expect(props["requestType"] as? String == "SelfDeactivate")
        #expect(props["linkedRoleEligibilityScheduleId"] as? String == "b1477448-2cc6-4ceb-93b4-54a202a89413")
        #expect(props["scheduleInfo"] == nil)
    }

    @Test func cancelPostsToTheRequestAtItsScope() async throws {
        let (p, http, _) = makeProvider()
        await http.on("POST", "/cancel", status: 200, body: Data())
        let key = RoleKey(identityId: "id1", tenantId: "t1", scope: .azureResource(scope: "/subscriptions/sub-1/resourceGroups/rg-ops", roleDefinitionId: readerId))
        let a = ActiveAssignment(roleKey: key, assignmentId: "req-77", startDateTime: .now, endDateTime: nil, status: .pendingApproval)
        try await p.cancelPendingRequest(a, identity: identity)
        let post = await http.requests.first!
        #expect(post.method == "POST")
        #expect(post.url.absoluteString.hasPrefix("https://management.azure.com/subscriptions/sub-1/resourceGroups/rg-ops/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/req-77/cancel"))
    }
```

Note on the stub router: `StubHTTPClient` picks the *last* matching route, so register the broad `roleEligibilityScheduleInstances` route before the more specific `?`/`skiptoken` ones, as written above.

- [ ] **Step 3: Run tests to verify they fail**

```bash
swift test --filter AzureResourceProviderTests 2>&1 | grep -E "failed|error:" | head
```

- [ ] **Step 4: Replace the four 501 stubs**

```swift
    // MARK: Policy

    struct PolicyRule: Decodable {
        let id: String
        let maximumDuration: String?
        let enabledRules: [String]?
        let setting: ApprovalSetting?
        struct ApprovalSetting: Decodable { let isApprovalRequired: Bool? }
    }
    struct PolicyProperties: Decodable { let roleDefinitionId: String?; let effectiveRules: [PolicyRule]? }
    struct PolicyAssignment: Decodable { let name: String; let properties: PolicyProperties }

    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy {
        guard case .azureResource(let scope, let roleDefinitionId) = role.key.scope else { throw PIMError.notEligible }
        let url = try armURL(scope.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/providers/Microsoft.Authorization/roleManagementPolicyAssignments")
        let assignments = try await listAll(PolicyAssignment.self, identity: identity, tenantId: role.key.tenantId, url: url)
        guard let match = assignments.first(where: { $0.properties.roleDefinitionId?.caseInsensitiveCompare(roleDefinitionId) == .orderedSame }),
              let rules = match.properties.effectiveRules else { return .manualDefault }
        var policy = RolePolicy.manualDefault
        for rule in rules {
            switch rule.id {
            case "Expiration_EndUser_Assignment":
                if let d = rule.maximumDuration.flatMap(ISO8601Duration.parse) { policy.maximumDuration = d; policy.defaultDuration = d }
            case "Enablement_EndUser_Assignment":
                let enabled = Set(rule.enabledRules ?? [])
                policy.requiresJustification = enabled.contains("Justification")
                policy.requiresTicket = enabled.contains("Ticketing")
                policy.requiresMFA = enabled.contains("MultiFactorAuthentication")
            case "Approval_EndUser_Assignment":
                policy.requiresApproval = rule.setting?.isApprovalRequired ?? false
            default: break
            }
        }
        return policy
    }

    // MARK: Activation

    struct RoleDefinition: Decodable { let id: String; let properties: Props; struct Props: Decodable { let roleName: String? } }

    /// Manual roles carry a role *name*; ARM wants the definition id at that scope.
    func resolveRoleDefinitionId(_ nameOrId: String, scope: String, identity: Identity, tenantId: String) async throws -> String {
        if nameOrId.contains("/") { return nameOrId }
        let url = try armURL(scope.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/providers/Microsoft.Authorization/roleDefinitions",
                             apiVersion: "2022-04-01", query: ["$filter": "roleName eq '\(nameOrId)'"])
        let defs = try await listAll(RoleDefinition.self, identity: identity, tenantId: tenantId, url: url)
        guard let id = defs.first?.id else { throw PIMError.notEligible }
        return id
    }

    /// Finds the caller's eligibility for a scope + role; ARM needs its principal id and schedule name to activate.
    func eligibility(scope: String, roleDefinitionId: String, identity: Identity, tenantId: String) async throws -> (principalId: String, scheduleName: String) {
        let url = try armURL("providers/Microsoft.Authorization/roleEligibilityScheduleInstances", query: ["$filter": "asTarget()"])
        let items = try await listAll(Instance.self, identity: identity, tenantId: tenantId, url: url)
        guard let match = items.first(where: {
            $0.properties.scope.caseInsensitiveCompare(scope) == .orderedSame &&
            $0.properties.roleDefinitionId.caseInsensitiveCompare(roleDefinitionId) == .orderedSame
        }), let principal = match.properties.principalId, let schedule = match.properties.roleEligibilityScheduleId else {
            throw PIMError.notEligible
        }
        return (principal, schedule.components(separatedBy: "/").last ?? schedule)
    }

    func requestURL(scope: String) throws -> URL {
        try armURL(scope.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/" + UUID().uuidString.lowercased())
    }

    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        guard case .azureResource(let scope, let nameOrId) = request.roleKey.scope else { throw PIMError.notEligible }
        let tenantId = request.roleKey.tenantId
        let roleDefinitionId = try await resolveRoleDefinitionId(nameOrId, scope: scope, identity: identity, tenantId: tenantId)
        let elig = try await eligibility(scope: scope, roleDefinitionId: roleDefinitionId, identity: identity, tenantId: tenantId)
        var props: [String: Any] = [
            "principalId": elig.principalId,
            "roleDefinitionId": roleDefinitionId,
            "requestType": "SelfActivate",
            "linkedRoleEligibilityScheduleId": elig.scheduleName,
            "justification": request.justification,
            "scheduleInfo": [
                "startDateTime": GraphJSON.encoderDateString(.now),
                "expiration": ["type": "AfterDuration", "duration": ISO8601Duration.format(request.duration)],
            ],
        ]
        if let t = request.ticket { props["ticketInfo"] = ["ticketNumber": t.number, "ticketSystem": t.system] }
        let body = try JSONSerialization.data(withJSONObject: ["properties": props])
        let r = try await transport.put(identity: identity, tenantId: tenantId, url: try requestURL(scope: scope), scopes: scopes, body: body)
        let created = try GraphJSON.decoder.decode(Instance.self, from: r.body)
        let start = created.properties.scheduleInfo?.startDateTime ?? .now
        let end = created.properties.scheduleInfo?.expiration?.endDateTime
            ?? created.properties.scheduleInfo?.expiration?.duration.flatMap(ISO8601Duration.parse).map { start.addingTimeInterval(TimeInterval($0.components.seconds)) }
            ?? start.addingTimeInterval(TimeInterval(request.duration.components.seconds))
        let status: ActiveAssignment.Status = switch created.properties.status ?? "Provisioned" {
        case "PendingApproval", "PendingAdminDecision", "PendingApprovalProvisioning": .pendingApproval
        case "PendingProvisioning", "PendingScheduleCreation", "ScheduleCreated", "Accepted", "PendingEvaluation", "ProvisioningStarted", "PendingExternalProvisioning": .pendingProvisioning
        case "Denied", "Failed", "Canceled", "Revoked", "TimedOut", "Invalid", "AdminDenied", "FailedAsResourceIsLocked": .failed(created.properties.status ?? "Failed")
        default: .active
        }
        return ActiveAssignment(roleKey: request.roleKey, assignmentId: created.name, startDateTime: start,
                                endDateTime: status == .active ? end : nil, status: status)
    }

    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard case .azureResource(let scope, let nameOrId) = assignment.roleKey.scope else { throw PIMError.notEligible }
        let tenantId = assignment.roleKey.tenantId
        let roleDefinitionId = try await resolveRoleDefinitionId(nameOrId, scope: scope, identity: identity, tenantId: tenantId)
        let elig = try await eligibility(scope: scope, roleDefinitionId: roleDefinitionId, identity: identity, tenantId: tenantId)
        let props: [String: Any] = [
            "principalId": elig.principalId,
            "roleDefinitionId": roleDefinitionId,
            "requestType": "SelfDeactivate",
            "linkedRoleEligibilityScheduleId": elig.scheduleName,
        ]
        _ = try await transport.put(identity: identity, tenantId: tenantId, url: try requestURL(scope: scope), scopes: scopes,
                                    body: try JSONSerialization.data(withJSONObject: ["properties": props]))
    }

    public func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard case .azureResource(let scope, _) = assignment.roleKey.scope, let name = assignment.assignmentId else { throw PIMError.notEligible }
        let url = try armURL(scope.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/\(name)/cancel")
        _ = try await transport.post(identity: identity, tenantId: assignment.roleKey.tenantId, url: url, scopes: scopes, body: Data())
    }
```

- [ ] **Step 5: Run the suite, commit**

```bash
swift test 2>&1 | tail -1
git add Sources/PimTrayCore Tests/PimTrayCoreTests
git commit -m "Implement Azure PIM policy, activation, deactivation and cancel"
```

---

### Task 5: Provider-agnostic refresh, row caption, configure view, README

**Files:**
- Modify: `Sources/PimTrayApp/App/AppModel.swift`, `Sources/PimTrayApp/Views/RoleRow.swift`, `Sources/PimTrayApp/Views/ConfigureRolesView.swift`, `README.md`

**Interfaces:**
- Consumes: `AzureResourceProvider(http:tokens:)`, `EligibleRole.detail`, `ActivationCoordinator.provider(for:)`.
- Produces: `AppModel.refresh(_:)` that queries every provider in `[.entraDirectory, .azureResource]`; `applyPolicies` picks the provider per role kind.

- [ ] **Step 1: Rewrite `refresh(_:)` and `applyPolicies` in AppModel**

Replace the body of `refresh(_:)` with:
```swift
    func refresh(_ key: TenantKey) async {
        guard let identity = self.identity(key.identityId), var tenant = self.tenant(key) else { return }
        guard !busy.contains(key) else { return }
        busy.insert(key)
        defer { busy.remove(key) }
        tenantErrors[key] = nil

        let providers: [any PIMProvider] = [RoleScopeKind.entraDirectory, .azureResource].compactMap { coordinator.provider(for: $0) }
        // Start from what we already know so a transient failure never blanks a provider's rows.
        var discoveredByKind: [RoleScopeKind: [EligibleRole]] = Dictionary(grouping: roles(for: key).filter { $0.source == .discovered }) { $0.key.scope.kind }
        var errors: [String] = []
        var consentBlocked = tenant.discoveryMode != .automatic
        var kindsWithActive: Set<RoleScopeKind> = []
        var current: [ActiveAssignment] = []

        for provider in providers {
            let kind = provider.kind
            let isEntra = kind == .entraDirectory
            let tenantSnapshot = tenant
            // Eligible roles. Entra honours the consent block; ARM consent is user-consentable.
            if !(isEntra && consentBlocked) {
                do {
                    let found = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
                        try await provider.eligibleRoles(identity: identity, tenant: tenantSnapshot)
                    }
                    discoveredByKind[kind] = await applyPolicies(to: found, identity: identity)
                } catch PIMError.consentRequired where isEntra {
                    discoveredByKind[kind] = []
                    consentBlocked = true
                    tenant.discoveryMode = .manualRoles
                    tenant.lastDiscoveryError = "Role discovery not permitted in this tenant. Configure known roles or ask an admin to consent."
                    state.upsertTenant(tenant)
                    persist()
                } catch is CancellationError {
                    return
                } catch {
                    errors.append("\(Self.label(kind)): \((error as? PIMError)?.userMessage ?? error.localizedDescription)")
                }
            }
            // Active assignments.
            do {
                let snapshot = tenant
                let found: [ActiveAssignment]
                if isEntra && consentBlocked {
                    found = try await provider.activeAssignments(identity: identity, tenant: snapshot)
                } else {
                    found = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: key.tenantId, scopes: provider.scopes) { @Sendable in
                        try await provider.activeAssignments(identity: identity, tenant: snapshot)
                    }
                }
                current += found
                kindsWithActive.insert(kind)
            } catch PIMError.interactionRequired where isEntra && consentBlocked {
            } catch PIMError.consentRequired where isEntra && consentBlocked {
            } catch is CancellationError {
                return
            } catch {
                errors.append("\(Self.label(kind)): \((error as? PIMError)?.userMessage ?? error.localizedDescription)")
            }
        }

        let manual = ManualRoleSource.eligibleRoles(from: state.manualRoles, tenantKey: key)
        let discovered = discoveredByKind.values.flatMap { $0 }.sorted { $0.displayName < $1.displayName }
        roles[key] = ManualRoleSource.merge(discovered: discovered, manual: manual)
        // Replace only the kinds we successfully re-read; keep the rest.
        active = active.filter { !($0.key.tenantKey == key && kindsWithActive.contains($0.key.scope.kind)) }
        for a in current { active[a.roleKey] = a }
        if !errors.isEmpty { tenantErrors[key] = errors.joined(separator: " · ") }
        await rescheduleNotifications()
    }

    private static func label(_ kind: RoleScopeKind) -> String {
        switch kind {
        case .entraDirectory: "Entra"
        case .azureResource: "Azure"
        case .group: "Groups"
        }
    }
```
Change `applyPolicies(to:identity:provider:)` to `applyPolicies(to roles: [EligibleRole], identity: Identity)` and inside the per-role task use `coordinator.provider(for: role.key.scope.kind)` (skip roles whose provider is missing). Update its one remaining call site in `activate(_:)` (manual-role policy refetch) accordingly. Ensure `AzureResourceProvider(http: http, tokens: tokens)` is constructed in `init`.

- [ ] **Step 2: Row caption**

In `RoleRow.swift`, inside the name `VStack`:
```swift
                if let detail = role.detail { Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(1) }
```
placed before the existing `manual` caption.

- [ ] **Step 3: Configure view**

In `ConfigureRolesView.save()`, the Azure rows must produce `displayName: row.roleName` (not `"\(row.roleName) · \(row.scope)"`); `ManualRoleSource` supplies the scope as `detail`. Remove the "Activation for Azure resource roles arrives in phase 2." footnote.

- [ ] **Step 4: README**

Add after "Tenants that refuse consent":
```markdown
## Azure resource roles

Eligibilities on management groups, subscriptions, resource groups and resources
appear as rows with the scope under the role name. The first Azure call in a
tenant asks you to consent to `user_impersonation` for Azure Service Management;
no admin consent is needed. A tenant where you have no Azure access simply shows
no Azure rows. Manual Azure roles are entered as scope + role name and resolved
to the role definition when you activate.
```

- [ ] **Step 5: Build, test, commit**

```bash
swift test 2>&1 | tail -1
xcodegen generate && xcodebuild -project PimTray.xcodeproj -scheme PimTrayApp -configuration Debug -derivedDataPath build build 2>&1 | grep -E "error:|warning:.*Sources/PimTrayApp|BUILD" | head
git add Sources README.md
git commit -m "Refresh every provider, show scope captions and document Azure roles"
```

---

### Task 6: Client ID in Settings and first-run setup view

**Files:**
- Create: `Sources/PimTrayApp/App/AppSettings.swift`, `Sources/PimTrayApp/Views/SettingsView.swift`, `Sources/PimTrayApp/Views/SetupView.swift`
- Delete: `Sources/PimTrayApp/App/AppConfig.swift`, `PimTrayConfig.plist.example`
- Modify: `Sources/PimTrayApp/App/AppModel.swift`, `Sources/PimTrayApp/App/PimTrayApp.swift`, `Sources/PimTrayApp/Views/PanelView.swift`, `project.yml`, `.gitignore`, `README.md`

**Interfaces:**
- Produces:
  - `@MainActor @Observable final class AppSettings { var clientId: String; static let bundleId = "no.frodehus.pimtray"; static var redirectUri: String; var isConfigured: Bool; static func isValidClientId(_:) -> Bool }` backed by `UserDefaults.standard` key `clientId`.
  - `AppModel`: `let settings: AppSettings`; `var isConfigured: Bool`; `func applyClientId(_ id: String) throws` (validates, saves, signs out everything, rebuilds the token provider, coordinator and discovery); `private(set) var tokens/coordinator/discovery` become replaceable.
  - Scenes: `Settings { SettingsView() }`; panel shows `SetupView` when not configured and a "Settings…" footer entry always.

- [ ] **Step 1: AppSettings**

```swift
import Foundation
import Observation

/// User-editable configuration. The client id is the only required value; it lives in UserDefaults, not in a bundled plist.
@MainActor
@Observable
final class AppSettings {
    static let bundleId = "no.frodehus.pimtray"
    static var redirectUri: String { "msauth.\(bundleId)://auth" }
    static let clientIdKey = "clientId"

    var clientId: String {
        didSet { UserDefaults.standard.set(clientId, forKey: Self.clientIdKey) }
    }

    init(defaults: UserDefaults = .standard) {
        clientId = defaults.string(forKey: Self.clientIdKey) ?? ""
    }

    var isConfigured: Bool { Self.isValidClientId(clientId) }

    static func isValidClientId(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return UUID(uuidString: trimmed) != nil && trimmed != "00000000-0000-0000-0000-000000000000"
    }
}
```

- [ ] **Step 2: AppModel**

Replace `AppConfig` usage. Keep the existing initializer signature for the internal dependency-injected form, and rework `live()`:
```swift
    let settings: AppSettings
    private(set) var tokens: any TokenProviding
    private(set) var coordinator: ActivationCoordinator
    private(set) var discovery: TenantDiscovery
    private let http: any HTTPClient
    private let anchor: AuthAnchorWindow?

    var isConfigured: Bool { settings.isConfigured && !(tokens is UnavailableTokenProvider) }

    static func live() -> AppModel {
        let settings = AppSettings()
        let anchor = AuthAnchorWindow()
        let notifier = ExpiryNotifier()
        let tokens: any TokenProviding
        if settings.isConfigured, let msal = try? MSALTokenProvider(clientId: settings.clientId.trimmingCharacters(in: .whitespacesAndNewlines), redirectUri: AppSettings.redirectUri, anchor: anchor) {
            tokens = msal
        } else {
            tokens = UnavailableTokenProvider()
        }
        let model = AppModel(tokens: tokens, http: URLSessionHTTPClient(), store: AppStateStore(), notifier: notifier, settings: settings, anchor: anchor)
        // keep the existing notifier.onExtend / onAuthorizationDenied wiring here
        return model
    }

    /// Saves a new client id. Because MSAL's token cache is per client, every account is signed out.
    func applyClientId(_ raw: String) throws {
        let id = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard AppSettings.isValidClientId(id) else { throw PIMError.unexpected(status: 0, body: "Enter the application (client) ID as a GUID") }
        guard let anchor else { throw PIMError.unexpected(status: 0, body: "Sign-in is unavailable in this build") }
        for identity in state.identities { Task { try? await tokens.signOut(identity) } }
        state = AppState()
        roles = [:]; active = [:]; policyCache = [:]; tenantErrors = [:]; selection = []
        persist()
        settings.clientId = id
        let msal = try MSALTokenProvider(clientId: id, redirectUri: AppSettings.redirectUri, anchor: anchor)
        tokens = msal
        coordinator = ActivationCoordinator(providers: [EntraDirectoryProvider(http: http, tokens: msal), AzureResourceProvider(http: http, tokens: msal), GroupProvider()], tokens: msal)
        discovery = TenantDiscovery(http: http, tokens: msal)
        notice = nil
        startupError = nil
    }
```
The `init` gains `settings: AppSettings = AppSettings()` and `anchor: AuthAnchorWindow? = nil` parameters, stores `http`, and builds `coordinator`/`discovery` as before. `adminConsentURL(tenantId:)` uses `settings.clientId`. Remove `clientId` init parameter if it exists. `addAccount()` returns early with `notice = "Complete initial setup first"` when `!isConfigured`. `startupError` is no longer set for a missing client id.

- [ ] **Step 3: Views**

`SetupView.swift`:
```swift
import SwiftUI

struct SetupView: View {
    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "shield.lefthalf.filled").font(.system(size: 34)).foregroundStyle(.secondary)
            Text("Complete initial setup").font(.headline)
            Text("PimTray needs the application (client) ID of your Entra app registration before it can sign in.")
                .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
            SettingsLink { Text("Open Settings…") }.buttonStyle(.borderedProminent)
        }
        .padding(20)
        .frame(maxWidth: .infinity)
    }
}
```

`SettingsView.swift`:
```swift
import SwiftUI
import AppKit

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var draft = ""
    @State private var error: String?
    @State private var confirmReplace = false

    var body: some View {
        Form {
            Section("Entra app registration") {
                TextField("Application (client) ID", text: $draft, prompt: Text("00000000-0000-0000-0000-000000000000"))
                    .textFieldStyle(.roundedBorder)
                LabeledContent("Redirect URI") {
                    HStack {
                        Text(AppSettings.redirectUri).textSelection(.enabled).font(.caption.monospaced())
                        Button { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(AppSettings.redirectUri, forType: .string) } label: { Image(systemName: "doc.on.doc") }
                            .buttonStyle(.borderless).accessibilityLabel("Copy redirect URI")
                    }
                }
                Text("Register the redirect URI under the iOS/macOS platform with bundle ID \(AppSettings.bundleId), enable public client flows, and add the Graph PIM permissions listed in the README.")
                    .font(.caption).foregroundStyle(.secondary)
                if let error { Text(error).font(.caption).foregroundStyle(.red) }
            }
            HStack {
                Spacer()
                Button("Save") { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(draft.trimmingCharacters(in: .whitespacesAndNewlines) == model.settings.clientId || !AppSettings.isValidClientId(draft))
            }
        }
        .formStyle(.grouped)
        .frame(width: 480)
        .onAppear { draft = model.settings.clientId }
        .confirmationDialog("Change client ID?", isPresented: $confirmReplace) {
            Button("Sign out and change", role: .destructive) { apply() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Saving a different client ID signs out all \(model.identities.count) accounts; you will add them again.")
        }
    }

    private func save() {
        if model.identities.isEmpty { apply() } else { confirmReplace = true }
    }

    private func apply() {
        do { try model.applyClientId(draft); error = nil } catch { self.error = (error as? PIMError)?.userMessage ?? error.localizedDescription }
    }
}
```

`PanelView`: replace the `startupError` branch ordering with: notice banner → `if !model.isConfigured { SetupView() } else if let fatal = model.startupError { … } else if model.identities.isEmpty { … } else { … }`. Footer: `SettingsLink { Text("Settings…") }.buttonStyle(.borderless)` between "Add account…" (disabled when `!model.isConfigured`) and Quit.

`PimTrayApp`: add `Settings { SettingsView().environment(model) }` as a third scene.

- [ ] **Step 4: Remove the plist path**

Delete `AppConfig.swift` and `PimTrayConfig.plist.example`; remove the `PimTrayConfig.plist` optional resource entry from `project.yml`; remove `PimTrayConfig.plist` from `.gitignore`. README: replace the plist step with "Launch PimTray, open Settings (⌘,) from the panel, paste the application (client) ID". Any remaining reference to `AppConfig` must be gone (`grep -rn AppConfig Sources` returns nothing).

- [ ] **Step 5: Build, test, commit**

```bash
swift test 2>&1 | tail -1
xcodegen generate && xcodebuild -project PimTray.xcodeproj -scheme PimTrayApp -configuration Debug -derivedDataPath build build 2>&1 | grep -E "error:|warning:.*Sources/PimTrayApp|BUILD" | head
git add -A Sources project.yml .gitignore README.md && git rm -q PimTrayConfig.plist.example
git commit -m "Move the client ID to Settings with a first-run setup view"
```

---

### Task 7: Panel layout: sticky tenant headers with account context, aligned rows, capped height

**Files:**
- Modify: `Sources/PimTrayApp/Views/PanelView.swift`, `Sources/PimTrayApp/Views/IdentitySection.swift`, `Sources/PimTrayApp/Views/TenantSection.swift`, `Sources/PimTrayApp/Views/RoleRow.swift`

**Interfaces:**
- Produces: `PanelMetrics` (shared insets); `IdentityHeader(identity:)` (accent-tinted account row, a normal row between accounts); `TenantHeader(tenant:identity:expanded:)` (the sticky unit: an accent caption with the account UPN above the tenant line); `TenantRoles(tenant:)` (the rows). `PanelView` renders one `Section` per tenant inside a `LazyVStack(pinnedViews: [.sectionHeaders])`; `IdentitySection` is removed.

Behaviour: while scrolling through a tenant's roles its header stays pinned and shows the account caption; the next tenant of the same account pushes it away and pins with the same caption; a new account appears as a full `IdentityHeader` row, then its first tenant pins.

- [ ] **Step 1: Shared metrics** (top of `PanelView.swift`)

```swift
enum PanelMetrics {
    static let width: CGFloat = 380
    static let maxListHeight: CGFloat = 460
    static let headerInset: CGFloat = 12      // account row, tenant header
    static let roleInset: CGFloat = 28        // role rows: status-dot column
    static let trailingInset: CGFloat = 12
    static let countdownWidth: CGFloat = 44
}
```

- [ ] **Step 2: PanelView list**

Replace the identities `ScrollView` block with:
```swift
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0, pinnedViews: [.sectionHeaders]) {
                        ForEach(model.identities) { identity in
                            IdentityHeader(identity: identity)
                            ForEach(model.tenants(for: identity.id)) { tenant in
                                TenantBlock(identity: identity, tenant: tenant)
                            }
                        }
                    }
                    .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { contentHeight = $0 }
                }
                .frame(height: min(max(contentHeight, 44), PanelMetrics.maxListHeight))
```
and `.frame(width: PanelMetrics.width)` on the outer VStack. `TenantBlock` is a `Section` wrapper so each tenant owns its pinned header:
```swift
struct TenantBlock: View {
    let identity: Identity
    let tenant: TenantContext
    @State private var expanded = true
    var body: some View {
        Section {
            if expanded { TenantRoles(tenant: tenant) }
        } header: {
            TenantHeader(tenant: tenant, identity: identity, expanded: $expanded)
        }
    }
}
```

- [ ] **Step 3: IdentityHeader** (rewrite `IdentitySection.swift`; delete `IdentitySection`)

```swift
import SwiftUI
import PimTrayCore

/// Full account row shown between accounts. Accent-tinted so accounts read differently from tenants.
struct IdentityHeader: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let identity: Identity

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "person.crop.circle.fill").foregroundStyle(Color.accentColor)
            Text(identity.upn).font(.subheadline.weight(.semibold)).lineLimit(1).truncationMode(.middle)
            Spacer(minLength: 8)
            Menu {
                Button("Discover tenants…") { open(.discoverTenants(identity.id)) }
                Button("Add tenant…") { open(.addTenant(identity.id)) }
                Divider()
                Button("Sign out", role: .destructive) { model.signOut(identity) }
            } label: { Image(systemName: "ellipsis.circle") }
            .menuStyle(.borderlessButton).fixedSize()
            .accessibilityLabel("Account actions")
        }
        .padding(.horizontal, PanelMetrics.headerInset)
        .padding(.vertical, 7)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.accentColor.opacity(0.14))
        .overlay(alignment: .bottom) { Rectangle().fill(Color.accentColor.opacity(0.35)).frame(height: 1) }
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }
}
```

- [ ] **Step 4: TenantHeader and TenantRoles** (rewrite `TenantSection.swift`; delete `TenantSection`)

`TenantHeader` is the pinned unit. It must be opaque so rows scroll under it, and it carries the account caption:
```swift
struct TenantHeader: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let tenant: TenantContext
    let identity: Identity
    @Binding var expanded: Bool

    private var roles: [EligibleRole] { model.roles(for: tenant.id) }
    private var activeCount: Int { roles.filter { model.assignment(for: $0.key)?.status == .active }.count }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 4) {
                Image(systemName: "person.crop.circle.fill").font(.caption2)
                Text(identity.upn).font(.caption2.weight(.medium)).lineLimit(1).truncationMode(.middle)
            }
            .foregroundStyle(Color.accentColor)
            HStack(spacing: 6) {
                Button { withAnimation(.snappy) { expanded.toggle() } } label: {
                    Image(systemName: "chevron.right").rotationEffect(.degrees(expanded ? 90 : 0))
                        .font(.caption.weight(.semibold)).foregroundStyle(.secondary).frame(width: 12)
                }
                .buttonStyle(.plain).accessibilityLabel(expanded ? "Collapse tenant" : "Expand tenant")
                Text(tenant.displayName).font(.subheadline)
                // keep the existing: home caption, "manual roles" badge, busy ProgressView
                Spacer()
                // keep the existing: "\(activeCount) active" text and the tenant Menu with its accessibility label
            }
        }
        .padding(.leading, PanelMetrics.headerInset)
        .padding(.trailing, PanelMetrics.trailingInset)
        .padding(.vertical, 5)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.regularMaterial)
        .overlay(alignment: .bottom) { Divider() }
    }
    // keep the existing open(_:) helper for the tenant menu routes
}

struct TenantRoles: View {
    @Environment(AppModel.self) private var model
    let tenant: TenantContext
    private var roles: [EligibleRole] { model.roles(for: tenant.id) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if roles.isEmpty {
                Text(tenant.discoveryMode == .manualRoles ? "No roles configured." : "No eligible roles.")
                    .font(.caption).foregroundStyle(.secondary)
                    .padding(.leading, PanelMetrics.roleInset).padding(.vertical, 6)
            }
            ForEach(roles) { role in RoleRow(role: role) }
            if let err = model.tenantErrors[tenant.id] ?? tenant.lastDiscoveryError {
                Label(err, systemImage: "exclamationmark.circle").font(.caption).foregroundStyle(.orange)
                    .padding(.leading, PanelMetrics.roleInset).padding(.trailing, PanelMetrics.trailingInset).padding(.vertical, 4)
            }
        }
    }
}
```
Every element the old `TenantSection` header had (home caption, manual-roles badge, busy indicator, active count, the tenant menu with Configure/Retry/admin-consent/Remove and its accessibility label) moves into `TenantHeader` unchanged.

- [ ] **Step 5: Role row alignment**

In `RoleRow.swift`: replace `.padding(.leading, 4)` with `.padding(.leading, PanelMetrics.roleInset)` and add `.padding(.trailing, PanelMetrics.trailingInset)` and `.frame(minHeight: 28)` on the outer `HStack`; the countdown `Text` gets `.frame(width: PanelMetrics.countdownWidth, alignment: .trailing)` so the buttons line up with and without a countdown; in select mode the checkbox stays first, before the dot.

- [ ] **Step 6: Build and commit**

```bash
xcodegen generate && xcodebuild -project PimTray.xcodeproj -scheme PimTrayApp -configuration Debug -derivedDataPath build build 2>&1 | grep -E "error:|warning:.*Sources/PimTrayApp|BUILD" | head
git add Sources/PimTrayApp/Views
git commit -m "Sticky tenant headers with account context, accent account rows, aligned role rows"
```
