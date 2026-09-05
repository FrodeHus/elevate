# Elevate Phase 3 — PIM for Groups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** List, activate, deactivate and cancel PIM for Groups eligibilities in a "Groups" tab of the panel, refresh roles automatically after a group activation, and caption role rows granted through a group.

**Architecture:** `ElevateCore` gains a real `GroupProvider` (Graph `identityGovernance/privilegedAccess/group`) with the same `PIMProvider` shape as the Entra provider, a `GroupScopes` set, `EligibleRole.viaGroup` and `TenantContext.groupsUnavailableReason`. `AppModel` runs the group provider for own-app and custom-app identities, keeps group rows in the same `roles` map (kind `.group`), filters by a persisted `panelTab`, and refreshes Entra and Azure roles five seconds after a successful group activation or deactivation. `PanelView` gets a segmented "Roles | Groups" control.

**Tech Stack:** Swift 6.2 (language mode 6, strict concurrency), SwiftUI on macOS 26, Swift Testing, Microsoft Graph v1.0. XcodeGen project in `macos/`.

**Spec:** `docs/superpowers/specs/2026-09-05-elevate-phase3-groups-design.md`

## Global Constraints

- All paths below are relative to `macos/`. Run tests with `swift test` from `macos/`; build the app with `xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build`.
- `ElevateCore` imports only Foundation (and CryptoKit where already used). No `@unchecked Sendable` in `AppModel`. Swift 6 strict concurrency: new types are `Sendable`, provider structs are value types.
- Graph scopes for groups, exact strings: `https://graph.microsoft.com/PrivilegedEligibilitySchedule.Read.AzureADGroup`, `https://graph.microsoft.com/PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup`, `https://graph.microsoft.com/RoleManagementPolicy.Read.AzureADGroup`.
- Group rows use the existing `RoleScope.group(groupId:accessId:)` with `GroupAccess.member` / `.owner`.
- `principalId` in group activate/deactivate bodies is the caller's `oid` from the Graph token via `AccessTokenClaims.objectId`, falling back to the eligibility instance's `principalId`.
- First-party accounts (`identity.signInMethod.isPreauthorisedForEntraActivation == false`) never call the group provider.
- Old `state.json` without the new fields must decode (optional fields, default nil).
- Tests use Swift Testing (`@Suite`, `@Test`, `#expect`), `StubHTTPClient`, `FakeTokenProvider`, `Fixtures.data("name")` loading `Tests/ElevateCoreTests/Fixtures/<name>.json`. Never commit `Elevate.xcodeproj`. Commit after every task with the given message, on `main`.

## File structure

```
Sources/ElevateCore/Auth/TokenProviding.swift              + GroupScopes
Sources/ElevateCore/Models/Roles.swift                     + EligibleRole.viaGroup
Sources/ElevateCore/Models/Identity.swift                  + TenantContext.groupsUnavailableReason
Sources/ElevateCore/Providers/EntraDirectoryProvider.swift  memberType → viaGroup
Sources/ElevateCore/Providers/AzureResourceProvider.swift   memberType + principal name → viaGroup
Sources/ElevateCore/Providers/GroupProvider.swift          new, real provider
Sources/ElevateCore/Providers/StubProviders.swift          delete (only the stub lived there)
Sources/ElevateApp/App/AppSettings.swift                   + panelTab
Sources/ElevateApp/App/AppModel.swift                      group refresh, tab filter, deferred refresh, consent URL
Sources/ElevateApp/Views/PanelView.swift                   segmented control, tab-aware list
Sources/ElevateApp/Views/TenantSection.swift               tab-aware rows and empty states
Sources/ElevateApp/Views/IdentitySection.swift             active count over all kinds (already), no change expected
Sources/ElevateApp/Views/RoleRow.swift                     viaGroup caption
Tests/ElevateCoreTests/GroupProviderTests.swift            new
Tests/ElevateCoreTests/Fixtures/group-*.json               new fixtures
README.md                                                   permissions + Groups tab, link to the guide
../docs/entra-app-registration.md                          new: step-by-step registration guide
../docs/entra-app/required-resource-access.json             new: az ad app create manifest
../docs/entra-app/create-app-registration.sh                new: creates the registration
```

---

### Task 1: Core models — GroupScopes, viaGroup, groupsUnavailableReason

**Files:**
- Modify: `Sources/ElevateCore/Auth/TokenProviding.swift`
- Modify: `Sources/ElevateCore/Models/Roles.swift` (EligibleRole)
- Modify: `Sources/ElevateCore/Models/Identity.swift` (TenantContext)
- Test: `Tests/ElevateCoreTests/ModelsTests.swift`

**Interfaces:**
- Produces: `GroupScopes.all: [String]`; `EligibleRole.viaGroup: String?` with init parameter `viaGroup: String? = nil`; `TenantContext.groupsUnavailableReason: String?` with init parameter `groupsUnavailableReason: String? = nil`.

- [ ] **Step 1: Write the failing tests**

Append to `Tests/ElevateCoreTests/ModelsTests.swift` inside the suite:

```swift
    @Test func groupScopesAreTheThreeGraphGroupPermissions() {
        #expect(GroupScopes.all == [
            "https://graph.microsoft.com/PrivilegedEligibilitySchedule.Read.AzureADGroup",
            "https://graph.microsoft.com/PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup",
            "https://graph.microsoft.com/RoleManagementPolicy.Read.AzureADGroup",
        ])
    }

    @Test func eligibleRoleAndTenantDecodeWithoutGroupFields() throws {
        let roleJSON = #"{"key":{"identityId":"i","tenantId":"t","scope":{"entraDirectory":{"roleDefinitionId":"r","directoryScopeId":"/"}}},"displayName":"R","source":"discovered","policy":{"defaultDuration":{"secondsComponent":3600,"attosecondsComponent":0},"maximumDuration":{"secondsComponent":3600,"attosecondsComponent":0},"requiresJustification":true,"requiresTicket":false,"requiresMFA":false,"requiresApproval":false}}"#
        let role = try JSONDecoder().decode(EligibleRole.self, from: Data(roleJSON.utf8))
        #expect(role.viaGroup == nil)
        var tagged = role
        tagged.viaGroup = "Ops Admins"
        let round = try JSONDecoder().decode(EligibleRole.self, from: JSONEncoder().encode(tagged))
        #expect(round.viaGroup == "Ops Admins")

        let tenantJSON = #"{"identityId":"i","tenantId":"t","displayName":"T","source":"home","discoveryMode":"automatic"}"#
        let tenant = try JSONDecoder().decode(TenantContext.self, from: Data(tenantJSON.utf8))
        #expect(tenant.groupsUnavailableReason == nil)
        var blocked = tenant
        blocked.groupsUnavailableReason = "no"
        #expect(try JSONDecoder().decode(TenantContext.self, from: JSONEncoder().encode(blocked)).groupsUnavailableReason == "no")
    }
```

If the `Duration` JSON shape above fails to decode, build the role in code instead: `EligibleRole(key: RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/")), displayName: "R", source: .discovered, policy: .manualDefault)`, encode it, strip nothing, and only assert the round trip of `viaGroup` plus that decoding the encoded form with `viaGroup` removed via `JSONSerialization` yields nil.

- [ ] **Step 2: Run tests to verify they fail**

Run: `swift test --filter ModelsTests`
Expected: compile error, `GroupScopes` and `viaGroup` not found.

- [ ] **Step 3: Implement**

In `Sources/ElevateCore/Auth/TokenProviding.swift`, after `GraphScopes`:

```swift
/// Delegated Graph permissions for PIM for Groups. Admin-consent only, like the Entra PIM scopes.
public enum GroupScopes {
    public static let all = [
        "https://graph.microsoft.com/PrivilegedEligibilitySchedule.Read.AzureADGroup",
        "https://graph.microsoft.com/PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup",
        "https://graph.microsoft.com/RoleManagementPolicy.Read.AzureADGroup",
    ]
}
```

In `Sources/ElevateCore/Models/Roles.swift`, `EligibleRole`:

```swift
    public var policy: RolePolicy
    /// Name of the group that grants this eligibility (or "group" when only the member type is known);
    /// nil for a direct eligibility. Shown as a caption; activation is unaffected.
    public var viaGroup: String?
    public var id: RoleKey { key }

    public init(key: RoleKey, displayName: String, detail: String? = nil, source: RoleSource, policy: RolePolicy, viaGroup: String? = nil) {
        self.key = key
        self.displayName = displayName
        self.detail = detail
        self.source = source
        self.policy = policy
        self.viaGroup = viaGroup
    }
```

In `Sources/ElevateCore/Models/Identity.swift`, `TenantContext`: add after `entraActivation`

```swift
    /// Set when the group PIM reads are not permitted in this tenant (missing admin consent).
    /// The group provider is skipped while it is set; "Retry discovery" clears it.
    public var groupsUnavailableReason: String?
```

extend the init signature with `groupsUnavailableReason: String? = nil` after `entraActivation` and assign it.

- [ ] **Step 4: Run tests**

Run: `swift test`
Expected: all pass (existing `EligibleRole(...)` call sites compile because the new parameter has a default).

- [ ] **Step 5: Commit**

```bash
git add Sources/ElevateCore Tests/ElevateCoreTests/ModelsTests.swift
git commit -m "Add GroupScopes, EligibleRole.viaGroup and TenantContext.groupsUnavailableReason"
```

---

### Task 2: "via group" captions on Entra and Azure eligibilities

**Files:**
- Modify: `Sources/ElevateCore/Providers/EntraDirectoryProvider.swift` (Schedule struct, eligibleRoles)
- Modify: `Sources/ElevateCore/Providers/AzureResourceProvider.swift` (Expanded struct, eligibleRoles)
- Modify: `Tests/ElevateCoreTests/Fixtures/arm-eligible-page2.json`
- Test: `Tests/ElevateCoreTests/EntraDirectoryProviderTests.swift`, `Tests/ElevateCoreTests/AzureResourceProviderTests.swift`

**Interfaces:**
- Consumes: `EligibleRole.viaGroup` from Task 1.

- [ ] **Step 1: Write the failing tests**

In `EntraDirectoryProviderTests.listsEligibleRolesWithBearerTokenForTenant`, after the `roles[0].key.scope` expectation add:

```swift
        #expect(roles[0].viaGroup == nil)                 // Global Reader: memberType Direct
        #expect(roles[1].viaGroup == "group")             // User Administrator: memberType Group
```

(`entra-eligible.json` already has `memberType: "Group"` on the User Administrator entry.)

In `AzureResourceProviderTests`, find the test that lists eligible roles from `arm-eligible` + `arm-eligible-page2` and add:

```swift
        let viaGroup = roles.first { $0.viaGroup != nil }
        #expect(viaGroup?.viaGroup == "Platform Team")
```

Edit `Tests/ElevateCoreTests/Fixtures/arm-eligible-page2.json`: on its (single) entry set `"memberType": "Group"` and inside `expandedProperties` set `"principal": { "id": "grp-1", "displayName": "Platform Team", "type": "Group" }`. Keep every other field as is.

- [ ] **Step 2: Run tests to verify they fail**

Run: `swift test --filter "EntraDirectoryProviderTests|AzureResourceProviderTests"`
Expected: FAIL on the `viaGroup` expectations.

- [ ] **Step 3: Implement**

Entra: add `let memberType: String?` to `Schedule`. In `eligibleRoles`, build the role with `viaGroup: s.memberType?.caseInsensitiveCompare("Group") == .orderedSame ? "group" : nil`.

Azure: add `let principal: Named?` to `Expanded`, and `let memberType: String?` to `Properties`. In `eligibleRoles`, compute

```swift
            let viaGroup: String? = i.properties.memberType?.caseInsensitiveCompare("Group") == .orderedSame
                ? (i.properties.expandedProperties?.principal?.displayName ?? "group") : nil
```

and pass `viaGroup: viaGroup` to `EligibleRole(...)`.

- [ ] **Step 4: Run tests**

Run: `swift test`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Sources/ElevateCore/Providers Tests/ElevateCoreTests
git commit -m "Caption Entra and Azure eligibilities granted through a group"
```

---

### Task 3: GroupProvider reads — eligible, active, pending

**Files:**
- Create: `Sources/ElevateCore/Providers/GroupProvider.swift`
- Delete: `Sources/ElevateCore/Providers/StubProviders.swift`
- Create: `Tests/ElevateCoreTests/Fixtures/group-eligible.json`, `group-eligible-page2.json`, `group-active.json`, `group-pending.json`
- Test: `Tests/ElevateCoreTests/GroupProviderTests.swift`

**Interfaces:**
- Produces: `public struct GroupProvider: PIMProvider` with `init(http: any HTTPClient, tokens: any TokenProviding)`, `kind == .group`, `scopes == GroupScopes.all`. `eligibleRoles` returns rows keyed `.group(groupId:accessId:)`, `displayName` = group display name (fallback group id), `detail` = `"member"` or `"owner"`, `viaGroup` from `memberType`. `activeAssignments` returns activated instances (`assignmentType == "Activated"`) as `.active` and `PendingApproval` requests as `.pendingApproval`.
- The two `GroupProvider()` call sites in `AppModel` (lines ~100 and ~161) must become `GroupProvider(http: http, tokens: tokens)` / `GroupProvider(http: http, tokens: composite)` in this task so the app target still compiles.

- [ ] **Step 1: Fixtures**

`group-eligible.json`:

```json
{
  "@odata.nextLink": "https://graph.microsoft.com/v1.0/identityGovernance/privilegedAccess/group/eligibilityScheduleInstances/filterByCurrentUser(on='principal')?$skiptoken=page2",
  "value": [
    {
      "id": "gelig-1", "principalId": "user-obj-1", "groupId": "grp-ops", "accessId": "member",
      "memberType": "direct", "startDateTime": "2026-01-01T00:00:00Z", "endDateTime": null,
      "group": { "id": "grp-ops", "displayName": "Ops Admins" }
    },
    {
      "id": "gelig-2", "principalId": "user-obj-1", "groupId": "grp-sec", "accessId": "owner",
      "memberType": "group", "startDateTime": "2026-01-01T00:00:00Z", "endDateTime": null,
      "group": { "id": "grp-sec", "displayName": "Security Owners" }
    }
  ]
}
```

`group-eligible-page2.json`:

```json
{
  "value": [
    {
      "id": "gelig-3", "principalId": "user-obj-1", "groupId": "grp-dev", "accessId": "member",
      "memberType": "direct", "group": { "id": "grp-dev", "displayName": "Dev Contributors" }
    }
  ]
}
```

`group-active.json`:

```json
{
  "value": [
    {
      "id": "ginst-1", "principalId": "user-obj-1", "groupId": "grp-ops", "accessId": "member",
      "assignmentType": "activated", "memberType": "direct",
      "startDateTime": "2026-09-04T12:00:00Z", "endDateTime": "2026-09-04T16:00:00Z",
      "group": { "id": "grp-ops", "displayName": "Ops Admins" }
    },
    {
      "id": "ginst-2", "principalId": "user-obj-1", "groupId": "grp-perm", "accessId": "member",
      "assignmentType": "assigned", "memberType": "direct",
      "startDateTime": "2026-01-01T00:00:00Z", "endDateTime": null,
      "group": { "id": "grp-perm", "displayName": "Permanent" }
    }
  ]
}
```

`group-pending.json`:

```json
{
  "value": [
    {
      "id": "greq-9", "status": "PendingApproval", "action": "selfActivate",
      "principalId": "user-obj-1", "groupId": "grp-sec", "accessId": "owner",
      "createdDateTime": "2026-09-04T13:00:00Z",
      "scheduleInfo": { "startDateTime": "2026-09-04T13:00:00Z", "expiration": { "type": "afterDuration", "duration": "PT2H" } }
    }
  ]
}
```

Note the casing: Graph returns `assignmentType` as `activated`/`assigned` for groups (lower-case). Compare case-insensitively.

- [ ] **Step 2: Write the failing tests**

`Tests/ElevateCoreTests/GroupProviderTests.swift`:

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct GroupProviderTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t-home")
    let tenant = TenantContext(identityId: "id1", tenantId: "t1", displayName: "Contoso", source: .home)

    func makeProvider() -> (GroupProvider, StubHTTPClient, FakeTokenProvider) {
        let http = StubHTTPClient()
        let tokens = FakeTokenProvider()
        return (GroupProvider(http: http, tokens: tokens), http, tokens)
    }

    @Test func listsEligibleGroupsAcrossPagesWithAccessCaption() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("group-eligible-page2"))
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible"))
        let roles = try await p.eligibleRoles(identity: identity, tenant: tenant)
        #expect(roles.map(\.displayName) == ["Dev Contributors", "Ops Admins", "Security Owners"])
        #expect(roles.map(\.detail) == ["member", "member", "owner"])
        #expect(roles[1].key.scope == .group(groupId: "grp-ops", accessId: .member))
        #expect(roles[2].key.scope == .group(groupId: "grp-sec", accessId: .owner))
        #expect(roles[2].viaGroup == "group")
        #expect(roles[1].viaGroup == nil)
        #expect(roles.allSatisfy { $0.source == .discovered && $0.key.tenantId == "t1" })
        let first = await http.requests.first!
        #expect(first.headers["Authorization"] == "Bearer token-t1")
        #expect(first.url.absoluteString.contains("identityGovernance/privilegedAccess/group/eligibilityScheduleInstances/filterByCurrentUser(on='principal')"))
        #expect(first.url.absoluteString.contains("expand=group"))
    }

    @Test func activeKeepsActivatedOnlyAndMergesPending() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "assignmentScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-active"))
        await http.on("GET", "assignmentScheduleRequests/filterByCurrentUser", body: Fixtures.data("group-pending"))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 2)
        let ops = active.first { $0.roleKey.scope == .group(groupId: "grp-ops", accessId: .member) }!
        #expect(ops.status == .active)
        #expect(ops.assignmentId == "ginst-1")
        #expect(ops.endDateTime == GraphJSON.parseDate("2026-09-04T16:00:00Z"))
        let sec = active.first { $0.roleKey.scope == .group(groupId: "grp-sec", accessId: .owner) }!
        #expect(sec.status == .pendingApproval)
        #expect(sec.assignmentId == "greq-9")
        let pendingReq = await http.requests(matching: "assignmentScheduleRequests").first!
        #expect(pendingReq.url.absoluteString.contains("status eq 'PendingApproval'") || pendingReq.url.absoluteString.contains("status%20eq%20'PendingApproval'"))
    }

    @Test func forbiddenReadMapsToConsentRequired() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "eligibilityScheduleInstances", status: 403, body: Data(#"{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}"#.utf8))
        await #expect(throws: PIMError.consentRequired) {
            _ = try await p.eligibleRoles(identity: identity, tenant: tenant)
        }
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `swift test --filter GroupProviderTests`
Expected: compile error, `GroupProvider(http:tokens:)` not found.

- [ ] **Step 4: Implement the provider (reads only; writes throw for now)**

Delete `Sources/ElevateCore/Providers/StubProviders.swift`. Create `Sources/ElevateCore/Providers/GroupProvider.swift`:

```swift
import Foundation

/// PIM for Groups through Microsoft Graph: eligibility for, and activation of, group membership or ownership.
public struct GroupProvider: PIMProvider {
    public let kind: RoleScopeKind = .group
    public let scopes = GroupScopes.all
    let transport: GraphTransport

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        transport = GraphTransport(http: http, tokens: tokens)
    }

    // MARK: Wire models

    struct GroupRef: Decodable { let id: String?; let displayName: String? }
    struct Instance: Decodable {
        let id: String
        let principalId: String?
        let groupId: String
        let accessId: String
        let memberType: String?
        let assignmentType: String?
        let startDateTime: Date?
        let endDateTime: Date?
        let group: GroupRef?
    }
    struct Expiration: Decodable { let type: String?; let duration: String?; let endDateTime: Date? }
    struct ScheduleInfo: Decodable { let startDateTime: Date?; let expiration: Expiration? }
    struct ScheduleRequest: Decodable {
        let id: String
        let status: String
        let groupId: String
        let accessId: String
        let createdDateTime: Date?
        let scheduleInfo: ScheduleInfo?
    }
    struct Page<T: Decodable>: Decodable {
        let value: [T]
        let nextLink: String?
        enum CodingKeys: String, CodingKey { case value; case nextLink = "@odata.nextLink" }
    }

    static let base = "/identityGovernance/privilegedAccess/group"

    func url(_ path: String) throws -> URL {
        if let u = URL(string: GraphTransport.graphBase.absoluteString + path) { return u }
        let encoded = path.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? path
        guard let u = URL(string: GraphTransport.graphBase.absoluteString + encoded) else {
            throw PIMError.unexpected(status: 0, body: "Bad URL")
        }
        return u
    }

    /// GET every page, following `@odata.nextLink`.
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

    static func access(_ raw: String) -> GroupAccess { raw.caseInsensitiveCompare("owner") == .orderedSame ? .owner : .member }
    static func isGroupMember(_ memberType: String?) -> Bool { memberType?.caseInsensitiveCompare("group") == .orderedSame }

    // MARK: Reads

    public func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] {
        let items = try await listAll(Instance.self, identity: identity, tenantId: tenant.tenantId,
                                      url: try url("\(Self.base)/eligibilityScheduleInstances/filterByCurrentUser(on='principal')?$expand=group($select=id,displayName)"))
        var seen = Set<RoleScope>()
        var roles: [EligibleRole] = []
        for i in items {
            let access = Self.access(i.accessId)
            let scope = RoleScope.group(groupId: i.groupId, accessId: access)
            guard seen.insert(scope).inserted else { continue }
            roles.append(EligibleRole(key: RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: scope),
                                      displayName: i.group?.displayName ?? i.groupId,
                                      detail: access == .owner ? "owner" : "member",
                                      source: .discovered, policy: .manualDefault,
                                      viaGroup: Self.isGroupMember(i.memberType) ? "group" : nil))
        }
        return roles.sorted { ($0.displayName, $0.detail ?? "") < ($1.displayName, $1.detail ?? "") }
    }

    public func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] {
        let instances = try await listAll(Instance.self, identity: identity, tenantId: tenant.tenantId,
                                          url: try url("\(Self.base)/assignmentScheduleInstances/filterByCurrentUser(on='principal')?$expand=group($select=id,displayName)"))
        let requests = try await listAll(ScheduleRequest.self, identity: identity, tenantId: tenant.tenantId,
                                         url: try url("\(Self.base)/assignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter=status eq 'PendingApproval'"))
        var result: [RoleKey: ActiveAssignment] = [:]
        for i in instances where i.assignmentType?.caseInsensitiveCompare("activated") == .orderedSame {
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: .group(groupId: i.groupId, accessId: Self.access(i.accessId)))
            result[key] = ActiveAssignment(roleKey: key, assignmentId: i.id, startDateTime: i.startDateTime ?? .now,
                                           endDateTime: i.endDateTime, status: .active)
        }
        for r in requests where r.status == "PendingApproval" {
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: .group(groupId: r.groupId, accessId: Self.access(r.accessId)))
            guard result[key] == nil else { continue }
            result[key] = ActiveAssignment(roleKey: key, assignmentId: r.id,
                                           startDateTime: r.scheduleInfo?.startDateTime ?? r.createdDateTime ?? .now,
                                           endDateTime: nil, status: .pendingApproval)
        }
        return Array(result.values)
    }

    // MARK: Policy and writes (Task 4)

    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy { .manualDefault }
    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment { throw PIMError.notEligible }
    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws { throw PIMError.notEligible }
    public func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws { throw PIMError.notEligible }
}
```

Check `GraphTransport.graphBase` exists (it is used by the Entra provider's `url`); if the property has another name, use that one.

Update both `AppModel` constructor sites: `GroupProvider(http: http, tokens: tokens)` in `init` (line ~100) and `GroupProvider(http: http, tokens: composite)` in `applyClientId` (line ~161).

- [ ] **Step 5: Run tests and build the app**

Run: `swift test` — expected all pass. Then `xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build 2>&1 | grep -E "error:|BUILD"` — expected `BUILD SUCCEEDED`.

- [ ] **Step 6: Commit**

```bash
git add Sources/ElevateCore/Providers Sources/ElevateApp/App/AppModel.swift Tests/ElevateCoreTests
git commit -m "Add GroupProvider reads for PIM for Groups eligibilities and assignments"
```

---

### Task 4: GroupProvider policy, activate, deactivate, cancel

**Files:**
- Modify: `Sources/ElevateCore/Providers/GroupProvider.swift`
- Create: `Tests/ElevateCoreTests/Fixtures/group-policy.json`, `group-activate-response.json`
- Test: `Tests/ElevateCoreTests/GroupProviderTests.swift`

**Interfaces:**
- Consumes: `AccessTokenClaims.objectId(_:)` (exists), `ISO8601Duration`, `GraphJSON.encoderDateString`.
- Produces: full `PIMProvider` behaviour for kind `.group`.

- [ ] **Step 1: Fixtures**

`group-policy.json`:

```json
{
  "value": [
    {
      "id": "Group_grp-ops_member_p1",
      "scopeId": "grp-ops", "scopeType": "Group", "roleDefinitionId": "member",
      "policy": {
        "id": "Group_grp-ops_p1",
        "rules": [
          { "@odata.type": "#microsoft.graph.unifiedRoleManagementPolicyExpirationRule", "id": "Expiration_EndUser_Assignment", "isExpirationRequired": true, "maximumDuration": "PT8H" },
          { "@odata.type": "#microsoft.graph.unifiedRoleManagementPolicyEnablementRule", "id": "Enablement_EndUser_Assignment", "enabledRules": ["Justification", "Ticketing"] },
          { "@odata.type": "#microsoft.graph.unifiedRoleManagementPolicyApprovalRule", "id": "Approval_EndUser_Assignment", "setting": { "isApprovalRequired": false } },
          { "@odata.type": "#microsoft.graph.unifiedRoleManagementPolicyAuthenticationContextRule", "id": "AuthenticationContext_EndUser_Assignment", "isEnabled": true, "claimValue": "c3" }
        ]
      }
    }
  ]
}
```

`group-activate-response.json`:

```json
{
  "id": "greq-new", "status": "Provisioned", "action": "selfActivate",
  "principalId": "caller-oid", "groupId": "grp-ops", "accessId": "member",
  "scheduleInfo": { "startDateTime": "2026-09-04T12:00:00Z", "expiration": { "type": "afterDuration", "duration": "PT2H", "endDateTime": "2026-09-04T14:00:00Z" } }
}
```

- [ ] **Step 2: Write the failing tests**

Append to `GroupProviderTests`:

```swift
    /// A JWT-shaped Graph token whose `oid` names the caller.
    private struct JWTTokenProvider: TokenProviding {
        let oid: String
        private var token: String {
            let payload = try! JSONSerialization.data(withJSONObject: ["oid": oid, "tid": "t1"])
            let b64 = payload.base64EncodedString().replacingOccurrences(of: "+", with: "-")
                .replacingOccurrences(of: "/", with: "_").trimmingCharacters(in: CharacterSet(charactersIn: "="))
            return "eyJhbGciOiJub25lIn0.\(b64).sig"
        }
        func signIn(method: SignInMethod) async throws -> Identity { throw PIMError.notEligible }
        func signOut(_ identity: Identity) async throws {}
        func identities() async throws -> [Identity] { [] }
        func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String { token }
        func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String { token }
    }

    var opsMember: EligibleRole {
        EligibleRole(key: RoleKey(identityId: "id1", tenantId: "t1", scope: .group(groupId: "grp-ops", accessId: .member)),
                     displayName: "Ops Admins", detail: "member", source: .discovered, policy: .manualDefault)
    }

    @Test func policyIsReadPerGroupAndAccess() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleManagementPolicyAssignments", body: Fixtures.data("group-policy"))
        let policy = try await p.policy(for: opsMember, identity: identity)
        #expect(policy.maximumDuration == .seconds(8 * 3600))
        #expect(policy.defaultDuration == .seconds(8 * 3600))
        #expect(policy.requiresJustification && policy.requiresTicket && !policy.requiresMFA && !policy.requiresApproval)
        #expect(policy.authenticationContext == "c3")
        let req = await http.requests.first!
        let u = req.url.absoluteString.removingPercentEncoding ?? req.url.absoluteString
        #expect(u.contains("scopeId eq 'grp-ops'") && u.contains("scopeType eq 'Group'") && u.contains("roleDefinitionId eq 'member'"))
        #expect(u.contains("$expand=policy($expand=rules)"))
    }

    @Test func activatePostsSelfActivateAsTheCaller() async throws {
        let http = StubHTTPClient()
        let p = GroupProvider(http: http, tokens: JWTTokenProvider(oid: "caller-oid"))
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible-page2"))
        await http.on("POST", "assignmentScheduleRequests", status: 201, body: Fixtures.data("group-activate-response"))
        let a = try await p.activate(ActivationRequest(roleKey: opsMember.key, duration: .seconds(7200), justification: "INC-7", ticket: TicketInfo(number: "42", system: "Jira")), identity: identity)
        #expect(a.status == .active)
        #expect(a.assignmentId == "greq-new")
        #expect(a.endDateTime == GraphJSON.parseDate("2026-09-04T14:00:00Z"))
        #expect(a.roleKey == opsMember.key)
        let post = await http.requests(matching: "assignmentScheduleRequests").first { $0.method == "POST" }!
        #expect(post.url.absoluteString.hasSuffix("/identityGovernance/privilegedAccess/group/assignmentScheduleRequests"))
        let body = try JSONSerialization.jsonObject(with: post.body!) as! [String: Any]
        #expect(body["action"] as? String == "selfActivate")
        #expect(body["accessId"] as? String == "member")
        #expect(body["groupId"] as? String == "grp-ops")
        #expect(body["principalId"] as? String == "caller-oid")
        #expect(body["justification"] as? String == "INC-7")
        #expect((body["ticketInfo"] as? [String: Any])?["ticketNumber"] as? String == "42")
        let exp = (body["scheduleInfo"] as! [String: Any])["expiration"] as! [String: Any]
        #expect(exp["type"] as? String == "afterDuration" && exp["duration"] as? String == "PT2H")
    }

    @Test func opaqueTokenFallsBackToEligibilityPrincipal() async throws {
        let (p, http, _) = makeProvider()   // FakeTokenProvider returns "token-t1", not a JWT
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("group-eligible-page2"))
        await http.on("POST", "assignmentScheduleRequests", status: 201, body: Fixtures.data("group-activate-response"))
        _ = try await p.activate(ActivationRequest(roleKey: opsMember.key, duration: .seconds(3600), justification: "x"), identity: identity)
        let post = await http.requests(matching: "assignmentScheduleRequests").first { $0.method == "POST" }!
        let body = try JSONSerialization.jsonObject(with: post.body!) as! [String: Any]
        #expect(body["principalId"] as? String == "user-obj-1")
    }

    @Test func pendingApprovalResponseIsReported() async throws {
        let http = StubHTTPClient()
        let p = GroupProvider(http: http, tokens: JWTTokenProvider(oid: "caller-oid"))
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible-page2"))
        await http.on("POST", "assignmentScheduleRequests", status: 201,
                      body: Data(#"{"id":"greq-p","status":"PendingApproval","groupId":"grp-ops","accessId":"member","scheduleInfo":{"startDateTime":"2026-09-04T12:00:00Z"}}"#.utf8))
        let a = try await p.activate(ActivationRequest(roleKey: opsMember.key, duration: .seconds(3600), justification: "x"), identity: identity)
        #expect(a.status == .pendingApproval)
        #expect(a.endDateTime == nil)
    }

    @Test func deactivatePostsSelfDeactivateWithoutSchedule() async throws {
        let http = StubHTTPClient()
        let p = GroupProvider(http: http, tokens: JWTTokenProvider(oid: "caller-oid"))
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible-page2"))
        await http.on("POST", "assignmentScheduleRequests", status: 201, body: Fixtures.data("group-activate-response"))
        let a = ActiveAssignment(roleKey: opsMember.key, assignmentId: "ginst-1", startDateTime: .now, endDateTime: nil, status: .active)
        try await p.deactivate(a, identity: identity)
        let post = await http.requests(matching: "assignmentScheduleRequests").first { $0.method == "POST" }!
        let body = try JSONSerialization.jsonObject(with: post.body!) as! [String: Any]
        #expect(body["action"] as? String == "selfDeactivate")
        #expect(body["principalId"] as? String == "caller-oid")
        #expect(body["groupId"] as? String == "grp-ops" && body["accessId"] as? String == "member")
        #expect(body["scheduleInfo"] == nil)
    }

    @Test func cancelPostsToTheRequestCancelAction() async throws {
        let (p, http, _) = makeProvider()
        await http.on("POST", "/cancel", status: 204)
        let a = ActiveAssignment(roleKey: opsMember.key, assignmentId: "greq-9", startDateTime: .now, endDateTime: nil, status: .pendingApproval)
        try await p.cancelPendingRequest(a, identity: identity)
        let post = await http.requests.first!
        #expect(post.method == "POST")
        #expect(post.url.absoluteString.hasSuffix("/identityGovernance/privilegedAccess/group/assignmentScheduleRequests/greq-9/cancel"))
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `swift test --filter GroupProviderTests`
Expected: the new tests fail (`notEligible` thrown, policy defaults).

- [ ] **Step 4: Implement**

Replace the placeholder section of `GroupProvider.swift` with:

```swift
    // MARK: Policy

    struct PolicyRule: Decodable {
        let id: String
        let maximumDuration: String?
        let enabledRules: [String]?
        let setting: ApprovalSetting?
        let isEnabled: Bool?
        let claimValue: String?
        struct ApprovalSetting: Decodable { let isApprovalRequired: Bool? }
    }
    struct Policy: Decodable { let id: String; let rules: [PolicyRule]? }
    struct PolicyAssignment: Decodable { let id: String; let roleDefinitionId: String?; let policy: Policy? }

    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy {
        guard case .group(let groupId, let access) = role.key.scope else { throw PIMError.notEligible }
        let filter = "scopeId eq '\(groupId)' and scopeType eq 'Group' and roleDefinitionId eq '\(access == .owner ? "owner" : "member")'"
        let encoded = filter.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? filter
        let r = try await transport.get(identity: identity, tenantId: role.key.tenantId,
                                        url: try url("/policies/roleManagementPolicyAssignments?$filter=\(encoded)&$expand=policy($expand=rules)"),
                                        scopes: scopes)
        let assignments = try GraphJSON.decoder.decode(Page<PolicyAssignment>.self, from: r.body).value
        guard let rules = assignments.first?.policy?.rules else { return .manualDefault }
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
            case "AuthenticationContext_EndUser_Assignment":
                if rule.isEnabled == true, let claim = rule.claimValue, !claim.isEmpty { policy.authenticationContext = claim }
            default: break
            }
        }
        return policy
    }

    // MARK: Activate / deactivate

    /// The eligibility's own principal id (a group when inherited), used only when the token hides the caller's oid.
    func eligibilityPrincipalId(groupId: String, access: GroupAccess, identity: Identity, tenantId: String) async throws -> String? {
        let items = try await listAll(Instance.self, identity: identity, tenantId: tenantId,
                                      url: try url("\(Self.base)/eligibilityScheduleInstances/filterByCurrentUser(on='principal')?$expand=group($select=id,displayName)"))
        return items.first { $0.groupId == groupId && Self.access($0.accessId) == access }?.principalId
    }

    /// Always the caller: an eligibility inherited through another group names that group, which Graph refuses.
    func requestPrincipalId(groupId: String, access: GroupAccess, identity: Identity, tenantId: String) async throws -> String {
        if let token = try? await transport.tokens.accessToken(identity: identity, tenantId: tenantId, scopes: scopes),
           let oid = AccessTokenClaims.objectId(token) { return oid }
        guard let fallback = try await eligibilityPrincipalId(groupId: groupId, access: access, identity: identity, tenantId: tenantId) else {
            throw PIMError.notEligible
        }
        return fallback
    }

    static func status(_ raw: String) -> ActiveAssignment.Status {
        switch raw {
        case "PendingApproval", "PendingAdminDecision": .pendingApproval
        case "PendingProvisioning", "PendingScheduleCreation", "ScheduleCreated": .pendingProvisioning
        case "Denied", "Failed", "Canceled", "Revoked": .failed(raw)
        default: .active
        }
    }

    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        guard case .group(let groupId, let access) = request.roleKey.scope else { throw PIMError.notEligible }
        let tenantId = request.roleKey.tenantId
        let principal = try await requestPrincipalId(groupId: groupId, access: access, identity: identity, tenantId: tenantId)
        var body: [String: Any] = [
            "action": "selfActivate",
            "principalId": principal,
            "groupId": groupId,
            "accessId": access == .owner ? "owner" : "member",
            "justification": request.justification,
            "scheduleInfo": [
                "startDateTime": GraphJSON.encoderDateString(.now),
                "expiration": ["type": "afterDuration", "duration": ISO8601Duration.format(request.duration)],
            ],
        ]
        if let t = request.ticket { body["ticketInfo"] = ["ticketNumber": t.number, "ticketSystem": t.system] }
        let r = try await transport.post(identity: identity, tenantId: tenantId,
                                         url: try url("\(Self.base)/assignmentScheduleRequests"),
                                         scopes: scopes, body: try JSONSerialization.data(withJSONObject: body))
        let created = try GraphJSON.decoder.decode(ScheduleRequest.self, from: r.body)
        let start = created.scheduleInfo?.startDateTime ?? .now
        let end = created.scheduleInfo?.expiration?.endDateTime
            ?? created.scheduleInfo?.expiration?.duration.flatMap(ISO8601Duration.parse).map { start.addingTimeInterval(TimeInterval($0.components.seconds)) }
            ?? start.addingTimeInterval(TimeInterval(request.duration.components.seconds))
        let status = Self.status(created.status)
        return ActiveAssignment(roleKey: request.roleKey, assignmentId: created.id, startDateTime: start,
                                endDateTime: status == .active ? end : nil, status: status)
    }

    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard case .group(let groupId, let access) = assignment.roleKey.scope else { throw PIMError.notEligible }
        let tenantId = assignment.roleKey.tenantId
        let principal = try await requestPrincipalId(groupId: groupId, access: access, identity: identity, tenantId: tenantId)
        let body: [String: Any] = [
            "action": "selfDeactivate",
            "principalId": principal,
            "groupId": groupId,
            "accessId": access == .owner ? "owner" : "member",
        ]
        _ = try await transport.post(identity: identity, tenantId: tenantId,
                                     url: try url("\(Self.base)/assignmentScheduleRequests"),
                                     scopes: scopes, body: try JSONSerialization.data(withJSONObject: body))
    }

    /// Withdraws a request still awaiting approval. Graph answers 204 with no body.
    public func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard let requestId = assignment.assignmentId else { throw PIMError.notEligible }
        _ = try await transport.post(identity: identity, tenantId: assignment.roleKey.tenantId,
                                     url: try url("\(Self.base)/assignmentScheduleRequests/\(requestId)/cancel"),
                                     scopes: scopes, body: Data())
    }
```

`transport.tokens` is accessible within the module (the Azure provider already uses it). `ScheduleRequest` must decode the activate response: its `groupId`/`accessId` are present in the fixture; keep them non-optional.

- [ ] **Step 5: Run tests**

Run: `swift test`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add Sources/ElevateCore/Providers/GroupProvider.swift Tests/ElevateCoreTests
git commit -m "Implement GroupProvider policy, activation, deactivation and cancel"
```

---

### Task 5: AppModel — group refresh, tab filter, deferred role refresh, consent URL

**Files:**
- Modify: `Sources/ElevateApp/App/AppSettings.swift`
- Modify: `Sources/ElevateApp/App/AppModel.swift`

**Interfaces:**
- Produces: `enum PanelTab: String, CaseIterable { case roles, groups }` (App target, `Sources/ElevateApp/App/PanelTab.swift`); `AppModel.panelTab: PanelTab` (settable, persisted; setting it clears `selection`); `AppModel.roles(for: TenantKey, tab: PanelTab) -> [EligibleRole]`; `AppModel.refresh(_ key: TenantKey, kinds: Set<RoleScopeKind>? = nil)`; `AppModel.groupsUnavailableReason(for: TenantKey) -> String?` (tenant field or the first-party note).
- Consumes: `GroupScopes.all`, `TenantContext.groupsUnavailableReason`.

- [ ] **Step 1: PanelTab and settings**

Create `Sources/ElevateApp/App/PanelTab.swift`:

```swift
import Foundation

/// Which list the panel shows. Persisted so the panel reopens where it was left.
enum PanelTab: String, CaseIterable, Sendable {
    case roles, groups
    var title: String { self == .roles ? "Roles" : "Groups" }
}
```

In `AppSettings`, add `static let panelTabKey = "panelTab"`, a stored property

```swift
    var panelTab: PanelTab {
        didSet { defaults.set(panelTab.rawValue, forKey: Self.panelTabKey) }
    }
```

and initialise it at the end of `init` with `panelTab = PanelTab(rawValue: defaults.string(forKey: Self.panelTabKey) ?? "") ?? .roles`.

- [ ] **Step 2: AppModel tab state and filters**

In `AppModel` (near `selectMode`):

```swift
    /// The list the panel shows; switching drops the bulk selection since it spans one tab only.
    var panelTab: PanelTab {
        get { settings.panelTab }
        set { settings.panelTab = newValue; selection.removeAll() }
    }

    static func kinds(for tab: PanelTab) -> Set<RoleScopeKind> {
        tab == .groups ? [.group] : [.entraDirectory, .azureResource]
    }

    func roles(for tenantKey: TenantKey, tab: PanelTab) -> [EligibleRole] {
        let kinds = Self.kinds(for: tab)
        return roles(for: tenantKey).filter { kinds.contains($0.key.scope.kind) }
    }

    /// Why the Groups tab is empty by construction for this tenant, or nil when groups are read normally.
    func groupsUnavailableReason(for key: TenantKey) -> String? {
        guard let identity = identity(key.identityId) else { return nil }
        if !identity.signInMethod.isPreauthorisedForEntraActivation {
            return "The \(identity.signInMethod.displayName) supports Azure resource roles only; PIM for Groups needs your own or a custom app registration."
        }
        return tenant(key)?.groupsUnavailableReason
    }
```

`AppSettings` is `@MainActor @Observable`, so reading `settings.panelTab` inside a view re-renders on change.

- [ ] **Step 3: Refresh with kinds and group consent handling**

Change the signature to `func refresh(_ key: TenantKey, kinds requestedKinds: Set<RoleScopeKind>? = nil) async`. Replace the `kinds` computation:

```swift
        var kinds: [RoleScopeKind] = tenant.azureUnavailableReason == nil ? [.entraDirectory, .azureResource, .group] : [.entraDirectory, .group]
        if !identity.signInMethod.isPreauthorisedForEntraActivation { kinds.removeAll { $0 == .entraDirectory || $0 == .group } }
        if tenant.groupsUnavailableReason != nil { kinds.removeAll { $0 == .group } }
        if let requestedKinds { kinds.removeAll { !requestedKinds.contains($0) } }
```

In the provider loop, add `let isGroup = kind == .group` and handle a group consent refusal on the eligible read:

```swift
                } catch PIMError.consentRequired where isGroup {
                    guard generation == configGeneration, self.tenant(key) != nil else { return }
                    discoveredByKind[kind] = []
                    tenant.groupsUnavailableReason = "PIM for Groups is not permitted in this tenant until an admin consents to the group permissions."
                    state.upsertTenant(tenant)
                    persist()
                    continue   // skip the active read for this provider
                }
```

Place it before the generic `catch` and before the `signInDeclined` catch so ordering matches the Entra `consentRequired` case. `continue` inside the `for provider in providers` loop skips the active-assignment read. Also handle `PIMError.forbidden` for groups the same way (a custom-app first-party style 403 comes through as `.forbidden` when `signInMethod != .ownApp`): `catch PIMError.consentRequired where isGroup` becomes `catch let e as PIMError where isGroup && (e == .consentRequired || { if case .forbidden = e { return true } else { return false } }())`. Keep it readable: define `static func isGroupConsentFailure(_ e: PIMError) -> Bool` returning true for `.consentRequired` and `.forbidden`.

Existing "replace only the kinds we successfully re-read" logic already keys on `kindsWithActive`, so a skipped group provider leaves prior group rows and assignments intact; the eligible list for a consent-refused tenant is emptied via `discoveredByKind[kind] = []`.

`retryDiscovery` clears `t.groupsUnavailableReason = nil` alongside the other resets.

- [ ] **Step 4: Deferred role refresh after a group change**

Add:

```swift
    /// Membership changes carry roles with them; give the directory a moment, then re-read roles only.
    private func refreshRolesAfterGroupChange(_ tenantKeys: Set<TenantKey>) {
        guard !tenantKeys.isEmpty else { return }
        let generation = configGeneration
        Task {
            try? await Task.sleep(for: .seconds(5))
            guard generation == self.configGeneration else { return }
            for key in tenantKeys where self.tenant(key) != nil {
                await self.refresh(key, kinds: [.entraDirectory, .azureResource])
            }
        }
    }
```

In `activate(_:)`, after `persist()` and before `selectMode = false`:

```swift
        let changedGroupTenants = Set(outcomes.compactMap { o -> TenantKey? in
            guard o.roleKey.scope.kind == .group, case .activated = o.result else { return nil }
            return o.roleKey.tenantKey
        })
        refreshRolesAfterGroupChange(changedGroupTenants)
```

In `deactivate(_:)`, after `active[key] = nil`: `if key.scope.kind == .group { refreshRolesAfterGroupChange([key.tenantKey]) }`.

- [ ] **Step 5: Admin consent URL includes the group scopes**

In `adminConsentURL`, change the `scope` query item to `(GraphScopes.all + GroupScopes.all).joined(separator: " ")`.

- [ ] **Step 6: Build**

Run: `xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build 2>&1 | grep -E "error:|BUILD"`
Expected: `BUILD SUCCEEDED`. Then `swift test` still passes (Core untouched).

- [ ] **Step 7: Commit**

```bash
git add Sources/ElevateApp/App
git commit -m "Run the group provider per tenant, add the panel tab filter and refresh roles after group changes"
```

---

### Task 6: Views — segmented control, tab-aware lists, via-group caption

**Files:**
- Modify: `Sources/ElevateApp/Views/PanelView.swift`
- Modify: `Sources/ElevateApp/Views/TenantSection.swift`
- Modify: `Sources/ElevateApp/Views/RoleRow.swift`
- Modify: `README.md`

**Interfaces:**
- Consumes: `model.panelTab`, `model.roles(for:tab:)`, `model.groupsUnavailableReason(for:)`, `EligibleRole.viaGroup`, `TenantMenuItems`, `StatusPill`.

- [ ] **Step 1: Segmented control**

In `PanelView.body`, directly after `header` and its `Divider()`:

```swift
            Picker("", selection: Binding(get: { model.panelTab }, set: { model.panelTab = $0 })) {
                ForEach(PanelTab.allCases, id: \.self) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            Divider()
```

Change the "Activate N" button label to say "role" or "group" by tab: `Text("Activate \(model.selection.count) \(model.panelTab == .groups ? "group" : "role")\(model.selection.count == 1 ? "" : "s")")`.

- [ ] **Step 2: Tab-aware tenant rows**

In `TenantSection.swift`, `TenantRoles`:

```swift
    private var roles: [EligibleRole] { model.roles(for: tenant.id, tab: model.panelTab) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if model.panelTab == .groups, let reason = model.groupsUnavailableReason(for: tenant.id) {
                Label(reason, systemImage: "info.circle").font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.leading, PanelMetrics.roleInset).padding(.trailing, PanelMetrics.trailingInset).padding(.vertical, 6)
            } else if roles.isEmpty {
                Text(emptyText).font(.caption).foregroundStyle(.secondary)
                    .padding(.leading, PanelMetrics.roleInset).padding(.vertical, 6)
            }
            ForEach(roles) { role in RoleRow(role: role) }
        }
    }

    private var emptyText: String {
        switch (model.panelTab, tenant.discoveryMode) {
        case (.groups, _): "No eligible groups."
        case (.roles, .manualRoles): "No roles configured."
        case (.roles, .automatic): "No eligible roles."
        }
    }
```

`TenantHeader.activeCount` and `IdentityHeader.activeCount` keep counting all kinds (they use `model.roles(for:)` without a tab), which matches the spec's menu bar count. In `TenantMenuItems`, show "Open admin consent link…" when `tenant.discoveryMode == .manualRoles || tenant.groupsUnavailableReason != nil`.

Add a pill: in `TenantPills`, after the "Azure off" entry:

```swift
        if let reason = tenant.groupsUnavailableReason {
            StatusPill(text: "Groups off", help: reason)
        }
```

- [ ] **Step 3: Via-group caption**

In `RoleRow`, inside the name `VStack`, after the `detail` line:

```swift
                if let via = role.viaGroup {
                    Text(via == "group" ? "via group" : "via \(via)").font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                        .help("This eligibility is granted through a group; activating it activates the role for you")
                }
```

- [ ] **Step 4: README**

In `README.md` prerequisites, extend the delegated permission list with the three group permissions, and add a short "Groups" paragraph under the panel description: the Groups tab lists PIM for Groups memberships and ownerships; activation, countdown and deactivate work as for roles; roles carried by a group are re-read a few seconds after activation; first-party accounts do not get groups.

- [ ] **Step 5: Build, launch, verify**

Run the build command; expected `BUILD SUCCEEDED`. Relaunch with `pkill -x Elevate; open build/Build/Products/Debug/Elevate.app`. Verify by eye: the segmented control appears; Groups tab shows the user's group eligibility or the unavailable note; Roles tab unchanged.

- [ ] **Step 6: Commit**

```bash
git add Sources/ElevateApp/Views README.md
git commit -m "Add the Groups tab, group empty states and via-group captions"
```

---

### Task 8: App registration guide and Azure CLI manifest

**Files:**
- Create: `docs/entra-app-registration.md`
- Create: `docs/entra-app/required-resource-access.json`
- Create: `docs/entra-app/create-app-registration.sh`
- Modify: `README.md` (root) and `macos/README.md` prerequisites: link to the guide instead of repeating the steps.

**Interfaces:** none (documentation and a script). Scope ids below were resolved on 2026-09-05 with `az ad sp show --id 00000003-0000-0000-c000-000000000000 --query "oauth2PermissionScopes[?value=='…'].id"` and are stable, globally identical ids.

- [ ] **Step 1: The manifest**

`docs/entra-app/required-resource-access.json` (the exact shape `az ad app create --required-resource-accesses @file` expects: `type: "Scope"` = delegated):

```json
[
  {
    "resourceAppId": "00000003-0000-0000-c000-000000000000",
    "resourceAccess": [
      { "id": "e1fe6dd8-ba31-4d61-89e7-88639da4683d", "type": "Scope" },
      { "id": "eb0788c2-6d4e-4658-8c9e-c0fb8053f03d", "type": "Scope" },
      { "id": "8c026be3-8e26-4774-9372-8d5d6f21daff", "type": "Scope" },
      { "id": "3de2cdbe-0ff5-47d5-bdee-7f45b4749ead", "type": "Scope" },
      { "id": "8f44f93d-ecef-46ae-a9bf-338508d44d6b", "type": "Scope" },
      { "id": "06dbc45d-6708-4ef0-a797-f797ee68bf4b", "type": "Scope" },
      { "id": "7e26fdff-9cb1-4e56-bede-211fe0e420e8", "type": "Scope" }
    ]
  },
  {
    "resourceAppId": "797f4846-ba00-4fd7-ba43-dac1f8f63013",
    "resourceAccess": [
      { "id": "41094075-9dad-400e-a0bd-54e686782033", "type": "Scope" }
    ]
  }
]
```

Id → permission, for the guide's table: `e1fe6dd8…` User.Read; `eb0788c2…` RoleEligibilitySchedule.Read.Directory; `8c026be3…` RoleAssignmentSchedule.ReadWrite.Directory; `3de2cdbe…` RoleManagementPolicy.Read.Directory; `8f44f93d…` PrivilegedEligibilitySchedule.Read.AzureADGroup; `06dbc45d…` PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup; `7e26fdff…` RoleManagementPolicy.Read.AzureADGroup; ARM `41094075…` user_impersonation (Azure Service Management, app id `797f4846-…`).

- [ ] **Step 2: The script**

`docs/entra-app/create-app-registration.sh` (make executable):

```bash
#!/usr/bin/env bash
# Creates the Elevate app registration with Azure CLI and prints the client id.
# Usage: ./create-app-registration.sh [display-name] [bundle-id]
#   display-name  defaults to "Elevate"
#   bundle-id     defaults to no.reothor.elevate (must match the app's bundle id; only change it for your own build)
# Requires: az login as a user allowed to create app registrations (Application Developer or above).
set -euo pipefail
NAME="${1:-Elevate}"
BUNDLE="${2:-no.reothor.elevate}"
HERE="$(cd "$(dirname "$0")" && pwd)"

APP_ID=$(az ad app create \
  --display-name "$NAME" \
  --sign-in-audience AzureADMultipleOrgs \
  --public-client-redirect-uris "msauth.${BUNDLE}://auth" "http://localhost" \
  --web-redirect-uris "https://login.microsoftonline.com/common/oauth2/nativeclient" \
  --required-resource-accesses "@${HERE}/required-resource-access.json" \
  --query appId -o tsv)

# A service principal in the home tenant so admin consent can be recorded there.
az ad sp create --id "$APP_ID" >/dev/null 2>&1 || true

echo "Application (client) ID: $APP_ID"
echo
echo "Next: grant admin consent in each tenant that will use Elevate:"
echo "  az ad app permission admin-consent --id $APP_ID      (home tenant, needs Privileged Role Administrator or Global Administrator)"
echo "  or open: https://login.microsoftonline.com/{tenant-id}/adminconsent?client_id=$APP_ID"
echo "Then paste the client id into Elevate → Settings."
```

- [ ] **Step 3: The guide**

`docs/entra-app-registration.md` with these sections, written for someone who has never opened the Entra portal:

1. **What the registration is for**: one multi-tenant public-client app; Elevate signs in with it through MSAL on macOS (redirect `msauth.<bundle id>://auth`) and, on Windows or for the custom-app method, through the `http://localhost` loopback redirect. No secret is ever used.
2. **Option A, Azure CLI (recommended)**: prerequisites (`az` ≥ 2.60, `az login`, role Application Developer or higher), then `git clone`, `cd docs/entra-app`, `./create-app-registration.sh`, what it prints. Then the consent step: `az ad app permission admin-consent --id <client id>` in the home tenant, and for each customer tenant the admin consent URL `https://login.microsoftonline.com/<tenant id>/adminconsent?client_id=<client id>` opened by that tenant's admin (Elevate also offers this link per tenant from the tenant menu).
3. **Option B, portal, step by step**: Entra admin center → App registrations → New registration; name; supported account types "Accounts in any organizational directory (Any Microsoft Entra ID tenant – Multitenant)"; leave redirect blank; Register. Then Authentication → Add a platform → iOS/macOS → bundle id → Configure; Add a platform → Mobile and desktop applications → custom redirect `http://localhost`; Add a platform → Web → `https://login.microsoftonline.com/common/oauth2/nativeclient`. Then API permissions → Add a permission → Microsoft Graph → Delegated → the seven scopes from the table; → Azure Service Management → Delegated → user_impersonation; then "Grant admin consent for <tenant>". Note that "Allow public client flows" is not required (the iOS/macOS and mobile/desktop platforms already mark the redirect URIs as public-client) and to turn it on only if sign-in fails with AADSTS7000218.
4. **Permission table**: the seven Graph delegated permissions plus ARM user_impersonation, each with one line on what Elevate uses it for and whether admin consent is needed (all Graph ones yes; ARM user-consentable).
5. **Verify**: `az ad app show --id <client id> --query "{name:displayName,audience:signInAudience,public:publicClient.redirectUris}"` and what to expect; then Settings → paste client id → Add account.
6. **Troubleshooting**: AADSTS7000218 (redirect registered under Web instead of a public platform), AADSTS65001 / "Admin consent required" (consent missing in that tenant; use the link from the tenant menu), AADSTS50011 (redirect mismatch: bundle id differs from the build), "PIM for Groups is not permitted" (the three group scopes not consented).

Link the guide from the root `README.md` ("Setting up the Entra app registration → docs/entra-app-registration.md") and replace the detailed step list in `macos/README.md` prerequisites with a one-line pointer to the guide, keeping the redirect URI value visible there.

- [ ] **Step 4: Verify the script parses**

Run: `bash -n docs/entra-app/create-app-registration.sh && python3 -c "import json;json.load(open('docs/entra-app/required-resource-access.json'))"` — expected no output. Do not run the script against a real tenant as part of the task.

- [ ] **Step 5: Commit**

```bash
git add docs/entra-app-registration.md docs/entra-app README.md macos/README.md
git commit -m "Add the Entra app registration guide with an Azure CLI manifest and script"
```

---

### Task 9: Final review pass and push

- [ ] Run `swift test` (expect all suites pass) and the app build.
- [ ] Re-read the spec's success criteria list and tick each against the code: tab, captions, bulk within tab, menu bar count over all kinds, deferred refresh, manual groups (verify `ConfigureRolesView` still saves `.group` scopes and they render in the Groups tab), first-party empty state, via-group captions, registration guide linked from both READMEs.
- [ ] `git push origin main`.
