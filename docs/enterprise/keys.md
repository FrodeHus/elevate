# Managed configuration keys

Elevate reads seven organization-managed keys. A managed value wins over the
user's stored value, which wins over the built-in default; a key you do not
push leaves the choice to the user. Values are locked: the app shows the
setting disabled with a "Managed by your organization" caption and refuses
writes to it.

Names are case-sensitive in plists and JSON; the Windows registry is
case-insensitive. Invalid values are ignored key by key, never the whole
payload, and each rejection is listed as a warning in Diagnostics and in
`elevate config managed`.

Where the values come from:

- **macOS app** — managed preferences for the domain `no.reothor.elevate`
  (Jamf configuration profile, Intune custom profile or Preference file, or a
  plist under `/Library/Managed Preferences/<user>/` for a local test). Only
  forced values are read; a key the user could have written is ignored.
- **Windows app and CLI on Windows** — `HKLM\SOFTWARE\Policies\Reothor\Elevate`,
  then `HKCU\SOFTWARE\Policies\Reothor\Elevate`; the machine value wins per key.
- **CLI on macOS and Linux** — `/etc/elevate/managed.json`. The file is trusted
  only when neither it nor its directory is writable by others; otherwise it is
  ignored with a warning. (Advice, not a checked rule: deploy it root-owned with
  mode `0644` in a `0755` directory.)

## The keys

| Key | Type | Allowed values | Platforms | Stage | Example |
|---|---|---|---|---|---|
| `ClientId` | string | Application (client) id of your Entra app registration, a GUID | macOS, Windows, CLI | 1 | `11111111-2222-3333-4444-555555555555` |
| `DisableUpdateCheck` | boolean | `true` disables the daily GitHub releases check and the update UI; `false` leaves it on | macOS, Windows, CLI | 1 | `true` |
| `AllowedSignInMethods` | list of strings | Any of `ownApp`, `azureCLI`, `azurePowerShell`, `custom` (Other app (browser sign-in)); absent or empty means all | macOS, Windows, CLI | 2 | `ownApp` |
| `AllowedTenants` | list of strings | Tenant ids or verified domains; absent or empty means no restriction | macOS, Windows, CLI | 2 | `contoso.com` |
| `PinnedTenants` | list of strings | Tenant ids or verified domains tracked for every account that can reach them | macOS, Windows, CLI | 2 | `contoso.com` |
| `ManagedProfiles` | string (JSON document) | A profile set in the format below | macOS, Windows, CLI | 3 | `{"version":1,"profiles":[…]}` |
| `ManagedProfilesUrl` | string | An `https://` URL serving the same JSON, fetched once a day | macOS, Windows, CLI | 3 | `https://example.com/elevate/profiles.json` |
| `OrganizationName` | string | Your organization's name, 1-32 characters after trimming | macOS, Windows, CLI | 4 | `Contoso` |
| `OrganizationTitleStyle` | string | `by`, `managedBy` or `none`; absent means `by` | macOS, Windows, CLI | 4 | `managedBy` |
| `OrganizationSupportUrl` | string | An `https://` URL for your help desk | macOS, Windows, CLI | 4 | `https://help.contoso.com` |
| `OrganizationSupportEmail` | string | An email address for your help desk | macOS, Windows, CLI | 4 | `it@contoso.com` |

Anything else in the payload is ignored.

## `ClientId`

The application (client) id of the app registration Elevate signs in with. It
must be a GUID and must not be the all-zero GUID; anything else is rejected
with a warning and the user keeps the choice.

This is normally your own registration, created with
[the app registration guide](../entra-app-registration.md). You *may* push the
project's shared registration, `c9011cc5-7422-4630-a432-73ff4df5834e`, but
weigh the caveat first: it is optional, has **no SLA**, and its scopes,
redirect URIs and continued existence are the maintainer's to change — pushing
it locks your fleet to an app you do not control. See
[The shared Elevate app](../shared-app-registration.md), especially its known
risks. The app reports which one is in effect as
`Client id: shared Elevate app` or `own registration` in Diagnostics, never the
id itself.

Plist (macOS):

```xml
<key>ClientId</key>
<string>11111111-2222-3333-4444-555555555555</string>
```

Registry (Windows), `REG_SZ`:

```
[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Reothor\Elevate]
"ClientId"="11111111-2222-3333-4444-555555555555"
```

JSON (`/etc/elevate/managed.json`):

```json
{ "ClientId": "11111111-2222-3333-4444-555555555555" }
```

## `DisableUpdateCheck`

`true` stops the daily GitHub releases check, replaces "Check for updates"
with a caption and suppresses the update banner. Push it when your fleet is
updated by Intune or Jamf.

Plist (macOS):

```xml
<key>DisableUpdateCheck</key>
<true/>
```

Registry (Windows), `REG_DWORD` (`1` disables, `0` leaves the check on):

```
[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Reothor\Elevate]
"DisableUpdateCheck"=dword:00000001
```

JSON:

```json
{ "DisableUpdateCheck": true }
```

## `AllowedSignInMethods`

Limits which sign-in methods users may add. Names are matched
case-insensitively against `ownApp`, `azureCLI`, `azurePowerShell` and
`custom` (Other app (browser sign-in)); an unknown name is dropped with a
warning. An absent or empty list means every method is available. Accounts
added earlier with a method that is no longer allowed keep working, with a
caption.

Plist (macOS) — an array, or a single comma-separated string:

```xml
<key>AllowedSignInMethods</key>
<array>
  <string>ownApp</string>
</array>
```

Registry (Windows) — one value per entry under a subkey of the same name
(the shape Group Policy list elements write); a `REG_MULTI_SZ` of the same
name is accepted too, for scripts:

```
[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Reothor\Elevate\AllowedSignInMethods]
"1"="ownApp"
```

JSON:

```json
{ "AllowedSignInMethods": ["ownApp"] }
```

## `AllowedTenants`

Tenant ids or verified domains a user may add or discover. Domains are
resolved to tenant ids at startup; an entry that cannot be resolved blocks
nothing and is listed as a warning. An account's home tenant is always
allowed. Entries are trimmed, lower-cased and de-duplicated.

Plist (macOS):

```xml
<key>AllowedTenants</key>
<array>
  <string>contoso.com</string>
  <string>fabrikam.com</string>
</array>
```

Registry (Windows):

```
[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Reothor\Elevate\AllowedTenants]
"1"="contoso.com"
"2"="fabrikam.com"
```

JSON:

```json
{ "AllowedTenants": ["contoso.com", "fabrikam.com"] }
```

## `PinnedTenants`

Tenant ids or verified domains that are tracked automatically for every
account that can reach them, so a new hire sees the company's tenants without
adding them by hand. Same value shape as `AllowedTenants`.

Plist (macOS):

```xml
<key>PinnedTenants</key>
<array>
  <string>contoso.com</string>
  <string>fabrikam.com</string>
</array>
```

Registry (Windows):

```
[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Reothor\Elevate\PinnedTenants]
"1"="contoso.com"
"2"="fabrikam.com"
```

JSON:

```json
{ "PinnedTenants": ["contoso.com", "fabrikam.com"] }
```

## `ManagedProfiles`

A profile set published inline. Profiles it contains are listed, runnable and
bindable to the hot key, but cannot be edited, renamed, pinned or deleted by
the user. Roles the signed-in account is not eligible for plan as skipped.

The document is JSON:

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

- `version` is `1`; a higher version is rejected with a warning.
- `id` is a stable slug matching `[a-z0-9-]{1,64}`, unique in the set. The
  profile's internal id is derived from it, so a hot-key binding survives a
  reinstall.
- `name` is required. `reason` prefills the run sheet's justification;
  `pinned` (default `false`) shows the profile as a chip in the panel.
- `roles[]`: `kind` is `entraDirectory`, `azureResource` or `group`; `tenant`
  is a tenant id or verified domain; `duration` (optional) is an ISO 8601
  duration such as `PT2H`. `entraDirectory` requires `role`; `azureResource`
  requires `role` and `scope`; `group` requires `group` and takes `access`
  (`member`, the default, or `owner`).

Plist (macOS) — the document as one string:

```xml
<key>ManagedProfiles</key>
<string>{"version":1,"profiles":[{"id":"prod-incident","name":"Prod incident","roles":[{"kind":"entraDirectory","tenant":"contoso.com","role":"Security Reader"}]}]}</string>
```

Registry (Windows) — `REG_MULTI_SZ` under
`HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Reothor\Elevate`, the document's lines
joined with newlines (a single-line `REG_SZ` works too). A `.reg` file has to
spell a multi-string as `hex(7):` bytes, so write it with `reg add` instead, or
copy the encoded form from `enterprise/example/example.reg`:

```
reg add "HKLM\SOFTWARE\Policies\Reothor\Elevate" /v ManagedProfiles ^
  /t REG_MULTI_SZ /d "{\"version\":1,\"profiles\":[]}" /f
```

JSON — the object itself, or a string holding it:

```json
{ "ManagedProfiles": { "version": 1, "profiles": [] } }
```

## `ManagedProfilesUrl`

An `https://` URL serving the same document. It is fetched at startup when the
cached copy is older than 24 hours and once a day after, with a 1 MB cap; the
last successful body is kept and used when a fetch fails. `http://` URLs are
rejected with a warning. Profiles from the URL and from `ManagedProfiles` are
merged by `id`, the fetched entry winning.

Plist (macOS):

```xml
<key>ManagedProfilesUrl</key>
<string>https://example.com/elevate/profiles.json</string>
```

Registry (Windows), `REG_SZ`:

```
[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Reothor\Elevate]
"ManagedProfilesUrl"="https://example.com/elevate/profiles.json"
```

JSON:

```json
{ "ManagedProfilesUrl": "https://example.com/elevate/profiles.json" }
```

## `OrganizationName`

Your organization's name, shown beside Elevate's own. The app reads as
"Elevate", with "by Contoso" underneath it in the panel header, "Provided by
Contoso." on the first-run screen, and an "Organization" section in Settings.

**Elevate's own name is never replaced.** This is co-branding: your name is
added, not substituted. There is no key that renames or re-icons the app.

The name is trimmed and must be 1-32 characters. A longer one is rejected with
a warning naming its length — it is never silently truncated. The 32-character
limit is what the panel header can show without clipping.

**This key gates the other three.** Without it `OrganizationTitleStyle`,
`OrganizationSupportUrl` and `OrganizationSupportEmail` are ignored, each with
its own warning: a style with nothing to style, and an unattributed "Get help"
link, are both worse than no branding at all.

Plist (macOS):

```xml
<key>OrganizationName</key>
<string>Contoso</string>
```

Registry (Windows), `REG_SZ`:

```
[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Reothor\Elevate]
"OrganizationName"="Contoso"
```

JSON:

```json
{ "OrganizationName": "Contoso" }
```

## `OrganizationTitleStyle`

How the name is phrased beside Elevate's own. Matched case-insensitively
against three values; anything else is rejected with a warning.

- **`by`** (the default, used when the key is absent) - the header reads
  "Elevate" with "by Contoso" underneath.
- **`managedBy`** - the header reads "Elevate" with "Managed by Contoso"
  underneath; the more formal phrasing.
- **`none`** - no caption in the header at all, while Settings, the first-run
  line and the support contact all stay.

Push `none` when you want the help desk contact without changing the
everyday appearance.

Plist (macOS):

```xml
<key>OrganizationTitleStyle</key>
<string>managedBy</string>
```

Registry (Windows), `REG_SZ`:

```
[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Reothor\Elevate]
"OrganizationTitleStyle"="managedBy"
```

JSON:

```json
{ "OrganizationTitleStyle": "managedBy" }
```

## `OrganizationSupportUrl`

Your help desk, as an `https://` URL. It becomes a "Get help from Contoso IT"
link on the first-run screen and in Settings, and the CLI appends
`Need help? Contoso IT — https://help.contoso.com/` to sign-in and activation
failures — the moment a user actually needs to know who to call.

`http://` URLs and anything unparseable are rejected with a warning. Requires
`OrganizationName`.

Plist (macOS):

```xml
<key>OrganizationSupportUrl</key>
<string>https://help.contoso.com</string>
```

Registry (Windows), `REG_SZ`:

```
[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Reothor\Elevate]
"OrganizationSupportUrl"="https://help.contoso.com"
```

JSON:

```json
{ "OrganizationSupportUrl": "https://help.contoso.com" }
```

## `OrganizationSupportEmail`

An email address for your help desk, offered as a `mailto:` link wherever the
support URL would appear. When both are set the URL wins for the link and the
CLI contact line, and both are listed in Settings and diagnostics.

Must contain `@` and no whitespace; anything else is rejected with a warning.
Requires `OrganizationName`.

Plist (macOS):

```xml
<key>OrganizationSupportEmail</key>
<string>it@contoso.com</string>
```

Registry (Windows), `REG_SZ`:

```
[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Reothor\Elevate]
"OrganizationSupportEmail"="it@contoso.com"
```

JSON:

```json
{ "OrganizationSupportEmail": "it@contoso.com" }
```
