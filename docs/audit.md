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
[latest release](https://github.com/FrodeHus/elevate/releases/latest), unpack it anywhere on your
PATH, and verify it with `sha256sum -c elevate-audit-<version>-checksums.txt`.

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
| `--min-severity medium` | Hide low and informational findings. |
| `--ignore GA-COUNT` | Skip a rule (repeatable). |
| `--skip-azure` | Do not sign in to Azure or scan Azure RBAC. |
| `--tenant <id>` | Scan a tenant you are a guest in. |
| `--save-snapshot scan.json` / `--from-snapshot scan.json` | Save everything that was read; re-run the rules offline later, or attach the snapshot to a bug report. |
| `--client-id <guid>` | Use your own public client for Graph; see section 5. |

Exit codes: `0` no high findings, `2` at least one high finding, `1` error. `--no-fail` forces
`0` so a pipeline can publish the report without failing on it.

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

The HTML report opens with the counts per severity and a **Start here** list of the high
findings, each with a one-line remedy and a link to the right portal blade. Then one section per
rule and severity; group findings fold the membership path under "through N groups". The
appendix lists the assignment ids for auditors and the scopes the tool requested.

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
- **A group shows as "not onboarded" although it is** — the account cannot read that group's
  PIM data; for role-assignable groups that needs Global Reader or Privileged Role Administrator
  at directory scope.
- **Throttled** — the tool retries `429` replies honouring `Retry-After`; `--verbose` shows each
  request.
