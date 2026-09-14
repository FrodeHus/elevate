# Finding standing access with elevate-audit

`elevate-audit` is a companion command-line tool to Elevate. It signs in as an administrator,
reads your tenant, and lists every piece of *standing* privileged access that could become
PIM eligibility instead: permanent Entra role assignments held by people or by groups (with the
nested groups resolved), permanent members of groups that PIM for Groups already manages, and
permanent Owner, Contributor and similar assignments in Azure. It writes nothing. Nothing leaves
your machine.

It is separate from the Elevate app and CLI: a different binary, no shared settings, no shared
sign-in, and it never touches the Elevate app registration.

Related: [Getting started](getting-started.md), [Setting up the Entra app registration](entra-app-registration.md).

## 1. Install

macOS and Linux with Homebrew:

```sh
brew tap FrodeHus/elevate https://github.com/FrodeHus/elevate
brew trust frodehus/elevate
brew install frodehus/elevate/elevate-audit
```

Windows: `winget install Reothor.Elevate.Audit` once the manifest is published; until then, or on
any platform, download `elevate-audit-<version>-<platform>.tar.gz` (`.zip` on Windows) from the
[latest audit release](https://github.com/FrodeHus/elevate/releases?q=audit-v), unpack it anywhere
on your PATH, and verify it with `sha256sum -c elevate-audit-<version>-checksums.txt`. The audit
tool is versioned and released separately from the Elevate app and CLI: its releases are the
`audit-v` tags, titled "Elevate Audit", and its changelog is
[audit/CHANGELOG.md](../audit/CHANGELOG.md).

## 2. What it asks for

`elevate-audit` needs no app registration. It signs in with two Microsoft public clients:

| Resource | Client | What it asks for |
|---|---|---|
| Microsoft Graph | **Microsoft Graph Command Line Tools** (`14d82eec-204b-4c2f-b7e8-296a70dab67e`), Microsoft's own multi-tenant public client | Six delegated, read-only scopes, listed below |
| Azure Resource Manager | **Azure CLI** (`04b07795-8ddb-461a-bbee-02f9e1bf7b46`) | `.default`, which is what the Azure CLI itself uses |

The Graph scopes and why each one is needed:

| Scope | Used for |
|---|---|
| `User.Read` | The tenant's display name and the signed-in account for the report header. |
| `RoleManagement.Read.Directory` | Entra role definitions, every active role assignment and every eligibility. |
| `PrivilegedAssignmentSchedule.Read.AzureADGroup` | Active memberships and ownerships of groups managed by PIM for Groups. |
| `PrivilegedEligibilitySchedule.Read.AzureADGroup` | Eligible memberships and ownerships of those groups. |
| `GroupMember.Read.All` | Expanding the members of role-assigned groups, including nested groups. |
| `User.ReadBasic.All` | Names and sign-in names of the people found, instead of bare object ids. |

Every read is Microsoft Graph v1.0 except role definitions (for the isPrivileged flag) and group
members (v1.0 omits service principals), both read on beta.

All of them are read scopes; several require an administrator to consent. Since the person
running a standing-access audit is a privileged administrator, you consent for yourself at the
first sign-in. The consent screen is Microsoft's, and names Microsoft's tool, not Elevate.

Tokens live in memory for one run and are never written to disk. There is nothing to sign out of.

## 3. Who can run it

The signed-in account needs a directory role that can list PIM assignments tenant-wide: **Global
Reader**, **Privileged Role Administrator** or **Security Reader** all work. For the Azure part,
**Reader** on the management groups or subscriptions you want covered. Without Azure access the
Azure section is skipped and the report says so.

## 4. Run it

```sh
elevate-audit
```

Sign in when the browser opens (or pass `--device-code` over SSH), accept the consent prompt
once, and read the report in the terminal. Useful options:

| Option | Effect |
|---|---|
| `--html report.html` | Also write a self-contained HTML report you can share or print. |
| `--json report.json` (or `--json -`) | Also write the JSON report, or print only JSON on stdout. |
| `--all-roles` | Report every role, not only the privileged ones. |
| `--min-severity medium` | Hides findings below the level from the output; the summary counts and the exit code still cover every finding, and the report says how many were hidden. |
| `--ignore GA-COUNT` | Skip a rule (repeatable). |
| `--skip-azure` | Do not sign in to Azure or scan Azure RBAC. |
| `--tenant <id>` | Scan a tenant you are a guest in. |
| `--save-snapshot scan.json` / `--from-snapshot scan.json` | Save everything that was read; re-run the rules offline later, or attach the snapshot to a bug report. |
| `--client-id <guid>` | Use your own public client for Graph; see section 5. |
| `--verbose` | Log every request; also cross-checks each role-assigned group's walk against Graph's `transitiveMembers` and prints a line when the counts differ. |

Exit codes: `0` no high findings, `2` at least one high finding, `1` error. `--no-fail` forces
`0` so a pipeline can publish the report without failing on it.

`--save-snapshot` writes every principal's display name and sign-in name to the file; treat it
like the report — it contains personal data.

### The rules

| Code | Finding | Severity |
|---|---|---|
| `ENTRA-USER-PERMANENT` | A user holds a permanent active Entra role directly. | High |
| `ENTRA-GROUP-PERMANENT` | A group holds a permanent active Entra role; one finding for the group and one per person in it, with the path through nested groups. | High |
| `ENTRA-GROUP-NOT-PIM` | A role-assignable group carrying a role is not onboarded to PIM for Groups. | Medium |
| `ENTRA-GROUP-NOT-ASSIGNABLE` | A role is assigned to a group that is not role-assignable, which PIM for Groups cannot govern. | Medium |
| `GROUP-MEMBER-PERMANENT` | A group managed by PIM for Groups still has a permanent member or owner. | High |
| `AZURE-PERMANENT` | A permanent privileged Azure role assignment at any scope. | High for Owner, User Access Administrator and RBAC Administrator; Medium otherwise |
| `SP-PERMANENT` | A service principal or managed identity holds a permanent privileged role. PIM does not apply; review the workload identity instead. | Info |
| `GUEST-PERMANENT` | A guest holds a permanent privileged role, directly or through a group. | High |
| `ELIGIBLE-NO-END` | An eligibility has no end date. | Low |
| `GA-COUNT` | Fewer than 2 or more than 5 people can become Global Administrator. | Medium |

"Privileged" means the roles Microsoft Graph marks `isPrivileged` for Entra, and for Azure:
Owner, Contributor, User Access Administrator, Role Based Access Control Administrator, Key
Vault Administrator, Key Vault Data Access Administrator, Storage Account Contributor, Virtual
Machine Administrator Login, Azure Kubernetes Service RBAC Cluster Admin, Security Admin, and any
custom role whose actions include `*` or can write role assignments.

A currently *activated* PIM assignment is not standing access and is never reported.

## 5. A tenant that blocks the Graph PowerShell app

Some tenants block Microsoft Graph Command Line Tools with an app consent policy or Conditional
Access. `elevate-audit` then reports the failing scope or the AADSTS code and exits. Pass any
public client your tenant allows with `--client-id`:

1. Register a public client (or reuse one you own, for example your own Elevate registration —
   see [section 7 of the app registration guide](entra-app-registration.md#7-optional-read-scopes-for-elevate-audit)).
2. Under **Authentication**, add the platform **Mobile and desktop applications** with the
   redirect URI `http://localhost`.
3. Under **API permissions**, add the six delegated Microsoft Graph scopes from section 2 and
   grant admin consent.
4. Run `elevate-audit --client-id <application id>`.

With a custom client the tool asks for `https://graph.microsoft.com/.default`, so it can only do
what that registration was consented for.

## 6. Reading the report

The HTML report opens with a one-sentence verdict — how many people and workload identities
hold standing privileged access, and whether anything was skipped — and one tile per area:
Entra roles, PIM for Groups, Azure RBAC, Guests, Workload identities, Hygiene and Coverage. Each
tile shows a state (Critical, Attention, Review, Clean or Not scanned), the counts per severity,
and one line in plain words; click it to jump to the area. An area marked **under-counts** has a
partially skipped source behind it, listed under Coverage.

**Start here** lists the high findings, grouped so one action fixes many: a role-assigned group
appears once, with the number of people it reaches, instead of once per member.

Then one collapsible section per area, and inside it one block per rule and severity. Group
findings are rolled up into one card per group: who it grants what to, the remedy, a membership
outline showing the nested groups with counts, a small diagram of how the groups nest, and the
full list of people reached. Direct findings stay as table rows. The Coverage section lists every
source and whether it was read. The appendix holds the options used, the scopes requested and the
assignment ids for auditors.

With JavaScript on, a toolbar adds search across people, groups, roles and scopes, severity
filters, and expand or collapse all; long tables show the first
50 rows with a "Show all" button. With JavaScript off the report is the same page without the
toolbar, every row visible. The report loads nothing from the network either way, and prints
with every section open and every filter cleared.

Remedies are the standard PIM moves: convert a permanent assignment to eligible, onboard a group
to PIM for Groups, replace a dynamic or non-role-assignable group with a static role-assignable
one, or, for service principals, review the workload identity's need for the role.

A sample report from a fictional tenant is at
<https://elevate.reothor.no/audit-sample.html>.

## 7. Troubleshooting

- **"Consent … was declined or is not permitted"** — accept the six read scopes, or use
  `--client-id` (section 5).
- **"The signed-in account may not list the tenant's role assignments"** — you need Global
  Reader, Privileged Role Administrator or Security Reader (section 3).
- **The Azure section says "skipped"** — the account sees no subscriptions, or the ARM sign-in
  was cancelled. Grant Reader, or pass `--skip-azure` to silence it.
- **ENTRA-GROUP-NOT-PIM on a group you believe is onboarded** — the rule also fires for a
  role-assignable group with no PIM for Groups assignments at all, because Graph returns an empty
  result both for an un-onboarded group and for an onboarded one with nothing assigned.
- **A group shows as "not onboarded" although it is** — the account cannot read that group's
  PIM data; for role-assignable groups that needs Global Reader or Privileged Role Administrator
  at directory scope.
- **Throttled** — the tool retries `429` replies honouring `Retry-After`; `--verbose` shows each
  request.
- **The `groups` source shows "N nested group(s) could not be read"** — the account lacks read
  access to those groups, so their members are missing from the walk.
- **The `groups` source is skipped tenant-wide** — the scope needed to read group membership is
  missing entirely for this account, not just for individual groups.
- **Degraded-mode group output** — when the account cannot read groups tenant-wide, the report
  lists a `groups` skipped source, and the few groups read in the same batch before the failure was
  detected still appear, but with no members; their `ENTRA-GROUP-NOT-PIM` and `NOT-ASSIGNABLE`
  findings are based on metadata alone (role-assignability, PIM onboarding), and member findings for
  those groups are missing entirely.
- **`--verbose` prints a walk/transitiveMembers count mismatch** — for a role-assigned group, the
  tool's own membership walk found a different number of members than Graph's `transitiveMembers`
  reports; this is informational and does not by itself indicate a missing finding.
