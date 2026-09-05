# Elevate Approvals Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show activation requests awaiting the signed-in user's approval in a pinned panel section, decide them with a justification sheet, and notify when new ones appear.

**Architecture:** Core gains an `ApprovalRequest` model, an `ApprovalProvider` protocol with Entra, Group and ARM implementations (reads via `filterByCurrentUser(on='approver')` / `asApprover()`, decisions via the approval step or stage PATCH), a `patch` verb on `GraphTransport`, and `ApprovalDiff`. `AppModel` reads approvals per tenant on refresh, tracks decisions in flight, and notifies newly seen requests; the panel gets an `ApprovalsSection`, a `DecisionView` window and a menu bar glyph.

**Tech Stack:** Swift 6.2, SwiftUI macOS 26, Swift Testing, Microsoft Graph v1.0 (+ beta for approvals), ARM 2020-10-01 / 2021-01-01-preview.

**Spec:** `docs/superpowers/specs/2026-09-05-elevate-approvals-design.md`

## Global Constraints

- Paths relative to `macos/`. Tests `swift test` (176 now). Build `xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build 2>&1 | grep -E "error:|BUILD"`. Relaunch `pkill -x Elevate; sleep 1; open build/Build/Products/Debug/Elevate.app`.
- `ElevateCore` imports only Foundation. Swift 6 strict concurrency; no `@unchecked Sendable` in `AppModel`. Never commit `Elevate.xcodeproj`. Swift Testing; `StubHTTPClient` matches the LAST registered route whose method and URL substring match.
- Graph approvals resources (`roleAssignmentApprovals`, `assignmentApprovals`) use base `https://graph.microsoft.com/beta`; request lists use v1.0 (`GraphTransport.graphBase`). ARM approvals use api-version `2021-01-01-preview`; ARM request list `2020-10-01`.
- Approval list failures are swallowed per tenant/kind (previous list kept). Decision failures surface. First-party accounts (`!isPreauthorisedForEntraActivation`) read ARM approvals only.
- Branch `approvals` from `main`; commit after every task with the given message.

## File structure

```
Sources/ElevateCore/Models/ApprovalRequest.swift            new model + Action/Kind
Sources/ElevateCore/Providers/ApprovalProvider.swift        protocol + ApprovalDiff
Sources/ElevateCore/Providers/EntraApprovalProvider.swift   new
Sources/ElevateCore/Providers/GroupApprovalProvider.swift   new
Sources/ElevateCore/Providers/AzureApprovalProvider.swift   new
Sources/ElevateCore/Providers/GraphTransport.swift          + patch, graphBetaBase, graphBetaURL
Sources/ElevateApp/App/{AppModel,AppSettings,PanelRoute,ElevateApp}.swift
Sources/ElevateApp/Views/{ApprovalsSection,DecisionView,RouteWindow,PanelView}.swift
Tests/ElevateCoreTests/{ApprovalProvidersTests,ApprovalDiffTests}.swift + Fixtures/{entra,group,arm}-approver-*.json
```

---

### Task 1: Core — model, protocol, diff, transport patch

**Files:** `Models/ApprovalRequest.swift`, `Providers/ApprovalProvider.swift`, `Providers/GraphTransport.swift`; test `Tests/ElevateCoreTests/ApprovalDiffTests.swift`.

**Interfaces (produces):**
```swift
public struct ApprovalRequest: Codable, Hashable, Sendable, Identifiable { /* spec §2 fields; memberwise public init with defaults for optionals */ }
public protocol ApprovalProvider: Sendable {
    var kind: RoleScopeKind { get }
    var scopes: [String] { get }
    func pendingApprovals(identity: Identity, tenant: TenantContext) async throws -> [ApprovalRequest]
    func decide(_ request: ApprovalRequest, approve: Bool, justification: String, identity: Identity) async throws
}
public enum ApprovalDiff { public static func newRequests(previousIds: Set<String>, current: [ApprovalRequest]) -> [ApprovalRequest] }
// GraphTransport
public static let graphBetaBase = URL(string: "https://graph.microsoft.com/beta")!
public func graphBetaURL(_ path: String) throws -> URL
public func patch(identity:tenantId:url:scopes:body:) async throws -> HTTPResponse
```

- [ ] Test: `ApprovalDiffTests` — new ids returned in input order, previously seen excluded, empty cases.
- [ ] Implement per spec §2 (`ApprovalRequest.Kind` mirrors `RoleScopeKind`; keep `kind: RoleScopeKind` directly instead of a duplicate enum — ruling: use `RoleScopeKind`). `graphBetaURL` mirrors `graphURL` with the beta base. `patch` mirrors `post` with method "PATCH".
- [ ] `swift test`; commit `git commit -m "Add ApprovalRequest, ApprovalProvider, ApprovalDiff and transport PATCH"`.

---

### Task 2: Entra and Group approval providers

**Files:** `Providers/EntraApprovalProvider.swift`, `Providers/GroupApprovalProvider.swift`; fixtures `entra-approver-requests.json`, `entra-approval-steps.json`, `group-approver-requests.json`, `group-approval-steps.json`; tests in `ApprovalProvidersTests.swift`.

Fixture shapes: Graph request with `id`, `status: "PendingApproval"`, `action: "selfActivate"`, `principalId`, `roleDefinitionId`/`directoryScopeId` (Entra) or `groupId`/`accessId` (Group), `justification`, `createdDateTime`, `scheduleInfo.expiration.duration: "PT4H"`, expanded `roleDefinition {displayName}` / `group {displayName}`, `principal { "@odata.type": "#microsoft.graph.user", displayName, userPrincipalName }`; a second request with `action: "selfExtend"` and no `principal` expansion (fallback to `principalId`). Steps fixture: `{ "value": [ { "id": "step-1", "status": "InProgress", "assignedToMe": true, "reviewResult": "NotReviewed" } ] }`.

Tests per provider: (a) list → two `ApprovalRequest`s with correct `action`, `targetName`, `requesterName` (display name; fallback principal id), `requestedDuration == .seconds(4*3600)`, `decisionRef == request id`, URL contains `filterByCurrentUser(on='approver')` and `status eq 'PendingApproval'`; (b) decide → GET steps on the **beta** base (`/beta/roleManagement/directory/roleAssignmentApprovals/<id>/steps` / `/beta/identityGovernance/privilegedAccess/group/assignmentApprovals/<id>/steps`), then PATCH `…/steps/step-1` with body `{"reviewResult":"Approve","justification":"ok"}` (and "Deny"); (c) 403 on list throws `consentRequired` (the caller swallows).

Implementation: decode with `GraphJSON.decoder`; step selection = first step with `status == "InProgress"`, else first with `assignedToMe == true`, else first; throw `PIMError.unexpected(status: 0, body: "No approval step to decide")` when none. Group `scopeCaption` = "member"/"owner". Commit `git commit -m "Entra and Group approval providers"`.

---

### Task 3: ARM approval provider

**Files:** `Providers/AzureApprovalProvider.swift`; fixtures `arm-approver-requests.json` (one `PendingApproval` `SelfActivate` request with `properties.approvalId`, `expandedProperties.principal.displayName`, `roleDefinition.displayName`, `scope.displayName/type`, `scheduleInfo.expiration.duration`, plus one `Provisioned` request to be filtered out and one `SelfExtend`), `arm-approval.json` (`{ "properties": { "stages": [ { "id": ".../stages/stage-1", "name": "stage-1", "properties": { "reviewResult": "NotReviewed", "status": "InProgress" } } ] } }`); tests.

Tests: list URL has `$filter=asApprover()` and `api-version=2020-10-01`, keeps only `PendingApproval`, `decisionRef == approvalId`, `scopeCaption` from `AzureResourceProvider.caption` (reuse the static, make it `static func caption` internal — it already is); decide → GET `roleAssignmentApprovals/<approvalId>?api-version=2021-01-01-preview`, PATCH `…/stages/stage-1?api-version=2021-01-01-preview` with `{"reviewResult":"Approve","justification":"ok"}`; if the PATCH answers 400 retry once with `{"properties":{...}}` and succeed (test both: first 400 then 200). Commit `git commit -m "ARM approval provider"`.

---

### Task 4: AppModel — approvals state, refresh, decide, notifications

**Files:** `App/AppModel.swift`, `App/AppSettings.swift`, `App/PanelRoute.swift`.

**Interfaces (produces):** `approvalsOrdered: [ApprovalRequest]`, `pendingApprovalCount: Int`, `decisionInFlight: Set<String>`, `approvalErrors: [String: String]`, `collapsedApprovals` (+ `toggleApprovals()`), `lastApprovalJustification` (settings), `func decide(_ request: ApprovalRequest, approve: Bool, justification: String) async -> Bool`, `PanelRoute.decide(requestId: String, approve: Bool)`.

- Providers: `approvalProviders: [RoleScopeKind: any ApprovalProvider]` built next to the coordinator at both construction sites (`init` and `applyClientId`), with `EntraApprovalProvider(http:tokens:)`, `GroupApprovalProvider(http:tokens:)`, `AzureApprovalProvider(http:tokens:)`.
- Storage: `approvals: [TenantKey: [RoleScopeKind: [ApprovalRequest]]]` (session); `seenApprovalIds: Set<String>` persisted in `AppSettings` as JSON (`seenApprovalIds`), pruned after each refresh to ids still pending anywhere.
- In `refresh(_:kinds:)`, after the provider loop and before the tail: for each kind in `kinds` with an approval provider, `try? await acquire(provider.scopes) { try await provider.pendingApprovals(identity:tenant:) }`; on success replace `approvals[key]![kind]`; on nil keep. Then compute `ApprovalDiff.newRequests(previousIds: seenApprovalIds, current: all pending)` and for each post `notifier.notify(title: "Approval requested", body: "\(r.targetName) for \(r.requesterName), \(tenantName)")`, then add to seen and persist settings. Guard with `generation == configGeneration`.
- `decide`: guard not in flight; insert; call the provider found by `request.kind`; on success remove the request from `approvals`, `approvalErrors[id] = nil`, `settings.lastApprovalJustification = justification`, and `Task { await refresh(request.tenantKey, kinds: [request.kind]) }`; on failure `approvalErrors[id] = userMessage`; remove from in flight; return success.
- `approvalsOrdered` = all requests sorted by `createdAt ?? .distantPast` then tenant name; filtered by `searchQuery` on target, requester, tenant when filtering.
- `applyClientId` and `forgetIdentity`/`removeTenant` drop the tenant's approvals.
- Build; commit `git commit -m "Read, diff, notify and decide approvals in AppModel"`.

---

### Task 5: Views — ApprovalsSection, DecisionView, menu bar glyph

**Files:** `Views/ApprovalsSection.swift` (new), `Views/DecisionView.swift` (new), `Views/RouteWindow.swift`, `Views/PanelView.swift`, `App/ElevateApp.swift`.

- `ApprovalsSection`: pinned `Section` placed before `ActiveSection()` in `PanelView`, present when `approvalsOrdered` is non-empty; header "Approvals" + count in orange, chevron toggling `collapsedApprovals` (same chrome as `ActiveHeader`); rows: `person.badge.clock` glyph, "<requesterName>" bold, "<targetName>" (+ scopeCaption), caption "<tenant> · <duration HH:MM> · <relative created time via RelativeDateTimeFormatter>", `.help(justification ?? "No reason given")`; trailing: spinner when in flight; else if `action == .activate` buttons "Approve" (`.borderedProminent`, small) and "Deny" (small) opening `PanelRoute.decide(requestId:approve:)`; else `Text("Decide in the portal")` secondary; error text in red below when `approvalErrors[id]` is set.
- `DecisionView(requestId:approve:)`: finds the request in `model.approvalsOrdered`; shows requester, target, tenant, duration, reason; `TextField("Justification", axis: .vertical)` prefilled with `model.settings.lastApprovalJustification`; buttons Cancel and "Approve"/"Deny" (Deny disabled while the field is empty; Approve allowed empty); on submit `await model.decide(...)`, dismiss on success, show the error inline on failure. Width 420.
- `MenuBarLabel`: add `if model.pendingApprovalCount > 0 { Image(systemName: "person.badge.clock").font(.caption2) }` after the clock; extend `description(of:)` text via an extra parameter or append in the view.
- Build, relaunch, commit `git commit -m "Approvals section, decision sheet and menu bar glyph"`.

---

### Task 6: Docs and checks

- `swift test`, build, relaunch. `macos/README.md`: "**Approvals.**" paragraph (what is listed, how to decide, extend/renew limitation, first-party accounts see ARM approvals only). Commit "Document approvals".
