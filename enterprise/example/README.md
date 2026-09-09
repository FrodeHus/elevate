# Worked example — Contoso

One company's finished Elevate configuration, in every format the kit
supports. The four files describe the same policy; deploy the one that matches
how you manage the machine.

- `no.reothor.elevate.mobileconfig` — macOS, uploaded to Jamf Pro or as an
  Intune custom profile.
- `managed.json` — `/etc/elevate/managed.json` for the CLI on macOS and Linux
  (not writable by others, in a directory that is not writable by others).
- `example.reg` — the HKLM policy key on Windows, for a script or a local test;
  the same values are what the ADMX template writes.
- `profiles.json` — the profile set on its own, as a web server would serve it
  for `ManagedProfilesUrl`.

## What it pins

| Key | Value | Effect |
|---|---|---|
| `ClientId` | `11111111-2222-3333-4444-555555555555` | Users never enter a client id; setup skips that step and the Settings field is disabled. |
| `DisableUpdateCheck` | `true` | No daily GitHub check, no update banner — Contoso ships Elevate through its MDM. |
| `AllowedSignInMethods` | `["ownApp"]` | Only Contoso's own app registration; the Azure CLI, Azure PowerShell and custom-client options are hidden from Add account and from `elevate login --method`. |
| `AllowedTenants` | `["contoso.com", "fabrikam.com"]` | Only the corporate tenant and the subsidiary can be added or discovered. An account's own home tenant is always allowed. |
| `PinnedTenants` | `["contoso.com", "fabrikam.com"]` | Both tenants are tracked automatically for every account that can reach them, so a new hire sees them without adding anything. |
| `ManagedProfiles` | the "Prod incident" profile | Published to every machine: runnable and bindable to the hot key, but not editable, renamable, pinnable or deletable. |
| `ManagedProfilesUrl` | `https://example.com/elevate/profiles.json` | The same document, fetched once a day, so the set can change without touching policy. Entries merge by `id`, the fetched one winning. |

## The profile

"Prod incident" is pinned, prefills "Incident response" as the justification,
and asks for three roles in `contoso.com`:

- Azure `Contributor` on `/subscriptions/00000000-0000-0000-0000-000000000001`
  for two hours,
- the Entra directory role `Security Reader`,
- membership of the group `SRE on-call` for four hours.

Roles are named by tenant and role rather than bound to an account, so the same
document works on every machine. A role the signed-in account is not eligible
for is planned as skipped rather than failing the run, and a tenant no account
tracks drops its roles with a warning.

Both `ManagedProfiles` and `ManagedProfilesUrl` are set here to show the
merge; in practice most organizations pick one.
