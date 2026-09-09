# Managed configuration: troubleshooting

For the administrator who pushed a policy and for the person on the support desk taking the call.
The user-facing guide to the app's own warnings is
[docs/troubleshooting.md](../troubleshooting.md).

## First: what does the machine think is managed?

Three places answer that, in increasing detail.

1. **Settings**, at the bottom: a **Managed by your organization** section, present only when
   something is managed. It lists the keys in effect with their values as you wrote them (tenant
   entries also show the tenant id they resolved to), the source they came from, and any warnings
   in orange.
2. **Settings → Copy diagnostics**: the report has a `Managed configuration:` section with the
   source, the key names and the warnings. It never contains the values, so it stays safe to paste
   into an issue.

   ```
   Managed configuration:
     Source: managed preferences
     Keys: ClientId, AllowedTenants
     Warnings:
       AllowedSignInMethods: unknown method 'saml' ignored
   ```

3. **The CLI**: `elevate config` for the effective values with a `Source` column
   (`managed` / `user` / `default`), and `elevate config managed` for the origin, the keys and the
   warnings — `--json` for a script.

The source line tells you which delivery mechanism won: `managed preferences` (a macOS
configuration profile), `Windows policy` (HKLM or HKCU), or the path of the JSON file
(`/etc/elevate/managed.json`).

## Nothing is managed at all

The Settings section is missing, Diagnostics says `None`, `elevate config managed` says
`No managed configuration.`

- **The app was running when the policy arrived.** Values are read at launch. Quit Elevate
  completely and start it again.
- **macOS: the value is not forced.** Elevate reads only values a configuration profile *forces*
  for the domain `no.reothor.elevate`; something written into the user's own preferences of the
  same domain is deliberately ignored, so a user cannot fake a policy. Check the profile arrived:

  ```bash
  sudo profiles show -type configuration | grep -A3 no.reothor.elevate
  defaults read /Library/Managed\ Preferences/$USER/no.reothor.elevate
  ```

  Nothing from the second command means nothing is forced.
- **macOS: the wrong preference domain.** It is `no.reothor.elevate`, exactly.
- **Windows: the policy did not land.** `gpupdate /force`, then

  ```
  reg query "HKLM\SOFTWARE\Policies\Reothor\Elevate" /s
  reg query "HKCU\SOFTWARE\Policies\Reothor\Elevate" /s
  ```

  Note the path is under `SOFTWARE\Policies`, not `SOFTWARE\Reothor`.
- **CLI on macOS or Linux: the file is not where it is read from.** It is `/etc/elevate/managed.json`
  and nowhere else. A missing file is silent by design.
- **CLI on macOS: you deployed the configuration profile only.** The CLI does not read the app's
  managed preferences; it needs `/etc/elevate/managed.json` as well. See [cli.md](cli.md).
- **The keys are misspelled.** They are case-sensitive in plists and JSON (`ClientId`, not
  `clientID`). The registry is case-insensitive. Anything Elevate does not recognise is ignored
  without a warning, because unknown keys are not necessarily yours.

## `/etc/elevate/managed.json` is ignored

`elevate config managed` says:

```
/etc/elevate/managed.json: ignored because it is not owned by root or is writable by others
```

The file is trusted only when a user cannot rewrite it — otherwise someone could grant themselves a
policy and have Diagnostics report it as your organization's. Fix the modes:

```bash
sudo chown root /etc/elevate/managed.json
sudo chmod 644 /etc/elevate/managed.json
sudo chmod 755 /etc/elevate            # the directory must not be world-writable either
```

Two related messages: `ignored because it could not be parsed as JSON` (a trailing comma, a
smart quote from a word processor) and `ignored because its contents are not a JSON object` (the
file is a list, or the profile document alone rather than an object of keys). Check a draft before
deploying it:

```bash
elevate config managed --file ./managed.json
```

That dry run skips the ownership check — it says so — so it tells you about the *contents*; run
`elevate config managed` with no `--file` on a real machine to confirm the ownership rules too.

## A single key did not take effect

Invalid values are dropped key by key, never the whole payload, and each drop leaves a warning:

| Warning | Cause |
|---|---|
| `ClientId: '…' is not a valid GUID` | Not a GUID, or the all-zero GUID left in from the template. Users keep the client-id field. |
| `AllowedSignInMethods: unknown method '…' ignored` | A name outside `ownApp`, `azureCLI`, `azurePowerShell`, `custom`. Matching is case-insensitive; the other names still apply. |
| `AllowedTenants: could not resolve '…'` | A domain that has no Entra tenant, or that could not be looked up. |
| `PinnedTenants: could not resolve '…'` | The same, for a pinned entry. That entry pins nothing. |
| `ManagedProfilesUrl: only https URLs are accepted` | An `http://` or other scheme. |
| `ManagedProfilesUrl: …` (a fetch error) | The document could not be fetched. The last cached copy stands in. |
| `ManagedProfiles: …` | The inline document failed to parse; the message names the profile and field. |
| `<profile>: could not resolve tenant <name>` | A domain named by a published profile that could not be looked up; that role waits rather than being dropped. |
| `<profile>: no account in tenant <name>` | No signed-in account tracks that tenant, so those roles are dropped. |

An `AllowedTenants` list with any unresolved entry applies **no** restriction at all: Elevate will
not lock people out on a guess about half a list. Pinning is per entry, so the entries that did
resolve still pin.

Tenant lookups need the network. On a machine that starts offline nothing is resolved and no tenant
restriction is applied; Elevate runs the lookup again as soon as the network comes back.

## A user asks why a field is greyed out

Because you pushed that key. The caption under it says **Managed by your organization**, the
Settings section at the bottom of the window lists what is in effect, and the app refuses writes to
it — including through the CLI, where `elevate config set client-id …` answers `client-id is
managed by your organization.`

To hand a setting back, remove that key from the payload (do not set it to an empty value) and let
the policy re-apply; the app returns to the user's stored value, or the default if they never set
one.

## A sign-in method is missing from Add account

`AllowedSignInMethods` does not list it. What users see:

- Add account shows only the permitted methods — the radio group stays even when one method
  remains, so the limitation is visible rather than mysterious.
- An account added earlier with a method that is no longer permitted keeps its roles and carries the
  caption **Sign-in method no longer permitted by your organization**; its Sign in button is
  disabled. Sign it out and add it again with a permitted method.
- The CLI refuses `elevate login --method <name>` with *"The sign-in method '…' is not permitted by
  your organization"* and names the ones that are; `elevate accounts` flags such an account
  `not permitted`.

Remember that the Azure CLI and Azure PowerShell methods cover Azure resource roles only. Allowing
`ownApp` alone is the usual choice when you have your own registration.

## A tenant is missing, or cannot be removed

- **Greyed out in Discover tenants with "Not permitted by your organization"**: it is not in
  `AllowedTenants`. It is shown rather than hidden so the user knows why it is not there. Manually
  adding it is refused with the same reason.
- **A tenant vanished after an update**: a tenant that is neither an account's home tenant nor on
  the allow-list is removed at launch, so a policy that arrives later cleans up. An account's own
  home tenant is always allowed — that is where the account lives.
- **"Remove tenant" is missing, replaced by "Pinned by your organization"**: it is in
  `PinnedTenants`. Pinned tenants count as allowed even when they are not in `AllowedTenants`, and
  are tracked automatically for every account that can reach them. The CLI refuses
  `elevate tenants remove` with the same reason.
- **A pinned tenant shows a discovery error**: that account cannot reach it. It stays listed,
  because the policy asked for it.

## A published profile cannot be edited

It is published by your organization, and the marker and the caption **Published by your
organization** say so. Run and shortcut binding work; rename, edit, add or remove roles, pin,
unpin and delete do not, on any platform. The CLI answers
`'<name>' is published by your organization and cannot be changed.`

A user who wants a variant can build their own profile with the same roles; published profiles do
not consume any of the four pins they get for theirs.

## A role in a published profile shows "not eligible · skipped"

The signed-in account is not eligible for that role in that tenant. That is by design for a set
published to a mixed audience: the rest of the profile still runs. If *nobody* is eligible, check
the role or group name and the tenant against what PIM actually calls it — names are matched
case-insensitively, and an id (role template id, role definition GUID, group object id) is matched
too and is unambiguous.

If the roles are missing entirely rather than skipped, the tenant is not tracked by any signed-in
account; see [profiles.md](profiles.md#when-a-role-does-not-appear).

## A published profile did not update

- Inline sets change when the policy changes, and apply at the app's next launch.
- URL sets are fetched at startup when the cached copy is older than 24 hours, and once a day after.
  Between fetches the cached copy — `managed-profiles.json`, next to `state.json` in the data
  directory — is what you see; it is also what stands in when a fetch fails.
- Inline and fetched documents merge by `id`, the fetched one winning. A profile that seems to
  ignore your server change probably has a different `id` there than inline.
- Changing a profile's `id` replaces it: the old one disappears and a shortcut bound to it is lost.

## The update check still runs

`DisableUpdateCheck` must be a real boolean: `<true/>` in a plist, `true` in JSON, `REG_DWORD` `1`
in the registry. When it is in effect, the Settings button is replaced by **Updates are managed by
your organization**, no banner appears, and a forced check does nothing.

## Reporting a problem

Ask the user for **Settings → Copy diagnostics** (it holds the managed source, the key names and
the warnings, never the values, tokens or justifications) or, on a machine with the CLI,
`elevate config managed --json`. Issues go to
<https://github.com/FrodeHus/elevate/issues>.
