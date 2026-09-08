# Elevate — access packages

Date: 2026-09-08. Approved in chat. macOS first; the Core provider and diff
logic are designed so the Windows port can follow. Design canvas:
https://claude.ai/code/artifact/d376687d-aba2-4c4e-8e12-27ff7b29dfbf, artboard
sources under `docs/design/access-packages/`.

## 1. Goal

Let a signed-in user discover, request and follow Entra entitlement management
access packages per tenant, without adding noise to the primary panel. Along
the way, tell the user when new eligible roles appear, however they arrived.

Success criteria:

- **Entry point per tenant.** A tenant whose Graph token carries the
  entitlement scope shows a box glyph beside its header pills and an "Access
  packages…" item first in its tenant menu. Both open the access packages
  window for that tenant. Nothing else in the panel changes.
- **Window with four tabs.** Available, Requested, Assigned, Declined, with a
  search field that filters the current tab by package name and description,
  and a footer with refresh, an "Updated n min ago" caption and Close.
- **Request flow.** Request with a justification; a policy picker when more
  than one policy applies; a hand-off to the My Access portal when the chosen
  policy requires question answers. Pending requests can be cancelled.
- **Notifications.** Request approved, request denied or failed, assignment
  revoked, assignment expired, and new eligible roles. Each names the package
  or roles and the tenant. First sight of a tenant never notifies.
- **New-role accent.** Roles that appeared since the last refresh get a tinted
  row and a "new" badge in the panel; the accent clears the second time the
  panel opens after they were first shown.
- **Quiet background.** Access package polling runs on panel open (throttled to
  once per 15 minutes) and on a background tick every 8 hours. It never rides
  the one-minute active-assignment timer.

Non-goals for this version: renewals and assignment updates, approving other
people's package requests, showing the approver's justification (needs
`EntitlementManagement.Read.All`, admin-consent only, plus a catalog role),
question answers inside Elevate, Windows and CLI parity.

## 2. Scope, consent and detection

- `EntitlementScopes.all` in `Auth/TokenProviding.swift`:
  `https://graph.microsoft.com/EntitlementMgmt-SubjectAccess.ReadWrite`. It is
  requested together with the Graph PIM scopes on the own-registration
  sign-in methods only. The permission does not require admin consent; users
  in tenants that already consented see one incremental consent prompt on
  their next interactive sign-in.
- `TenantContext.accessPackagesAvailable: Bool?`. Nil until a refresh has seen
  a token; the existing scope probe (`probeEntraActivation` in
  `AppModel+Activation`) is extended to set it from the token's `scp` claim.
  `AccessTokenClaims.permitsEntitlementSelfService(_:)` returns whether the
  scope is present.
- Azure CLI and Azure PowerShell sign-in methods never carry the scope, so the
  entry point never shows for those accounts. No new reason string is needed;
  the feature is simply absent, as with Entra roles for those methods.
- Docs: the permission table in `docs/entra-app-registration.md` gains a row
  marked "Admin consent: No"; `docs/entra-app/create-app-registration.sh` and
  the manifest gain the permission id `e9fdcbbb-8807-410f-b9ec-8d5468c7c2ac`.

## 3. Core (`ElevateCore`)

### Models (`Models/AccessPackages.swift`)

- `AccessPackage`: `id`, `displayName`, `description`, `isHidden`.
- `AccessPackageRequest`: `id`, `packageId`, `packageName`, `requestType`,
  `state: RequestState`, `status: String?`, `justification: String?`,
  `createdDateTime`, `completedDateTime`, `policyId`.
- `RequestState`: `submitted`, `pendingApproval`, `delivering`, `delivered`,
  `deliveryFailed`, `denied`, `scheduled`, `canceled`, `partiallyDelivered`,
  `unknown`. Decoded case-insensitively; anything else is `unknown`.
- `AccessPackageAssignment`: `id`, `packageId`, `packageName`,
  `state: AssignmentState` (`delivering`, `delivered`, `expired`,
  `unknown`), `policyName`, `expiresAt: Date?`.
- `PolicyRequirement`: `policyId`, `policyDisplayName`,
  `isApprovalRequired`, `requiresAnswers: Bool` (true when the requirement
  carries any question), `existingAnswers`.
- All Codable and Sendable; Graph DTOs decode with the lenient `GraphJSON`
  decoder and map into these types inside the provider, so the app never
  sees Graph shapes.

### Provider (`Providers/AccessPackageProvider.swift`)

Built on `GraphTransport` on the v1.0 base with `EntitlementScopes.all`. Every
call takes `identity` and `tenantId`.

- `requestablePackages()` →
  `GET /identityGovernance/entitlementManagement/accessPackages/filterByCurrentUser(on='allowedRequestor')`,
  all pages via `listAll`. Hidden packages are kept but flagged.
- `myRequests()` →
  `GET .../assignmentRequests/filterByCurrentUser(on='target')?$expand=accessPackage,assignment`.
- `myAssignments()` →
  `GET .../assignments/filterByCurrentUser(on='target')?$expand=accessPackage,assignmentPolicy`.
- `requirements(packageId:)` →
  `POST .../accessPackages/{id}/getApplicablePolicyRequirements`, one
  `PolicyRequirement` per policy.
- `request(packageId:policyId:justification:)` →
  `POST .../assignmentRequests` with body
  `{"requestType":"userAdd","justification":…,"assignment":{"accessPackageId":…,"assignmentPolicyId":…}}`;
  `assignmentPolicyId` omitted when nil. Returns the created
  `AccessPackageRequest`.
- `cancel(requestId:)` → `POST .../assignmentRequests/{id}/cancel`.
- Errors: 401/403 with the consent or permission error codes map to
  `PIMError.consentRequired`, everything else through the existing mapper.

### Diff logic (`Coordination/AccessPackageDiff.swift`, `Coordination/NewRoleTracker.swift`)

Pure value types, no I/O.

- `AccessPackageDiff.events(previous:current:) -> [AccessPackageEvent]` where
  `previous` and `current` each hold requests and assignments. Events:
  `approved(request)` when a request's state moves into `delivered`,
  `deliveryFailed(request)`, `denied(request)`, `revoked(assignment)` when a
  previously delivered assignment is missing or `expired` before its
  `expiresAt`, `expired(assignment)` when it is missing or `expired` at or
  after `expiresAt`. A nil `previous` yields no events (baseline).
- `NewRoleTracker` holds `seen: Set<RoleKey>`, `new: Set<RoleKey>` and
  `shownOpens: Int`. `observe(discovered:)` returns the additions and updates
  `seen` and `new`; with an empty `seen` it only baselines. `panelOpened()`
  increments `shownOpens` while `new` is non-empty and clears `new` when it
  reaches 2. Removed keys leave `seen`, so a role that disappears and returns
  is new again.

## 4. App

### State (`Storage/AppState.swift`)

Per `TenantKey`, all optional with empty defaults:

- `accessPackageRequests: [AccessPackageRequest]`,
  `accessPackageAssignments: [AccessPackageAssignment]`,
  `accessPackagesPolledAt: Date?`.
- `newRoleTracker: NewRoleTracker`.

Plain arrays, strings and dates, so the Windows `state.json` interop rules
hold.

### Model (`App/AppModel+AccessPackages.swift`)

- `pollAccessPackages(_ key: TenantKey)`: requests and assignments for one
  tenant, `AccessPackageDiff` against the stored lists, notifications for each
  event, store and persist, then reschedule expiry notifications.
- `pollAccessPackagesIfDue(force:)`: all tenants with
  `accessPackagesAvailable == true`, skipping those polled within 15 minutes
  unless forced.
- Triggers: `panelOpened()` calls `pollAccessPackagesIfDue()`; a new 8-hour
  task in `AppModel.startTimer` calls it while online; the window calls it
  with `force: true` on appear, on tab change and from its refresh button.
- `requestablePackages(for:)` and `requirements(for:packageId:)` are fetched
  by the window on demand and held in view state only.
- Expiry: delivered assignments with an `expiresAt` are handed to the expiry
  notifier alongside role assignments, keyed by assignment id, so the
  notification fires at the end date even between polls.

### New-role diff (`App/AppModel+Refresh.swift`)

After discovery merges the eligible roles for a tenant, the model calls
`newRoleTracker.observe(discovered:)`. Additions produce one notification per
tenant, "New roles available in Contoso: Exchange Administrator, Teams
Administrator", and are persisted. `panelOpened()` calls
`newRoleTracker.panelOpened()` for every tenant. A tenant whose discovery
fails or is consent-blocked is not observed, so a temporary outage cannot
baseline away roles.

### Views

- `PanelRoute.accessPackages(TenantKey)` rendered by `AccessPackagesView`,
  560×520, titled "Access packages in <tenant>".
- `TenantHeader`: the box glyph (`shippingbox`) beside `TenantPills` when
  `accessPackagesAvailable == true`, with accessibility label "Access
  packages". `TenantMenuItems`: "Access packages…" as the first item under the
  same condition.
- `RoleRow`: when the key is in the tenant's `newRoleTracker.new`, a tinted
  background using the accent colour at low opacity and a "new" badge after
  the name. The badge carries the meaning; the tint is decoration.
- `AccessPackagesView`: search field, segmented `Picker` for the four tabs,
  a `List` per tab, footer with refresh, "Updated" caption and Close
  (`cancelAction`). Tabs:
  - Available: name, description, Request button; a state caption with dot
    instead when a pending request or delivered assignment exists.
  - Requested: states submitted, pendingApproval, delivering, scheduled,
    partiallyDelivered, newest first; created date and the user's
    justification; Cancel request for submitted and pendingApproval.
  - Assigned: delivered assignments, expiry or "No expiry", policy name;
    an "expires in n days" state in orange within 7 days and a "Request
    again" button.
  - Declined: denied, deliveryFailed, canceled; completed date and Graph's
    `status` text for failed and canceled; "Request again" for denied and
    failed.
- `RequestPackageSheet`: loads requirements on appear. One policy: hidden
  picker. Several: a `Picker` labelled with policy name, duration and whether
  approval is required. `requiresAnswers` on the chosen policy replaces the
  form with an explanation and an "Open in My Access" button that opens
  `https://myaccess.microsoft.com/@<tenantId>#/access-packages/<packageId>`.
  Otherwise: justification `TextEditor` (required), Cancel and "Submit
  Request" as the default button. On success the sheet closes, the window
  switches to Requested and forces a poll.
- Empty states are one-line captions per tab. `PIMError.consentRequired`
  replaces the lists with "Access packages are not permitted in this tenant"
  and the admin consent link the tenant menu already builds.
- Colours follow the panel: yellow pending, green delivered or delivering,
  orange expiring, red denied or failed, grey canceled.

## 5. Testing

Core (`swift test`), recorded fixtures under `Tests/ElevateCoreTests/Fixtures`:

- Provider: two-page package listing; request decoding for every state plus an
  unknown value; requirements with one policy, two policies, and a policy
  with questions; the `userAdd` body with and without a policy id; cancel
  URL; 403 with the consent error code maps to `consentRequired`.
- `AccessPackageDiff`: baseline yields nothing; approved, denied, failed,
  revoked before expiry, expired at expiry; unchanged lists yield nothing.
- `NewRoleTracker`: first observe baselines; additions returned once;
  removals ignored; removed-then-returned is new; clears on the second panel
  open, not the first; persists through Codable round trip.

App (hosted test bundle):

- Tenant menu and header show the entry point only when
  `accessPackagesAvailable == true`.
- `RouteWindow` renders `AccessPackagesView` for the route.
- `RoleRow` shows the badge for a key in `new`.

Deferred to the user, cannot be verified here:

- Incremental consent prompt on next sign-in in an already-consented tenant.
- A real request round trip: request, approval in the portal, approved
  notification after a background or forced poll, assignment on the Assigned
  tab.
- Revocation and expiry notifications against a real tenant.
- The My Access hand-off URL opening the right package.

## 6. Delivery

One branch, one PR to `main`, following the subagent-driven plan. Order:
scopes and detection, Core models and provider, diff logic, state and model,
views, docs. The changelog entry goes under an unreleased heading.
