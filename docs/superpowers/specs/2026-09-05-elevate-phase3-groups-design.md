# Elevate phase 3 — PIM for Groups

Date: 2026-09-05. Extends `2026-09-04-pimtray-design.md` §7.3. Approved in chat
(groups in their own panel tab via a segmented control; automatic role refresh
after a group activation).

## 1. Goal

List, activate, deactivate and cancel PIM for Groups eligibilities (membership
and ownership of groups) for every tracked tenant of an own-app or custom-app
account, in a "Groups" tab of the panel. Mark Entra and Azure role rows that
are granted through a group.

Success criteria:

- The panel has a segmented control "Roles | Groups" under the header. The
  Groups tab lists, per account and tenant, the groups the identity is eligible
  to join or own, with "member" or "owner" as caption, and the same activate,
  countdown, deactivate and cancel controls as role rows.
- Activation honours the group's policy (duration, justification, ticket, MFA
  or authentication context, approval) exactly as roles do.
- Bulk selection and "Activate N" work within the Groups tab.
- The menu bar count includes active group memberships and ownerships.
- After a group activation or deactivation succeeds, the tenant's Entra and
  Azure roles are refreshed automatically so roles carried by the group appear.
- Manual groups from "Configure known PIM roles… → Groups" activate through
  the same path.
- Azure CLI and Azure PowerShell accounts never call the group APIs and show
  an explanatory empty state in the Groups tab.
- Entra and Azure role rows whose eligibility comes through a group carry the
  caption "via group".

Out of scope: approving other people's requests, eligibility administration,
group membership outside PIM.

## 2. API surface (Microsoft Graph v1.0)

Base `https://graph.microsoft.com/v1.0/identityGovernance/privilegedAccess/group`.
Bearer token for the Graph scopes in §3, issued in the tenant. Every list
follows `@odata.nextLink`.

| Operation | Request |
|---|---|
| Eligible | `GET …/eligibilityScheduleInstances/filterByCurrentUser(on='principal')?$expand=group($select=id,displayName)` |
| Active | `GET …/assignmentScheduleInstances/filterByCurrentUser(on='principal')?$expand=group($select=id,displayName)`; keep `assignmentType == "Activated"` |
| Pending | `GET …/assignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter=status eq 'PendingApproval'` |
| Policy | `GET /policies/roleManagementPolicyAssignments?$filter=scopeId eq '{groupId}' and scopeType eq 'Group' and roleDefinitionId eq '{member\|owner}'&$expand=policy($expand=rules)` |
| Activate | `POST …/assignmentScheduleRequests` body `{accessId, principalId, groupId, action: "selfActivate", justification, ticketInfo?, scheduleInfo: {startDateTime, expiration: {type: "afterDuration", duration}}}` |
| Deactivate | same POST with `action: "selfDeactivate"` and no `scheduleInfo` |
| Cancel | `POST …/assignmentScheduleRequests/{id}/cancel` |

Fields consumed from eligibility instances: `id`, `groupId`, `accessId`
(`member`/`owner`), `principalId`, `memberType` (`direct`/`group`),
`group.displayName`. From assignment instances additionally
`assignmentType`, `startDateTime`, `endDateTime`. From requests: `id`,
`groupId`, `accessId`, `status`, `createdDateTime`, `scheduleInfo`.

`principalId` on activate and deactivate is always the caller's object id in
the tenant, read from the `oid` claim of the Graph token
(`AccessTokenClaims.objectId`), with the eligibility's `principalId` as
fallback for an opaque token. This mirrors the Azure provider: an eligibility
inherited through a nested group names the outer group, which the service
refuses.

Policy rules are parsed by the same rule ids as Entra roles:
`Expiration_EndUser_Assignment`, `Enablement_EndUser_Assignment`,
`Approval_EndUser_Assignment`, `AuthenticationContext_EndUser_Assignment`.

## 3. Scopes and consent

New `GroupScopes.all` in `TokenProviding.swift`:

```
https://graph.microsoft.com/PrivilegedEligibilitySchedule.Read.AzureADGroup
https://graph.microsoft.com/PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup
https://graph.microsoft.com/RoleManagementPolicy.Read.AzureADGroup
```

The group provider requests exactly these. Both token providers cope without
change: MSAL acquires a Graph token per requested scope set (admin consent is
already granted for the union), and the loopback flow asks for the Graph
resource's `/.default`, which carries every consented scope. Consequences:

- The admin consent URL (`AppModel.adminConsentURL`) lists the union.
- A 403 from a group read maps to `consentRequired` as for Entra. The tenant
  gets `groupsUnavailableReason` (new `TenantContext` field, persisted) and the
  Groups tab shows "Not permitted in this tenant" with the admin consent link;
  Entra and Azure keep working. "Retry discovery" clears it.
- First-party accounts (`!signInMethod.isPreauthorisedForEntraActivation`)
  skip the group provider as they skip Entra; the Groups tab shows "Azure
  resource roles only" for them.

`AccessTokenClaims.permitsEntraActivation` is unchanged; there is no
capability probe for groups because a missing scope is answered per request.

## 4. Core changes

- `GroupProvider` (replaces the stub in `StubProviders.swift`, own file
  `Providers/GroupProvider.swift`): `kind = .group`, `scopes = GroupScopes.all`,
  `GraphTransport` with the Graph error mapper. `eligibleRoles` returns
  `EligibleRole(key: .group(groupId:accessId:), displayName: group name,
  detail: "member"/"owner", source: .discovered, policy: .manualDefault)`;
  policies are filled by `AppModel.applyPolicies` as for roles.
  `activeAssignments` merges activated instances and pending requests.
  `activate` returns `ActiveAssignment` keyed by the request's key; status
  from the request `status` (`Provisioned` → active, `PendingApproval` →
  pending, `Denied`/`Failed`/`Canceled` → failed).
- `EligibleRole.viaGroup: String?` (Codable, default nil): display name of
  the granting group, or `"group"` when the API gives only the member type.
  Entra provider fills it from `memberType == "Group"` on
  `roleEligibilitySchedules` (with `$expand=principal` not available for
  the current user, the caption is the constant "via group"). Azure provider
  fills it from `properties.memberType == "Group"` with
  `expandedProperties.principal.displayName`.
- `ManualRoleSource` already produces `.group` keys from manual groups; the
  merge by scope applies unchanged.
- `TenantContext.groupsUnavailableReason: String?`.
- `ActivationCoordinator` needs no change: the provider registry already
  includes `.group`.

## 5. App changes

- `AppModel`:
  - `panelTab: PanelTab` (`roles`/`groups`) persisted in `AppSettings`
    (`UserDefaults` key `panelTab`).
  - `refresh(_:)` runs the group provider for own-app and custom-app
    identities; consent and unavailable handling per §3; rows land in the
    same `roles[tenantKey]` map (kind `.group`).
  - `roles(for:tab:)` filters by kind: `.roles` → Entra + Azure, `.groups`
    → group. `activeCount` counts all kinds.
  - `selection` is cleared when the tab changes; `selectMode` stays.
  - After `activate` or `deactivate` where any outcome key has kind
    `.group` and succeeded, schedule `refreshRolesAfterGroupChange(tenantKey)`:
    sleep 5 s, then `refresh(tenantKey)` restricted to Entra and Azure
    providers (new `kinds:` parameter on `refresh`). A pending-approval
    outcome does not trigger it.
- Views:
  - `PanelView`: `Picker` with `.segmented` style, labels "Roles" and
    "Groups", under the header; the list, empty states and "Activate N"
    read from `roles(for:tab:)`.
  - `TenantRoles` gets the tab; empty-state text per tab ("No eligible
    roles." / "No eligible groups."); groups-unavailable reason and the
    first-party note rendered in the Groups tab only.
  - `RoleRow` shows `detail` already; for groups it is "member"/"owner".
    `viaGroup` renders as a secondary caption "via group" or "via <name>".
  - `TenantMenuItems`: "Open admin consent link…" also when
    `groupsUnavailableReason` is set.
  - `IdentityHeader`/`TenantHeader` active count uses all kinds.
  - `ActivationView` unchanged (group rows carry policies like roles).
  - Menu bar label uses `model.activeCount` (unchanged, now includes groups).
- `ExpiryNotifier`: names come from `roles`, unchanged.

## 6. Error handling

- Group read 403 → `consentRequired` → `groupsUnavailableReason` (no
  per-refresh error pill); other errors → tenant error pill prefixed
  "Groups:".
- Activation failures surface in `ActivationView` as for roles, including
  the MFA / authentication-context retry from `ActivationCoordinator`.
- The deferred role refresh swallows its own errors into the usual tenant
  error pill; it never blocks or re-fires.

## 7. Testing

Swift Testing in `ElevateCoreTests`:

- `GroupProviderTests`: eligible list with paging and member/owner captions;
  active merges activated instances and pending requests; policy parsing
  including authentication context; activate body (`accessId`, `groupId`,
  caller `principalId` from a JWT token, `selfActivate`, duration, ticket);
  deactivate body; cancel URL; 403 → `consentRequired`.
- `ModelsTests`: `EligibleRole.viaGroup` and `TenantContext.groupsUnavailableReason`
  decode when absent.
- Entra and Azure provider tests: "via group" caption from fixtures with
  `memberType` Group.
- `GroupScopes` and the admin consent URL include the three group scopes.

Manual verification by the user: Groups tab lists the eligibility, activation
prompts as the policy requires, membership shows active with countdown, roles
carried by the group appear after the deferred refresh, deactivate works after
five minutes.

## 8. User action

Delegated Graph permissions `PrivilegedEligibilitySchedule.Read.AzureADGroup`,
`PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup` and
`RoleManagementPolicy.Read.AzureADGroup` added to the app registration with
admin consent (done 2026-09-05).
