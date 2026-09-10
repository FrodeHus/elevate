# Managed configuration for the `elevate` CLI

**What you end up with:** the `elevate` command-line tool on servers, containers, build agents,
Macs and Linux workstations reading your organization's client id and policy, with no per-machine
setup and nothing for the user to type.

**Prerequisites**

- The CLI installed. Macs that get `Elevate-<version>.pkg` (Jamf, Intune) and Windows PCs that get
  the MSI already have it, at `/usr/local/bin/elevate` and in the `cli` folder under the app;
  Linux workstations and servers take the single binary from the release
  ([cli/README.md](../../cli/README.md#install)).
- An Entra app registration for Elevate and its application (client) id
  ([docs/entra-app-registration.md](../entra-app-registration.md) if you need to create one).
- Root (or an administrator) on the machines you configure.
- `Elevate-enterprise-kit-<version>.zip` from the
  [latest release](https://github.com/FrodeHus/elevate/releases/latest), for
  `cli/managed.json` as a starting point.

The CLI reads the same seven keys as the apps ([keys.md](keys.md)), from the source that fits the
platform:

| Platform | Source |
|---|---|
| macOS, Linux | `/etc/elevate/managed.json` |
| Windows | `HKLM\SOFTWARE\Policies\Reothor\Elevate`, then `HKCU\...`; per key, the machine value wins |

On macOS the CLI does **not** read the app's configuration profile: managed preferences belong to
the app's preference domain, and the CLI is a separate program. A Mac that runs both needs the
configuration profile *and* `/etc/elevate/managed.json`. The Jamf script below writes the file.

## `/etc/elevate/managed.json` on macOS and Linux

A JSON object with the keys you want to lock. Start from `cli/managed.json` in the kit and delete
what you do not need — every key you leave in is locked, and a key you leave out leaves the choice
to the user.

```json
{
  "ClientId": "11111111-2222-3333-4444-555555555555",
  "DisableUpdateCheck": true,
  "AllowedSignInMethods": ["ownApp"],
  "AllowedTenants": ["contoso.com", "fabrikam.com"],
  "PinnedTenants": ["contoso.com"]
}
```

`ManagedProfiles` may be the profile-set object itself or a string holding the same JSON; see
[profiles.md](profiles.md).

### Trust rules

The file is trusted only when a user cannot rewrite it: **the file must not be writable by others,
and its directory must not be writable by others.** That is the whole rule Elevate checks —
ownership is not inspected. Otherwise the file is ignored entirely and
`elevate config managed` reports a warning like

```
/etc/elevate/managed.json: ignored because it is writable by others, or its directory is
```

As good practice (advice, not a checked rule), deploy it as root with mode `0644` in a `0755`
directory:

```bash
sudo install -d -m 0755 /etc/elevate
sudo install -m 0644 -o root managed.json /etc/elevate/managed.json
```

A missing file is not an error — it simply means nothing is managed. A file that is not a JSON
object, or that fails to parse, is ignored with its own warning.

### Ansible

```yaml
- name: Create the Elevate policy directory
  ansible.builtin.file:
    path: /etc/elevate
    state: directory
    owner: root
    mode: "0755"

- name: Install the Elevate managed configuration
  ansible.builtin.copy:
    src: files/elevate-managed.json
    dest: /etc/elevate/managed.json
    owner: root
    mode: "0644"
```

Use `ansible.builtin.template` with a `.j2` source instead when the client id or the tenant list
comes from your inventory. Nothing needs to be restarted: the CLI reads the file on each run.

### A Jamf Pro script for Macs

**Settings → Computer Management → Scripts → New**, paste this, then run it from a policy scoped to
the same Macs that get the app. It is idempotent, so a recurring trigger is fine.

```bash
#!/bin/bash
set -euo pipefail

mkdir -p /etc/elevate
cat > /etc/elevate/managed.json <<'EOF'
{
  "ClientId": "11111111-2222-3333-4444-555555555555",
  "DisableUpdateCheck": true,
  "AllowedSignInMethods": ["ownApp"],
  "AllowedTenants": ["contoso.com"],
  "PinnedTenants": ["contoso.com"]
}
EOF
chown root:wheel /etc/elevate /etc/elevate/managed.json
chmod 755 /etc/elevate
chmod 644 /etc/elevate/managed.json
```

The same script works from any other management tool that runs commands as root — Munki, a
Configuration Manager package, or `cloud-init` on a Linux image.

## The registry on Windows

The CLI on Windows reads the same policy keys the tray app does, so a Group Policy or Intune
rollout covers both and there is nothing extra to deploy:
[windows-group-policy.md](windows-group-policy.md), [windows-intune.md](windows-intune.md).

```
reg query "HKLM\SOFTWARE\Policies\Reothor\Elevate" /s
```

## Verify

`elevate config` prints the settings with a `Source` column — `managed`, `user` or `default` per
row — and a note under the table when the client id is managed:

```console
$ elevate config
╭───────────────────┬──────────────────────────────────────┬─────────╮
│ Key               │ Value                                │ Source  │
├───────────────────┼──────────────────────────────────────┼─────────┤
│ data directory    │ /home/ada/.config/elevate-cli        │         │
│ client-id         │ 11111111-2222-3333-4444-555555555555 │ managed │
│ custom-client-id  │ not set                              │ default │
│ unprotected-cache │ false                                │ default │
│ token-hint        │ shown                                │ default │
╰───────────────────┴──────────────────────────────────────┴─────────╯
client-id: managed by your organization (/etc/elevate/managed.json).
```

`elevate config get client-id` prints the value on stdout and the same note on stderr, so a script
can use the value and a person still sees where it came from.

`elevate config managed` prints what the policy actually gave the CLI: the origin, the key names in
effect, and every warning. It never prints a value, so its output is safe to paste into a ticket.

```console
$ elevate config managed
╭─────────┬─────────────────────────────────────────────────────╮
│ origin  │ /etc/elevate/managed.json                           │
│ key     │ ClientId                                            │
│ key     │ AllowedTenants                                      │
│ warning │ AllowedSignInMethods: unknown method 'saml' ignored │
╰─────────┴─────────────────────────────────────────────────────╯
```

`--json` gives `{origin, keys, warnings}` for a monitoring check:

```bash
elevate config managed --json | jq -e '.keys | index("ClientId")' > /dev/null
```

Anything the policy tried to set and could not use shows up here, and in the app's Diagnostics
report — see [troubleshooting.md](troubleshooting.md) for what each warning means.

## Test a file before you deploy it

`--file` reads a `managed.json` from any path instead of the platform source, so you can check what
Elevate would take from a draft on your laptop:

```bash
elevate config managed --file ./managed.json
```

It is a dry run: the trust check is skipped (the command says so), so a file that the real load
would ignore is still parsed here. That is the point — you get the parse errors and the per-key
warnings before the file reaches a fleet. Deploy it, then run `elevate config managed` without
`--file` on a real machine to confirm the ownership rules are satisfied too.

## Other CLI behaviour worth knowing

- `elevate config set client-id …` is refused with `client-id is managed by your organization.`
- `elevate login --method` refuses a method your policy does not permit and names the ones that are
  allowed; without `--method`, login picks the first permitted method.
- `elevate accounts` marks an account whose method is no longer permitted with `not permitted` in
  the Flags column. It keeps working; it just cannot be added again that way.
- `elevate tenants` shows `pinned` and `not permitted` in Flags; `elevate tenants remove` refuses a
  pinned tenant, and `elevate tenants add` refuses one your policy does not allow.
- `elevate profiles` gains a `Source` column (`user` or `managed`); a published profile can be run
  but not renamed, saved over or deleted.
- `elevate profiles export <name>` prints one of your own profiles in the published-profile format,
  as a starting point for a set: [profiles.md](profiles.md).
