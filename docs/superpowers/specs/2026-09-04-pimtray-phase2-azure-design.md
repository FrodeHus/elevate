# PimTray phase 2 — Azure resource roles

Date: 2026-09-04. Extends `2026-09-04-pimtray-design.md` §7.2. Approved.

## 1. Goal

Discover, activate, deactivate and cancel PIM eligibilities for Azure resource
roles (management group, subscription, resource group, resource) for every
tracked tenant, presented as ordinary role rows beside Entra directory roles.

Success criteria:

- After sign-in, each tenant section lists the identity's Azure eligibilities
  as rows: role name, with a caption of the scope display name and type.
- Activate, bulk activate, deactivate (5-minute rule), cancel pending, expiry
  notification and remembered justification work unchanged for these rows.
- A tenant where the identity has no Azure access contributes no Azure rows
  and no error.
- Manual Azure roles entered as scope + role name activate through the same
  path.

## 2. API surface (ARM, api-version 2020-10-01)

Base `https://management.azure.com`, bearer token for scope
`https://management.azure.com/user_impersonation` issued in the tenant.
Tenant-wide listing uses the empty scope prefix
(`/providers/Microsoft.Authorization/...`). Every list follows `nextLink`.

| Operation | Request |
|---|---|
| Eligible | `GET /providers/Microsoft.Authorization/roleEligibilityScheduleInstances?$filter=asTarget()` |
| Active | `GET /providers/Microsoft.Authorization/roleAssignmentScheduleInstances?$filter=asTarget()`; keep `properties.assignmentType == "Activated"` |
| Pending | `GET /providers/Microsoft.Authorization/roleAssignmentScheduleRequests?$filter=asTarget()`; keep `properties.status` in `PendingApproval`, `PendingAdminDecision`, `PendingApprovalProvisioning` |
| Policy | `GET {scope}/providers/Microsoft.Authorization/roleManagementPolicyAssignments`; pick the entry whose `properties.roleDefinitionId` matches; rules in `properties.effectiveRules` with ids `Expiration_EndUser_Assignment`, `Enablement_EndUser_Assignment`, `Approval_EndUser_Assignment` |
| Resolve role name | `GET {scope}/providers/Microsoft.Authorization/roleDefinitions?$filter=roleName eq '{name}'&api-version=2022-04-01` → `value[0].id` |
| Activate | `PUT {scope}/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{uuid}` body `properties: {principalId, roleDefinitionId, requestType: "SelfActivate", linkedRoleEligibilityScheduleId, justification, ticketInfo?, scheduleInfo: {startDateTime, expiration: {type: "AfterDuration", duration}}}` |
| Deactivate | same PUT with `requestType: "SelfDeactivate"`, `principalId`, `roleDefinitionId`, `linkedRoleEligibilityScheduleId` |
| Cancel | `POST {scope}/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{name}/cancel` |

Fields consumed from eligibility instances: `id`, `name`, `properties.scope`,
`properties.roleDefinitionId`, `properties.principalId`,
`properties.roleEligibilityScheduleId`, `properties.expandedProperties.
roleDefinition.displayName`, `properties.expandedProperties.scope.displayName`
and `.type`.

## 3. Model changes (backward compatible)

- `EligibleRole.detail: String?` — row caption, e.g. `Pay-As-You-Go · subscription`.
  `nil` for Entra roles.
- `RoleScope.azureResource(scope:roleDefinitionId:)` is unchanged. For
  discovered roles `roleDefinitionId` is the full ARM id. For manual roles it
  may be a role *name*; the provider resolves it at activation.
- `RoleKey` equality therefore treats a manual "Contributor" and a discovered
  `/subscriptions/…/roleDefinitions/b24988ac-…` as different keys; the merge
  drops the manual entry when a discovered role with the same scope and
  resolved id exists (compare on scope + display name, case-insensitive).

## 4. AzureResourceProvider

`struct AzureResourceProvider: PIMProvider`, `kind = .azureResource`,
`scopes = ArmScopes.all`, `init(http:tokens:)`.

- `eligibleRoles`: list eligible, map each instance to `EligibleRole(key:
  .azureResource(scope, roleDefinitionId), displayName, detail, source:
  .discovered, policy: .manualDefault)`; de-duplicate on key; sort by name.
- `activeAssignments`: activated instances → `.active` with start/end; pending
  requests → `.pendingApproval` with `assignmentId = name` (needed for cancel).
- `policy(for:)`: list policy assignments at the role's scope, match the role
  definition id, map rules exactly as `EntraDirectoryProvider` does; missing
  → `.manualDefault`.
- `activate`: (1) resolve role definition id if it does not contain `/`;
  (2) list eligible and find the instance with equal scope (case-insensitive)
  and role definition id → `principalId`, `roleEligibilityScheduleId` (the
  GUID name at the end of the path); none → `PIMError.notEligible`;
  (3) PUT SelfActivate; map status like Entra (`Provisioned` → active,
  pending states → pendingApproval/pendingProvisioning, `Denied`/`Failed`/
  `Canceled`/`Revoked` → failed); end = start + duration.
- `deactivate`: same lookup, PUT SelfDeactivate.
- `cancelPendingRequest`: POST cancel with the request name.

Error mapping: `ArmTransport` reuses `GraphTransport` request plumbing with an
ARM-specific mapper: 401 with claims → `claimsChallenge`, plain 401 →
`interactionRequired`, 403 → `policyViolation("Not permitted at this scope")`,
400 with `ActiveDurationTooShort` → the 5-minute message, 400
`RoleAssignmentRequestPolicyValidationFailed` → `policyViolation(message)`,
429 → throttled, else `unexpected`.

## 5. AppModel

- `refresh(_:)` iterates `[entra, azure]` providers. Eligible and active are
  fetched per provider with independent error handling; a provider failure
  records `tenantErrors[key]` prefixed with the provider name but never
  discards the other provider's rows. The consent-blocked rule applies to the
  Entra provider only; ARM consent is user-consentable and handled by the
  normal interactive retry.
- Policy cache and bounded policy fetch are shared; the provider is chosen by
  `role.key.scope.kind`.
- Manual Azure roles are merged per §3.

## 6. UI

- `RoleRow` shows `role.detail` as a secondary caption under the name.
- `ConfigureRolesView` Azure tab unchanged (scope + role name); its footnote
  about phase 2 is removed.
- README gains an "Azure resource roles" section: the ARM consent prompt on
  first use, and that tenants without Azure access show nothing.

## 7. Tests (Core, Swift Testing, stubbed HTTP)

`AzureResourceProviderTests`: eligible mapping with detail caption and paging
(`nextLink`); active filtering and pending mapping; policy rules; activation
body (principal, linked schedule id, duration) after lookup; role-name
resolution for manual roles; `notEligible` when no instance matches;
deactivate body; cancel URL; 403 mapping. `ManualRoleSource` merge test for
name-based de-duplication.
