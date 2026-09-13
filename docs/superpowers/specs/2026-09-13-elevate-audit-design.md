# Elevate Audit — finding standing privileged access that belongs in PIM

Date: 2026-09-13. A companion command-line tool, separate from the Elevate app
and CLI. Relates to `2026-09-07-elevate-cli-design.md` (CLI conventions and
release train) and `docs/shared-app-registration.md` (why the Elevate
registration's scopes stay frozen).

## 1. Goal

An administrator adopting Elevate wants to know what in their tenant is still
*standing* privileged access — permanent role assignments held by users or by
groups, permanent members of groups that carry roles, permanent Azure Owner
and Contributor assignments — so they can convert it to PIM eligibility and
let Elevate do the activating. `elevate-audit` signs in as that administrator,
reads the tenant, and produces a prioritised list of exactly those things,
with the group nesting resolved so every finding names the person at the end
of the chain.

Decisions made during brainstorming:

- **A separate tool, not a feature of Elevate.** It ships as its own binary,
  formula and winget package, is never bundled into the Elevate pkg or MSI,
  and nothing in the Elevate app or CLI learns about it. The Elevate app
  registration's scopes are not touched.
- **No app registration needed.** Graph calls use Microsoft's *Microsoft Graph
  Command Line Tools* public client (`14d82eec-204b-4c2f-b7e8-296a70dab67e`)
  with dynamic consent to six read-only scopes (five directory reads plus
  `User.Read`); Azure calls use the Azure CLI
  public client (`04b07795-8ddb-461a-bbee-02f9e1bf7b46`) with `.default`,
  exactly as Elevate's Azure CLI sign-in method does. `--client-id` swaps the
  Graph client for tenants that block the first-party app.
- **Spike results (2026-09-13, reothor.no tenant).** The Azure CLI client's
  Graph token carries broad directory scopes but no PIM scopes: every
  `roleManagement/directory/*ScheduleInstances` and
  `identityGovernance/privilegedAccess/group/*` call returned 403
  `PermissionScopeNotGranted`, while `roleAssignments`, `groups` and
  `transitiveMembers` succeeded. The Graph PowerShell client issued a token
  whose `scope` claim contained precisely the five requested read scopes after
  one consent prompt. Hence two clients, one per resource.
- **Read-only.** The tool never writes to Graph or ARM. Every finding carries a
  remedy sentence and a portal deep link; the administrator acts in the portal.
- **Privileged by default, widen on request.** Entra roles flagged
  `isPrivileged` by Graph (bundled catalogue as fallback) and a curated Azure
  list; `--all-roles` reports every role.
- **Outputs**: terminal, `--json`, and a self-contained `--html` report styled
  with the product page's design tokens. The site gets an `audit.html` page and
  a committed sample report.
- **Approach A**: the new project references `Elevate.Core` for the Graph and
  ARM transports, JSON options and the role catalogue; audit-specific code is
  all new.

Success criteria:

- Running `elevate-audit` as a Global Reader in a tenant with a nested
  role-assigned group produces a finding for the group and one finding per
  transitive user member, each showing the membership path.
- A tenant that blocks the Graph PowerShell app gets a message naming the
  failing scope and the `--client-id` route, and succeeds with a custom id.
- `--html` opens offline in a browser with no network requests and reads as a
  page from elevate.reothor.no.
- The exit code gates a pipeline: 0 with no High findings, 2 with High
  findings, 1 on error.

## 2. Layout and architecture

```
audit/
  README.md
  Elevate.Audit.sln
  Directory.Build.props            (mirrors cli/)
  src/Elevate.Audit/
    Program.cs                     System.CommandLine root: scan (default), version, update
    Auth/AuditTokenProvider.cs     MSAL public clients, in-memory only
    Collectors/                    one class per data source → plain records
    Model/Snapshot.cs              immutable, serialisable input to the rules
    Model/Finding.cs
    Rules/                         Snapshot → IReadOnlyList<Finding>, one class per rule
    Rendering/TerminalRenderer.cs  Spectre.Console
    Rendering/JsonRenderer.cs
    Rendering/HtmlRenderer.cs + report.css
    Resources/AzurePrivilegedRoles.json
  tests/Elevate.Audit.Tests/
    Fixtures/snapshots/*.json      hand-written snapshots for the rules
    Fixtures/http/*.json           recorded Graph/ARM pages for the collectors
    Golden/sample-report.html      the file committed to site/audit-sample.html
  winget/                          Reothor.Elevate.Audit manifests
```

`Elevate.Audit.csproj`: `net10.0`, `ProjectReference` to
`windows/src/Elevate.Core/Elevate.Core.csproj`, packages
`Microsoft.Identity.Client`, `System.CommandLine` and `Spectre.Console` at the
versions the CLI uses, single-file trimmed publish for osx-arm64, osx-x64,
linux-x64, linux-arm64, win-x64, win-arm64. Assembly and executable name
`elevate-audit`.

Reused from Core, unchanged: `HttpTransport`, `GraphTransport` (paging over
`@odata.nextLink`, 429 with `Retry-After`, error mapping), the ARM paging
helper in `AzureResourceProvider`, `Json.LenientOptions`, `RoleCatalogue`
(`isPrivileged` per template id), the GitHub release update check. Core's
activation code is linked but never called. If a small read-only DTO is
missing from Core it is added to the audit project, not to Core.

Data flow: `AuditTokenProvider` → collectors run concurrently per source,
sequentially within a source → `Snapshot` → rules → `Finding[]` → renderers.
Rules never touch the network, which is what makes them unit-testable and
lets `--save-snapshot <file>` / `--from-snapshot <file>` re-render offline or
attach a snapshot to a bug report without tenant access.

## 3. Sign-in

`AuditTokenProvider` holds one `IPublicClientApplication` per resource.

| Resource | Client id | Scopes requested |
|---|---|---|
| Microsoft Graph | `14d82eec-204b-4c2f-b7e8-296a70dab67e` (default) | `User.Read` (tenant name and the signed-in account), `RoleManagement.Read.Directory`, `PrivilegedAssignmentSchedule.Read.AzureADGroup`, `PrivilegedEligibilitySchedule.Read.AzureADGroup`, `GroupMember.Read.All`, `User.ReadBasic.All` |
| Microsoft Graph | `--client-id <guid>` | `https://graph.microsoft.com/.default` (whatever the registration was consented) |
| Azure Resource Manager | `04b07795-8ddb-461a-bbee-02f9e1bf7b46` | `https://management.azure.com/.default` |

- Authority `https://login.microsoftonline.com/<tenant>`, `--tenant` defaults
  to `organizations`, which lands in the account's home tenant. A guest
  auditing a resource tenant passes that tenant id.
- Interactive flow: system browser with the `http://localhost` loopback by
  default; `--device-code` for SSH and containers. The Azure sign-in reuses the
  Graph session silently when MSAL can (same account, same authority) and
  prompts once more otherwise.
- **No token cache on disk.** Tokens live in process memory for the duration of
  one scan. There is nothing to sign out of.
- `--skip-azure` omits the Azure sign-in and the Azure section entirely.
- Consent declined (AADSTS65004) or app blocked by policy (AADSTS53003,
  AADSTS7000112, AADSTS700016 variants) → exit 1 with a message naming the
  scope or the blocked application and pointing at `--client-id` and
  `docs/audit.md`. `--client-id` values must be GUIDs.

## 4. Data collection

All requests are GET. Graph `v1.0` throughout. Every collector returns plain
records into the `Snapshot`; classification happens in the rules.

### 4.1 Entra directory roles (`DirectoryRoleCollector`)

- `roleManagement/directory/roleDefinitions?$select=id,templateId,displayName,isPrivileged,isBuiltIn`.
  A template id missing `isPrivileged` falls back to `RoleCatalogue`.
- `roleManagement/directory/roleAssignmentScheduleInstances?$expand=principal($select=id,displayName,userPrincipalName,userType),roleDefinition($select=id,displayName,templateId)`
  — every current active assignment. Recorded per instance: principal id and
  type, role definition id, `directoryScopeId`, `appScopeId`, `assignmentType`
  (`Assigned` | `Activated`), `startDateTime`, `endDateTime`, `memberType`
  (`Direct` | `Group` | `Inherited`).
- `roleManagement/directory/roleEligibilityScheduleInstances?$expand=principal,roleDefinition`
  — every current eligibility, same fields minus `assignmentType`.
- **Permanent** means `assignmentType == Assigned && endDateTime == null`.
  `Activated` instances are someone's current PIM activation and are never a
  finding. Instances with `memberType == Group` are the per-member echoes of a
  group assignment; they are kept for evidence but the group assignment is the
  finding, so members are not double-counted.
- Scope is never filtered: an administrative-unit-scoped or app-scoped
  assignment is reported with its scope shown.

### 4.2 Groups (`GroupCollector`)

For every distinct group principal seen in a permanent privileged assignment
(Entra or Azure), and for every role-assignable group
(`groups?$filter=isAssignableToRole eq true&$select=id,displayName,isAssignableToRole,securityEnabled,mailEnabled,groupTypes,visibility`):

- `groups/{id}?$select=…` for metadata when not already fetched.
- `groups/{id}/members?$select=id,displayName,userPrincipalName,userType,accountEnabled,servicePrincipalType&$top=999`
  walked breadth-first with a visited set of group ids. This yields the
  **membership path** (`alex ← Tier0-Admins ← Platform-Team`) and terminates
  on cycles. Members are the one read taken on the Graph **beta** endpoint, because v1.0
  `/groups/{id}/members` has a documented known issue that omits service principals (and the
  `$expand=members` workaround caps at 20 objects). `transitiveMembers` is not used for the path because it flattens;
  it is called once per top-level group only as a cross-check count in
  verbose mode.
- Members are classified by `@odata.type`: `user`, `group`,
  `servicePrincipal`, `device`, `orgContact`. Devices and contacts are counted
  in the group record but never produce findings.
- **Dynamic groups** (`groupTypes` contains `DynamicMembership`) are expanded
  like any other and flagged in the group record so the rules can say so.
- **PIM for Groups**, per group:
  `identityGovernance/privilegedAccess/group/assignmentScheduleInstances?$filter=groupId eq '{id}'&$expand=principal`
  and `…/eligibilityScheduleInstances?$filter=groupId eq '{id}'&$expand=principal`.
  Recorded: `accessId` (`member` | `owner`), `assignmentType`, dates,
  principal. A 403 or 404 on one group is recorded as `pimStatus =
  NotOnboarded` for that group, not an error. A non-empty result marks
  `pimStatus = Onboarded`; an empty 200 marks `Unknown` (Graph returns empty
  for both "onboarded with nothing" and some not-onboarded cases).

### 4.3 Azure resource roles (`AzureCollector`)

Skipped entirely with `--skip-azure`, and recorded as skipped when the ARM
sign-in fails or `subscriptions` returns nothing.

- `providers/Microsoft.Management/managementGroups?api-version=2021-04-01`
  (a 403 here is recorded and the walk continues with subscriptions only).
- `subscriptions?api-version=2022-12-01`.
- Per management group and per subscription:
  `{scope}/providers/Microsoft.Authorization/roleAssignments?api-version=2022-04-01&$filter=atScope()`
  — includes assignments at that scope and below (resource groups and
  resources under a subscription).
- Per subscription: `…/roleAssignmentScheduleInstances?api-version=2020-10-01&$filter=atScope()`
  and `…/roleEligibilityScheduleInstances?api-version=2020-10-01&$filter=atScope()`.
- `{scope}/providers/Microsoft.Authorization/roleDefinitions?api-version=2022-04-01`
  for names, cached per subscription.
- **Permanent** means a classic role assignment with no matching
  `roleAssignmentScheduleInstances` entry of `assignmentType == Activated`,
  or a schedule instance with `assignmentType == Assigned` and no end.
  Assignments inherited from a parent scope are reported once, at the scope
  where they are defined.
- Privileged Azure roles come from
  `Resources/AzurePrivilegedRoles.json`: Owner, Contributor, User Access
  Administrator, Role Based Access Control Administrator, Key Vault
  Administrator, Key Vault Data Access Administrator, Storage Account
  Contributor, Virtual Machine Administrator Login, Azure Kubernetes Service
  RBAC Cluster Admin, Security Admin, and any custom role whose
  `permissions.actions` contains `*` or `Microsoft.Authorization/*/write`.
  Severity: High for Owner, User Access Administrator and Role Based Access
  Control Administrator; Medium otherwise.
- Group principals are expanded through `GroupCollector` exactly like Entra.

### 4.4 Principal resolution (`PrincipalCollector`)

Ids that arrive without a display name (ARM principals, members returned with
limited information) are resolved through `directoryObjects/getByIds` in
chunks of 1000, recording `@odata.type`, `displayName`, `userPrincipalName`,
`userType`, `accountEnabled`, `servicePrincipalType`. A guest is
`userType == Guest`. Unresolvable ids are kept as `<unknown principal>` with
the raw id.

### 4.5 Resilience

- Core's `GraphTransport` maps a 429 to an error and does not retry, so the
  auditor wraps the HTTP client in a `RetryingHttpClient` that retries 429 and
  503 honouring `Retry-After` (capped at 60 s, five attempts) for Graph and ARM
  alike.
- Collectors run concurrently per source, sequentially within a source, so a
  large tenant does not fan out hundreds of parallel requests.
- A source that fails outright records a `Skipped { source, reason }` entry on
  the snapshot. Every renderer prints the skipped list at the top. Only a
  Graph sign-in failure or a directory-role 403 on the tenant-wide list aborts
  the scan, because without those there is nothing to report.
- `--verbose` prints each request line and page count to stderr.

## 5. Rules and findings

```
Finding {
  id            rule code, stable
  severity      High | Medium | Low | Info
  principal     { id, displayName, upn?, type, isGuest, accountEnabled }
  role          { id, displayName, templateId?, isPrivileged, system: Entra | Azure | Group }
  scope         { id, displayName, kind: Directory | AdministrativeUnit | Application | ManagementGroup | Subscription | ResourceGroup | Resource | Group }
  via           [ { groupId, displayName } … ]   outermost first; empty when direct
  remedy        one sentence
  portalUrl     deep link to the assignment or group in the Entra or Azure portal
  evidence      { assignmentId, startDateTime?, endDateTime?, assignmentType?, memberType? }
}
```

All rules apply only to privileged roles unless `--all-roles`.

| Code | Finding | Severity |
|---|---|---|
| `ENTRA-USER-PERMANENT` | A user holds a permanent active Entra role directly. | High |
| `ENTRA-GROUP-PERMANENT` | A group holds a permanent active Entra role. One finding for the group, plus one finding per transitive **user** member carrying the membership path in `via`. | High |
| `ENTRA-GROUP-NOT-PIM` | A role-assignable group that carries a role is not onboarded to PIM for Groups (`pimStatus == NotOnboarded`). | Medium |
| `ENTRA-GROUP-NOT-ASSIGNABLE` | A role is assigned to a group with `isAssignableToRole == false` (possible for some roles and scopes); PIM for Groups cannot govern it, remedy is to recreate as role-assignable. | Medium |
| `GROUP-MEMBER-PERMANENT` | A PIM-onboarded group has a permanent active member or owner. | High |
| `AZURE-PERMANENT` | A permanent privileged Azure role assignment at any scope; group principals expanded with `via`. | High / Medium per role list |
| `SP-PERMANENT` | A service principal or managed identity holds a permanent privileged role. Remedy is a workload-identity review, not PIM. | Info |
| `GUEST-PERMANENT` | A guest holds a permanent privileged role, directly or through a group. Emitted in addition to the base finding. | High |
| `ELIGIBLE-NO-END` | An eligibility (Entra, group or Azure) has no end date. | Low |
| `GA-COUNT` | The number of distinct principals that are permanent or eligible Global Administrators is below 2 or above 5. Counted through group expansion. One finding per tenant. | Medium |

Rules:

- One class per rule implementing `IRule { IEnumerable<Finding> Evaluate(Snapshot) }`;
  the runner concatenates and sorts by severity, then rule, then principal
  name. Rules are pure; no I/O.
- `GUEST-PERMANENT` and the `via` expansion of group findings depend on
  `PrincipalCollector` having resolved `userType`; an unresolved principal is
  reported as its base finding only.
- Dynamic groups: the group finding's remedy notes that PIM for Groups cannot
  govern a dynamic group and suggests a static role-assignable group.
- `--min-severity <level>` filters output only; the exit code counts High
  findings before filtering. `--ignore <CODE>` (repeatable) removes a rule
  entirely and is listed in the report header.
- Exit codes: `0` no High findings, `2` at least one High finding, `1` error.
  `--no-fail` forces 0 unless there was an error.

## 6. Output

### 6.1 Terminal

Spectre.Console. Header: tenant id and name, account, scan time, tool version,
skipped sources, ignored rules. One panel per rule in severity order with a
table of principal, role, scope, via (rendered as `A ← B ← C`), and a counts
footer. Group findings render their member findings as an indented tree under
the group row. `--quiet` prints only the one-line summary and sets the exit
code. Colour follows `NO_COLOR` and `--no-color`.

### 6.2 JSON

`--json <file>` (or `-` for stdout) writes:

```json
{
  "tool": { "name": "elevate-audit", "version": "1.0.0" },
  "tenant": { "id": "…", "displayName": "…" },
  "account": "alex.rivera@contoso.com",
  "scannedAt": "2026-09-13T13:10:00Z",
  "options": { "allRoles": false, "ignored": [], "minSeverity": "Low" },
  "skipped": [ { "source": "azure", "reason": "no subscriptions visible" } ],
  "summary": { "high": 3, "medium": 2, "low": 1, "info": 1 },
  "findings": [ /* section 5 schema, camelCase, ISO 8601 */ ]
}
```

Field names are a stability contract covered by a golden test.
`--save-snapshot <file>` writes the raw `Snapshot` (a different shape, with a
`"kind": "elevate-audit-snapshot"` marker so the two cannot be confused);
`--from-snapshot <file>` skips sign-in and collection.

### 6.3 HTML

`--html <file>` writes one self-contained page: inline CSS, no JavaScript
required, no external requests, no web fonts. `Rendering/report.css` is a
trimmed copy of the product page's tokens and components (colour scale, type
scale, spacing, card and table styles, the header treatment). A test asserts
that the `:root` token block in `report.css` is byte-identical to the one in
`site/styles.css`, so drift fails the build rather than going unnoticed.
Structure:

1. Header with tenant, account, date, tool version, skipped sources.
2. Summary cards per severity.
3. "Start here": the High findings as a checklist, each with remedy and portal
   link.
4. One section per rule; group findings use `<details>` for member paths.
5. Appendix: evidence ids, options used, scopes requested, and a note that the
   report contains personal data and should be handled accordingly.

Prints to PDF cleanly (`@media print` opens every `<details>`). Light only,
like the site, which defines no dark palette.

### 6.4 Site and docs

- `site/audit.html`: what the tool finds, the five Graph scopes with one line
  each on why they are read-only, the two client ids and what `--client-id`
  changes, that no data leaves the machine, install commands per platform, a
  text sample of the terminal output, and a link to `site/audit-sample.html`.
  Linked from the main navigation and the footer. Covered by
  `scripts/check-site.py`; the Pages workflow's `paths` filter already
  includes `site/**`.
- `site/audit-sample.html`: the HTML report rendered from
  `tests/Fixtures/snapshots/sample.json` (fictional contoso.com principals).
  A golden test regenerates it and fails when the committed file differs, so
  the sample can never lag the renderer.
- `docs/audit.md`: user guide — prerequisites (Global Reader or Privileged
  Role Administrator; Reader on Azure scopes), install, the consent prompt
  shown by "Microsoft Graph Command Line Tools", reading the report, the
  `--client-id` route with the exact scopes to add to a private registration,
  exit codes, snapshots.
- `docs/entra-app-registration.md`: a short section listing the optional read
  scopes for people who point `--client-id` at their own Elevate
  registration, clearly marked as not needed by Elevate itself.

## 7. Distribution

- Release workflow (`v*` tags) gains an `audit` job: `dotnet publish` for the
  six runtime identifiers, macOS binaries signed and notarised like the CLI
  helper, artefacts `elevate-audit-<v>-<rid>.tar.gz` / `.zip`, `.sha256` per
  file and `elevate-audit-<v>-checksums.txt`. Release notes get an
  `elevate-audit` install paragraph.
- Homebrew: `Formula/elevate-audit.rb`, generated by a sibling script
  `scripts/update-audit-formula.sh` (the existing script carries the CLI
  formula's deprecation notice and is left alone).
- winget: `audit/winget/` manifests for `Reothor.Elevate.Audit`, submitted by
  hand like the CLI's.
- Not bundled in the pkg or the MSI. Installing Elevate never installs the
  auditor and vice versa.
- `elevate-audit update` reuses Core's release check with asset prefix
  `elevate-audit-`; `elevate-audit version` prints version, commit and
  runtime.
- CI: a new `audit.yml` (build + test on ubuntu and macos, Windows targeting
  not required) mirrors `cli.yml`.

## 8. Testing

- **Rules** on snapshot fixtures built in code by a `SnapshotBuilder`: direct user; group with two
  levels of nesting and a cycle; guest reached only through nesting; dynamic
  group; PIM-onboarded group with a permanent owner; Azure Owner held by a
  group; non-privileged role excluded by default and included with
  `--all-roles`; GA count of 1 and of 6; eligibility without end; service
  principal with Global Administrator.
- **Collectors** against a fake `HttpTransport` replaying recorded pages:
  a two-page `roleAssignmentScheduleInstances`, a 429 followed by 200, a
  per-group 403 becoming `NotOnboarded`, ARM `nextLink` paging, a management
  group 403 that degrades to subscriptions only, `getByIds` chunking at 1000.
- **Renderers**: JSON golden file, HTML golden file (which doubles as the
  site sample), terminal smoke test asserting the summary line and exit code,
  `report.css` token block equality with `site/styles.css`.
- **Site**: `scripts/check-site.py` over the two new pages.
- **Manual** before the first tagged release: one run against the reothor.no
  tenant with `--save-snapshot`, result summarised in the PR.
- Everything runs on macOS with `dotnet test audit/tests/Elevate.Audit.Tests`.

## 9. Error handling

| Situation | Behaviour |
|---|---|
| Consent declined, app blocked by policy | Exit 1; message names the scope or app, points at `--client-id` and `docs/audit.md`. |
| Signed-in user lacks a directory role that can list assignments (403 on the tenant-wide list) | Exit 1; message names Global Reader, Privileged Role Administrator, Security Reader. |
| No Azure subscriptions, ARM sign-in cancelled, management groups forbidden | Section skipped with reason; scan continues. |
| One group's PIM endpoints 403/404 | Group marked `NotOnboarded`; scan continues. |
| Graph 429 | Retried per `Retry-After`; surfaced in `--verbose`. |
| Network failure or unexpected error inside a source | The whole source becomes a `Skipped` entry with the failing request as the reason; the other sources still render. Exception: the Entra directory role source, whose failure aborts with exit 1, because a report without it would under-report standing access. |
| `--from-snapshot` with a JSON report instead of a snapshot | Exit 1: "this is a report, not a snapshot". |

## 10. Out of scope for v1

Any write operation or "fix it" mode; Exchange, Intune and Defender role
systems; tenant-defined custom Entra roles beyond what Graph's `isPrivileged`
returns; scheduled or continuous scanning; running the scan in the browser or
on the website; storing anything between runs; Windows-only broker (WAM)
sign-in.
