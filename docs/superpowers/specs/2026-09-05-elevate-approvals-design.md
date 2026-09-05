# Elevate — approvals

Date: 2026-09-05. Extends the panel and notification specs. Approved in chat:
pinned Approvals section, decision sheet with justification, menu bar glyph
and notification, read on the normal refresh cycle, portal note for extend and
renew requests.

## 1. Goal

Let an approver handle activation requests from the panel.

Success criteria:

- A pinned **Approvals** section above Active now (all tabs) lists every
  request awaiting the signed-in user's decision across accounts and tenants:
  requester name, role or group name, tenant, requested duration, request
  time, the requester's reason as a tooltip, Approve and Deny buttons.
  Absent when nothing is pending.
- Approve and Deny open a sheet with the details and a justification field
  prefilled with the last one used; Deny requires text. The row shows a
  spinner while the decision is sent, disappears on success, and shows the
  server message on failure.
- Menu bar: a `person.badge.clock` glyph after the count while approvals are
  pending. A notification "Approval requested: <role> for <requester>,
  <tenant>" fires once per newly seen request.
- Requests of type extend or renew are listed with "Decide in the portal"
  instead of buttons (the APIs do not support approving them).
- Read on every refresh and on panel open. A tenant that refuses the approver
  read shows nothing (no pill, no error). First-party accounts read only ARM
  approvals.

## 2. Core

Model (`Models/ApprovalRequest.swift`):

```swift
public struct ApprovalRequest: Codable, Hashable, Sendable, Identifiable {
    public enum Kind: String, Codable, Sendable { case entraDirectory, azureResource, group }
    public enum Action: String, Codable, Sendable { case activate, extend, renew, other }
    public var id: String            // request id (Graph) or request name (ARM)
    public var tenantKey: TenantKey
    public var kind: Kind
    public var action: Action
    public var targetName: String    // role or group display name (fallback: id)
    public var scopeCaption: String? // Azure scope display / group "member"/"owner"
    public var requesterName: String // display name or UPN; fallback principal id
    public var justification: String?
    public var requestedDuration: Duration?
    public var createdAt: Date?
    public var decisionRef: String?  // Graph: approval id (== request id); ARM: approvalId
}
```

`ApprovalProvider` protocol (`Providers/ApprovalProvider.swift`):

```swift
public protocol ApprovalProvider: Sendable {
    var kind: RoleScopeKind { get }
    var scopes: [String] { get }
    func pendingApprovals(identity: Identity, tenant: TenantContext) async throws -> [ApprovalRequest]
    func decide(_ request: ApprovalRequest, approve: Bool, justification: String, identity: Identity) async throws
}
```

Implementations, each a small struct sharing `GraphTransport`:

| Kind | List | Decide |
|---|---|---|
| Entra | `GET /roleManagement/directory/roleAssignmentScheduleRequests/filterByCurrentUser(on='approver')?$filter=status eq 'PendingApproval'&$expand=roleDefinition,principal` | `GET /roleManagement/directory/roleAssignmentApprovals/{id}/steps` → step with `status == "InProgress"` (or first), then `PATCH …/steps/{stepId}` `{reviewResult: "Approve"|"Deny", justification}` |
| Group | `GET /identityGovernance/privilegedAccess/group/assignmentScheduleRequests/filterByCurrentUser(on='approver')?$filter=status eq 'PendingApproval'&$expand=group,principal` | `GET /identityGovernance/privilegedAccess/group/assignmentApprovals/{id}/steps` then `PATCH …/steps/{stepId}` |
| ARM | `GET /providers/Microsoft.Authorization/roleAssignmentScheduleRequests?$filter=asApprover()&api-version=2020-10-01` keeping `properties.status == PendingApproval` | `GET /providers/Microsoft.Authorization/roleAssignmentApprovals/{approvalId}?api-version=2021-01-01-preview` → `properties.stages` with `reviewResult == NotReviewed` → `PATCH …/roleAssignmentApprovals/{approvalId}/stages/{stageName}` `{properties: {reviewResult, justification}}` — ARM stage PATCH body: send `{"reviewResult": "...", "justification": "..."}` at the top level as the docs show, with a fallback to the `properties`-wrapped form on a 400 |

Graph approval endpoints use the **beta** base (`https://graph.microsoft.com/beta`) for the approvals resources only; request lists stay on v1.0. `GraphTransport` gains `patch`. Action mapping: `selfActivate` → activate, `selfExtend` → extend, `selfRenew` → renew, others → other. ARM `properties.requestType`: `SelfActivate`/`SelfExtend`/`SelfRenew`. Requester: Graph `principal.displayName` or `userPrincipalName` (expanded), else `principalId`; ARM `expandedProperties.principal.displayName`.

`ApprovalDiff.newIds(previous: Set<String>, current: [ApprovalRequest]) -> [ApprovalRequest]` (tested) for the notification.

## 3. App

- `AppModel.approvals: [TenantKey: [ApprovalRequest]]`, `approvalsOrdered` (by `createdAt` ascending, tenant name tiebreak), `pendingApprovalCount`, `decisionInFlight: Set<String>`, `approvalErrors: [String: String]`, `lastApprovalJustification` (in `AppSettings`).
- In `refresh(_:kinds:)`, after the provider loop, read approvals for each kind in `kinds` whose approval provider exists, with the same first-party skip as Entra/Groups. Any error (403, consent, forbidden, network) leaves the previous list for that tenant/kind untouched and is not surfaced. Newly seen ids (per `ApprovalDiff`) post `notifier.notify(title: "Approval requested", body: "<target> for <requester>, <tenant>")`; a `seenApprovalIds` set persisted in `AppSettings` (JSON) prevents repeats across launches, pruned to ids still pending.
- `decide(_ request:, approve:, justification:) async`: inserts into `decisionInFlight`, calls the provider, on success removes the request from `approvals` and refreshes that tenant's kinds; on failure stores `approvalErrors[id] = userMessage`.
- Views: `ApprovalsSection` (pinned header "Approvals" + count + collapse via `collapsedApprovals` in settings), `ApprovalRow` (requester · target · tenant; caption "<duration> · <relative time>"; `.help(justification)`; Approve/Deny buttons, or "Decide in the portal" text for extend/renew; spinner while in flight; red error text), `DecisionView` routed window (`PanelRoute.decide(id: String, approve: Bool)`) with details, justification `TextField` (required for Deny), Cancel and Approve/Deny.
- Menu bar: `PanelStatus.compute` unchanged; `MenuBarLabel` adds `Image(systemName: "person.badge.clock")` when `model.pendingApprovalCount > 0`. `ActiveSummary`/search: the approvals section is filtered by the search query on target, requester, tenant.

## 4. Errors

Provider list errors are swallowed per tenant/kind (approvals are opportunistic). Decision errors surface in the row and the sheet. A decision that succeeds but whose request reappears on refresh (multi-stage policies) stays listed; the row's error shows nothing.

## 5. Testing

Core: `ApprovalProviderTests` for each kind (list parsing incl. action mapping and requester fallback; decide URL, method and body; step selection), `ApprovalDiffTests`. App: build + manual by the user against a tenant where they are an approver (or a second account requesting approval from the first).
