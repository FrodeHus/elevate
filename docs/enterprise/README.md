# Rolling Elevate out to a fleet

Elevate is a menu bar app (macOS), a tray app (Windows) and a command-line tool that let people
activate the Microsoft Entra, Azure resource and PIM for Groups roles they are eligible for. These
pages are for the administrator who deploys it: how to push your organization's values to every
machine so nobody has to be told a client id, and how to check that the values arrived.

You do not need to know Elevate to follow them. Each page says what you end up with, what you need
before you start, the steps with the exact console paths, and how to verify.

## How managed configuration works

- **A managed value wins over the user's stored value, which wins over the built-in default.**
- **Locking is per key.** A key you push is locked: the app renders that setting disabled with a
  "Managed by your organization" caption and refuses writes to it. A key you leave out leaves the
  choice to the user. There is no soft "suggested default" tier.
- **No company build.** One signed build per platform reads whatever you push; the values travel as
  a configuration profile, a Group Policy setting or a JSON file. Nothing here is compiled in.
- **Values apply at launch**, and again when the network comes back (the allowed and pinned tenants
  and the roles in published profiles are looked up online). A value you push in the middle of a
  session takes effect the next time the app starts.
- **Invalid values are ignored key by key**, never the whole payload, and every rejection becomes a
  warning that Diagnostics and `elevate config managed` show.

There are seven keys — the client id, the update check, the permitted sign-in methods, the allowed
and pinned tenants, and the published profile set inline or by URL. They are documented once, with
their types, allowed values and syntax in every format, in [keys.md](keys.md).

## Where the values come from

| Platform | Source |
|---|---|
| macOS app | Managed preferences for the domain `no.reothor.elevate`. Only *forced* values are read: a value the user writes into the same domain is ignored. |
| Windows app, and the CLI on Windows | `HKLM\SOFTWARE\Policies\Reothor\Elevate`, then `HKCU\SOFTWARE\Policies\Reothor\Elevate`; per key, the machine value wins. |
| CLI on macOS and Linux | `/etc/elevate/managed.json`, which must not be writable by others and must sit in a directory that is not writable by others. |

## Which page to read

| You manage | Read |
|---|---|
| Macs with Jamf Pro | [macos-jamf.md](macos-jamf.md) |
| Macs with Intune | [macos-intune.md](macos-intune.md) |
| Windows PCs with Intune | [windows-intune.md](windows-intune.md) |
| Windows PCs with Active Directory Group Policy | [windows-group-policy.md](windows-group-policy.md) |
| The CLI on servers, containers or build agents | [cli.md](cli.md) |
| Publishing role-set profiles to everyone | [profiles.md](profiles.md) |
| Something did not arrive, or a user asks why a field is greyed out | [troubleshooting.md](troubleshooting.md) |

The key reference behind all of them: [keys.md](keys.md).

## What you download

Both come from the [latest release](https://github.com/FrodeHus/elevate/releases/latest):

- `Elevate-<version>.pkg` — the macOS app as an installer package, for silent deployment by Jamf or
  Intune. It carries no company values; it is the same app as the DMG. It is signed and notarized
  when the release was built with the installer signing secrets in place, and unsigned otherwise —
  the release notes say which. Intune requires a signed pkg; Jamf can deploy either, though an
  unsigned one still has to satisfy Gatekeeper on the Mac.
- `Elevate-enterprise-kit-<version>.zip` — the templates: the ADMX and ADML for Group Policy and
  Intune, a mobileconfig, an Intune preference-file plist, a Jamf custom schema, a `managed.json`,
  a copy of `keys.md`, and a worked example of one company's finished configuration in every
  format. The same files live in [`enterprise/`](../../enterprise/) in the repository.

Windows is installed from the per-user MSI as before; there is no separate enterprise build. The
CLI is a single binary from the same release.

## How you verify, whatever you used

Every page ends with these. On a machine the policy reached:

1. **In the app**, open Settings. A managed setting is disabled and captioned "Managed by your
   organization"; with `DisableUpdateCheck` pushed, the update button reads "Updates are managed by
   your organization". A "Managed by your organization" section at the bottom of Settings lists the
   keys in effect, their values as you wrote them, the source, and any warnings.
2. **In Diagnostics** (Settings → Copy diagnostics), a "Managed configuration:" section names the
   source and the keys in effect, plus warnings. It lists key *names* only — never values — so the
   report stays safe to paste into an issue.
3. **In the CLI**, `elevate config` shows a `Source` column reading `managed`, `user` or `default`
   per row, and `elevate config managed` prints the origin, the keys and the warnings.

```bash
elevate config
elevate config managed
elevate config managed --json
```

Before you deploy a `managed.json`, `elevate config managed --file ./managed.json` parses that file
as a dry run and tells you what Elevate would take from it.

## Checking a template you edited

`scripts/validate-enterprise-kit.py` in the repository checks the ADMX, ADML, plists, Jamf schema
and `managed.json` against the key table in `keys.md`, and validates the example profile set. CI
runs it on every change under `enterprise/` and `docs/enterprise/`; run it against your own copy to
catch a typo before your fleet does:

```bash
python3 scripts/validate-enterprise-kit.py
```
