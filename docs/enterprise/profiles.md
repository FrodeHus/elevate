# Publishing profiles to your organization

A *profile* in Elevate is a saved set of roles that a user activates in one click: "Prod incident"
might ask for Contributor on the production subscription, the Security Reader directory role and
membership of the on-call group, all with one justification. Users build their own; you can publish
a set that everybody gets.

**What you end up with:** the profiles you author appear in every user's panel, ready to run and
bindable to the global shortcut, marked as published by your organization and not editable by the
user. Nobody has to be told which roles to pick for an incident.

**Prerequisites**

- Managed configuration already reaching the machines — one of
  [macos-jamf.md](macos-jamf.md), [macos-intune.md](macos-intune.md),
  [windows-intune.md](windows-intune.md), [windows-group-policy.md](windows-group-policy.md) or
  [cli.md](cli.md). Published profiles are the `ManagedProfiles` and `ManagedProfilesUrl` keys of
  that same payload.
- The tenants (or their verified domains), and the role and group names, you want to name.
- Optional: a web server that can serve one JSON file over https, if you want to change the set
  without touching policy.

## The format

One JSON document. A user's profile binds roles to *their* account, which you cannot know, so a
published profile names roles by **tenant and role** instead, and Elevate resolves them against
whichever accounts are signed in on the machine.

```json
{
  "version": 1,
  "profiles": [
    {
      "id": "prod-incident",
      "name": "Prod incident",
      "reason": "Incident response",
      "pinned": true,
      "roles": [
        { "kind": "azureResource", "tenant": "contoso.com",
          "scope": "/subscriptions/00000000-0000-0000-0000-000000000001",
          "role": "Contributor", "duration": "PT2H" },
        { "kind": "entraDirectory", "tenant": "contoso.com",
          "role": "Security Reader" },
        { "kind": "group", "tenant": "contoso.com",
          "group": "SRE on-call", "access": "member", "duration": "PT4H" }
      ]
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `version` | `1`. A higher number is rejected with a warning, so an older Elevate ignores a document it does not understand rather than guessing. |
| `id` | A stable slug, `[a-z0-9-]`, up to 64 characters, unique in the set. The profile's internal identifier is derived from it, so a user's shortcut binding survives a reinstall. Do not recycle a slug for a different profile. |
| `name` | Required. What the user sees. |
| `reason` | Optional. Prefills the justification on the run sheet. |
| `pinned` | Optional, default `false`. Shows the profile as a chip in the panel. Published chips come first and do **not** count against the four pins a user gets for their own profiles. |
| `roles[]` | One entry per role, see below. |

Every role has a `kind` and a `tenant` (a tenant id or a verified domain), and may have a
`duration` — an ISO 8601 duration such as `PT2H`, used as the proposed duration and capped by the
PIM policy as usual. The rest depends on the kind:

- **`entraDirectory`** — a directory role. `role` is its display name or its role template id.
  The directory scope defaults to `/`.

  ```json
  { "kind": "entraDirectory", "tenant": "contoso.com", "role": "Security Reader" }
  ```

- **`azureResource`** — an Azure resource role. `role` is the role's display name or its role
  definition GUID; `scope` is the ARM scope, compared case-insensitively — a subscription, a
  resource group, or a single resource.

  ```json
  { "kind": "azureResource", "tenant": "contoso.com",
    "scope": "/subscriptions/…/resourceGroups/prod-rg",
    "role": "Contributor", "duration": "PT2H" }
  ```

- **`group`** — a PIM for Groups membership. `group` is the group's display name or its object id;
  `access` is `member` (the default) or `owner`.

  ```json
  { "kind": "group", "tenant": "contoso.com", "group": "SRE on-call", "access": "owner" }
  ```

The document is validated as a whole, not profile by profile: the first shape error rejects the
entire set — inline or fetched — and leaves one warning naming the profile and field
(`ManagedProfiles: …` for the inline document, `ManagedProfilesUrl: …` for a fetched one). For the
inline document that means no published profiles at all until the mistake is fixed; for the fetched
one, the last successfully fetched copy stays in use. Validate before you publish: run
`python3 scripts/validate-enterprise-kit.py` against a kit copy of the document, or
`elevate config managed --file <path>` for a quick dry run of any JSON file. The full key reference
is in [keys.md](keys.md#managedprofiles).

## Start from a profile someone already built

The quickest way to author a set is to have someone build the profile in the app, then export it
from the CLI in exactly this format:

```bash
elevate profiles export "Prod incident" > prod-incident.json
```

The export names roles by tenant and role rather than by account, so the result is ready to publish
after you check the tenant entries and the slug. `elevate profiles export` refuses a profile that
is already published — export its source instead, since you have it.

## Publish it

Two ways, and they can be combined.

### Inline, with `ManagedProfiles`

Put the whole document in the key. It travels with the rest of your policy, so it needs no server
and works on a machine that has never been online:

- **macOS**: the document as one string in the `ManagedProfiles` key of the mobileconfig or the
  preference-file plist.
- **Windows**: the **Managed profiles (inline)** policy, a multi-line text box; in the registry a
  `REG_MULTI_SZ` (a single-line `REG_SZ` works too).
- **CLI on macOS and Linux**: in `/etc/elevate/managed.json`, either the object itself or a string
  holding the same JSON.

Changing the set means changing policy, which is fine when it changes twice a year.

### By URL, with `ManagedProfilesUrl`

Host the document and point Elevate at it:

```json
{ "ManagedProfilesUrl": "https://elevate.contoso.com/profiles.json" }
```

- The URL must be `https://`. An `http://` URL is ignored with a warning.
- It is fetched at startup when the cached copy is older than 24 hours, and once a day after, with
  a 1 MB cap. (In the CLI the fetch is bounded at 5 seconds, so a slow endpoint never stalls a
  command.)
- The last body that fetched successfully is cached as `managed-profiles.json` next to `state.json`
  in the app's or the CLI's data directory, and stands in whenever a fetch fails — the failure is a
  warning, not an outage.
- The document needs no authentication and carries no secrets: it is names of tenants, roles and
  groups. Serve it as `application/json`.

### Both

`ManagedProfiles` and the fetched document are **merged by `id`, the fetched entry winning**. A
common pattern is to ship a minimal safety net inline and keep the day-to-day set on the server.

## What users see

- The profile is listed in the panel and in the Profiles window with a building marker and the
  caption **Published by your organization**.
- It can be **run**, and bound to the global shortcut.
- It cannot be renamed, edited, pinned, unpinned or deleted; the pin state is yours. In the CLI,
  `profiles rename`, `profiles delete` and saving over the name are refused with
  `'<name>' is published by your organization and cannot be changed.`, and `elevate profiles` shows
  `managed` in the `Source` column.
- On the run sheet, a role the signed-in account is not eligible for renders **not eligible ·
  skipped**: the rest of the profile still runs. Nothing fails because one person lacks one role.
- Durations you set are proposed, and the user can still shorten them on the run.
- Running a published profile does not change it: per-role reasons and durations are remembered as
  usual, but nothing is written back into the profile.

## Updating a set

Edit the document (or the policy), and:

- an inline change reaches a machine when the policy does, and applies at the app's **next launch**;
- a URL change reaches it at the **next daily fetch**, or the next launch if the cached copy is
  already a day old.

Changing a profile's `name` or roles is safe. Changing its `id` makes it a new profile: the old one
disappears and any shortcut bound to it is lost, so keep slugs stable.

## When a role does not appear

Two different outcomes, and Elevate distinguishes them:

- **The tenant is tracked, but the account has no such eligible role.** The role stays in the
  profile and plans as **not eligible · skipped**. That is the normal case for a profile published
  to a mixed audience.
- **No signed-in account tracks that tenant at all.** The role is dropped and the resolution warns,
  naming the profile and the tenant — for example `Prod incident: no account in tenant
  fabrikam.com`. The warning shows in Settings' managed section, in Diagnostics, and in
  `elevate config managed`. Pinning the tenant with `PinnedTenants` is usually the fix, so every
  account that can reach it tracks it automatically.

A tenant named by a domain has to be resolved to a tenant id over the network first; until that
happens (offline, say) its roles are left alone rather than dropped. A domain that cannot be
resolved at all is listed as a warning.

More symptoms and their causes: [troubleshooting.md](troubleshooting.md).
