# Elevate {{VERSION}} enterprise kit

Templates for rolling Elevate out to a fleet with Intune, Jamf Pro, Group
Policy or a script. Nothing here is company-specific until you fill it in: one
signed build per platform reads the values you push.

A managed value wins over the user's stored value, which wins over the default.
Locking is per key — a key you do not push leaves that choice to the user. In
the app a managed setting renders disabled with a "Managed by your
organization" caption, and Diagnostics lists the keys in effect (never the
values).

The seven keys, their types and their syntax in each format are documented in
`docs/enterprise/keys.md` in the repository; a copy named `keys.md` is added
next to this README at release time. The step-by-step how-tos live in
`docs/enterprise/` in the repository.

## What is in here

- `windows/Elevate.admx` — the administrative template. Policies under
  Reothor > Elevate write `Software\Policies\Reothor\Elevate` in HKLM
  (Computer Configuration) or HKCU (User Configuration). Copy it to the central
  store, or import it into Intune under Devices > Configuration > Import ADMX.
- `windows/en-US/Elevate.adml` — the English strings for the template. It must
  sit in a language subfolder next to the ADMX.
- `windows/example.reg` — the worked example as a `.reg` file writing the HKLM
  policy key, for a script-driven rollout or a quick local test. Same file as
  `example/example.reg`.
- `macos/no.reothor.elevate.mobileconfig` — a `com.apple.ManagedClient.preferences`
  profile forcing all seven keys for the domain `no.reothor.elevate`. Replace
  the placeholders, delete the keys you do not want to force, generate fresh
  `PayloadUUID`s, then upload it to Jamf Pro or as an Intune macOS custom
  profile.
- `macos/intune-preference-file.plist` — the same keys as a flat plist, for an
  Intune "Preference file" profile with the preference domain
  `no.reothor.elevate`.
- `macos/jamf-manifest.json` — a Jamf Pro custom schema for Application &
  Custom Settings, so the same keys can be filled in through Jamf's form
  instead of an uploaded profile.
- `cli/managed.json` — the template for `/etc/elevate/managed.json`, which the
  CLI reads on macOS and Linux. The file must not be writable by others, and
  its directory must not be writable by others, otherwise it is ignored with a
  warning. Deploying it root-owned with mode 0644 is the recommended practice.
- `example/` — one company's finished configuration in every format
  (mobileconfig, `managed.json`, `profiles.json` and `example.reg`), with a
  README explaining what it pins.

## Placeholders to replace

| Placeholder | Meaning |
|---|---|
| `00000000-0000-0000-0000-000000000000` | The Application (client) id of your Entra app registration. `c9011cc5-7422-4630-a432-73ff4df5834e` is the project's optional shared registration — usable, but it has no SLA and you do not control it; see `docs/shared-app-registration.md` |
| `contoso.com` | Your tenant's verified domain, or its tenant id |
| `https://example.com/elevate/profiles.json` | Where you host the managed profile set, if you host one |
| `Contoso` / `com.contoso.…` | Your organization's name and payload identifiers |

## Verify a rollout

On a machine the policy reached: the managed settings render disabled with the
caption, Diagnostics shows a "Managed configuration:" section listing the keys
in effect and any warnings, and `elevate config` says where each effective
value came from. `elevate config managed --file <path>` checks a file before
you deploy it.

`scripts/validate-enterprise-kit.py` in the repository checks these templates
against `docs/enterprise/keys.md` on every change; run it after editing your
own copy to catch a typo before your fleet does.
