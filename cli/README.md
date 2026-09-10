# Elevate CLI

The command-line counterpart of the macOS and Windows apps: just-in-time Microsoft Entra and
Azure PIM role activation from a terminal, on Linux, macOS and Windows. One self-contained
`elevate` binary per platform, built with .NET 10 on the same `Elevate.Core` the Windows app
uses, so roles, activation, profiles and approvals behave the same way and read the same
`state.json` schema.

```text
$ elevate roles --tenant contoso
╭──────────┬──────────────────────────┬───────┬────────────────────┬─────────┬────────────────────┬───────────────────────╮
│ ID       │ Role                     │ Kind  │ Scope              │ Tenant  │ Policy             │ Status                │
├──────────┼──────────────────────────┼───────┼────────────────────┼─────────┼────────────────────┼───────────────────────┤
│ 3f9a1c2e │ Global Reader            │ Entra │ —                  │ Contoso │ 8 h · MFA          │ eligible              │
│ b71d0e44 │ Contributor              │ Azure │ Prod · subscription│ Contoso │ 2 h · approval     │ awaiting approval     │
│ 9c02aa17 │ Platform Admins (member) │ Group │ member             │ Contoso │ 4 h                │ active 2 h 41 min left│
╰──────────┴──────────────────────────┴───────┴────────────────────┴─────────┴────────────────────┴───────────────────────╯

$ elevate activate "Global Reader" --duration 2h --reason "Support ticket 4211"
```

## Install

**With the app (macOS on Apple Silicon, Windows).** The Homebrew cask and the macOS installer
package put the CLI on your PATH as `/usr/local/bin/elevate` (it lives inside
`Elevate.app/Contents/Helpers`); the Windows MSI installs `elevate.exe` in the `cli` folder under
the app and adds that folder to your user PATH. Install the app and you have the CLI at the same
version: [macos/README.md](../macos/README.md#install), [windows/README.md](../windows/README.md#install).
If you previously installed the `elevate-cli` formula, run `brew uninstall frodehus/elevate/elevate-cli`
— its `/opt/homebrew/bin/elevate` comes before `/usr/local/bin` on the PATH and would keep running
the old binary.

**Homebrew formula (Linux, Intel Macs).** The formula lives in this repository, which doubles as a
tap. It is **deprecated**: the cask now carries the CLI, and the formula will be removed in a later
release. Linux and Intel Macs keep the archives below.

```bash
brew tap FrodeHus/elevate https://github.com/FrodeHus/elevate
brew trust frodehus/elevate        # Homebrew 6 requires trusting third-party taps
brew install frodehus/elevate/elevate-cli
```

Shell completions are installed with it. Upgrade with `brew upgrade frodehus/elevate/elevate-cli`.

**Windows, standalone.** `winget install Reothor.Elevate.CLI` once the manifest is submitted (winget
moderation requires signed binaries, so the manifest is a release artifact until Azure Artifact
Signing is set up, like the app's). Until then, download `elevate-cli-<version>-win-x64.zip` (or
`-win-arm64.zip`) from the [latest release](https://github.com/FrodeHus/elevate/releases/latest)
and put `elevate.exe` on your PATH. Do not combine this with the MSI's copy: two `elevate` entries
on the PATH means whichever comes first wins.

**Any platform.** Download the archive for your platform from the latest release, check it against
`elevate-cli-<version>-checksums.txt` (`sha256sum -c`), and unpack the single binary anywhere on
your PATH. Nothing else is needed: the .NET runtime is inside the file. Archives are named
`elevate-cli-<version>-<rid>.tar.gz` for `linux-x64`, `linux-arm64`, `osx-arm64` and `osx-x64`,
and `.zip` for `win-x64` and `win-arm64`.

Completions for a shell that Homebrew did not set up:

```bash
source <(elevate completion bash)            # or zsh; add to your profile
elevate completion fish | source
elevate completion powershell | Out-String | Invoke-Expression
```

## Sign in

The CLI signs in the same ways the apps do, chosen per account with `--method`:

| Method | Command | What it can do |
|---|---|---|
| Your own app registration | `elevate config set client-id <application id>` then `elevate login` | Entra directory roles, Azure resource roles, PIM for Groups, approvals, access packages |
| The shared Elevate app | `elevate config set client-id shared`, `elevate consent` for the link an administrator opens once per tenant, then `elevate login` | The same, once an administrator has consented — optional, no SLA, see [docs/shared-app-registration.md](../docs/shared-app-registration.md) |
| A custom (company) registration | `elevate login --method custom --client-id <application id>` | The same, given the same permissions; the id is remembered |
| The Azure CLI app | `elevate login --method cli` | Azure resource roles only, no registration or consent needed |
| The Azure PowerShell app | `elevate login --method pwsh` | Same, for tenants that block the Azure CLI app |

Sign-in opens the system browser and returns to `http://localhost`, which the registration must
list as a redirect URI under *Mobile and desktop applications* (the setup script and guide in
[docs/entra-app-registration.md](../docs/entra-app-registration.md) already add it; the
first-party apps accept it without registration; the shared Elevate app lists it too). Over SSH
or in a container, pass
`--device-code` (or run without a display on Linux, which selects it automatically): the CLI
prints a code to enter at microsoft.com/devicelogin from any device. The device code flow
requires *Allow public client flows* on your own registration.

Microsoft's Azure CLI and Azure PowerShell apps are not pre-authorised for the Graph PIM
permissions, so an account added that way sees and activates Azure resource roles only. See the
[sign-in methods](../macos/README.md#sign-in-methods) section of the macOS guide for the details;
they apply here unchanged.

After the first sign-in the home tenant is tracked. Other tenants: `elevate tenants discover --add`
lists and tracks every tenant the account can reach through Azure Resource Manager, and
`elevate tenants add contoso.com` tracks one by domain or id.

## Use

Every read command re-reads the tenants it needs from the service, so what it prints is current.
Roles are named on the command line by their **name** (exact first, then as a substring,
case-insensitive) or by the eight-character **id** from `elevate roles`; `--tenant`, `--account`,
`--kind entra|azure|groups` and `--scope` narrow the match, and a term that still matches several
roles lists them instead of guessing.

| Command | What it does |
|---|---|
| `elevate` / `elevate status` | What is active, awaiting approval or scheduled, with time left. |
| `elevate roles [filter]` | Everything you are eligible for, with policy notes and status. `--active` shows only what is active or pending. |
| `elevate watch` | A live countdown table until Ctrl+C; re-reads the service every 60 s (`--interval`). |
| `elevate activate <role…>` | Activate. Duration and reason default to what was used last for that role, then to the policy; `--duration 2h`, `--reason`, `--ticket`, `--at 14:30` (or `+2h`) and `--wait` override. A role that is already active is left alone. With no role named, a checklist is offered in a terminal. |
| `elevate extend <role…>` | Deactivate and re-activate, so the clock starts over. Refused for approval-required roles, which would leave you without the role while the request waits. |
| `elevate deactivate <role…>` / `elevate cancel <role…>` | Deactivate an active role; withdraw a request that is awaiting approval or scheduled. |
| `elevate run [--profile NAME] [--role ROLE…] -- <command>` | Activate what is named (roles already active are left alone, pending ones are waited for), wait until every one is active, approvals included, then run the command with the terminal's own stdin and stdout and exit with its code. Activations last 10 minutes by default, just enough for one command (`--duration` overrides; the durations remembered for `activate` and the profile are untouched). `--deactivate-after` deactivates what this call activated once the command exits; `--settle 2m` pauses after a group activation for the claim to propagate (default 30 s for groups); `--timeout 1h` bounds the wait (default 15 m). |
| `elevate init bash\|zsh\|fish\|pwsh` | A shell hook that wraps `az`, `kubectl`, `terraform` and `helm`: when one fails with an authorization error, a line suggests `elevate run`. `eval "$(elevate init zsh)"` in your profile. |
| `elevate profiles` | List profiles. `save <name> <role…>` (or `--from-active`), `show`, `run` (plans first: active and pending entries are skipped; `--dry-run` shows the plan), `rename`, `delete`, `import` (copies the desktop app's profiles), `export <name>` (prints one of your profiles as a managed profile document). Profiles your organization publishes are listed with source `managed` and cannot be renamed, deleted or saved over. |
| `elevate approvals` | Requests awaiting your decision as an approver; `approve <id>` and `deny <id> --reason …`. Extend and renew requests are listed with "decide in the portal", as in the apps. |
| `elevate packages` | Access packages (entitlement management) for accounts signed in with your own or a custom registration: `list` (with the state of any pending request or delivered assignment), `requests` (open ones; `--all` adds denied, failed and cancelled with dates and the service's status), `assigned` (delivered, with expiry and policy), `request <package> --justification …` (`--policy` when several apply; packages that ask questions are handed to My Access with a link), `cancel <id>`. The first call asks for the `EntitlementMgmt-SubjectAccess.ReadWrite` permission, which needs no admin consent. |
| `elevate accounts` / `login` / `logout` | The signed-in accounts. |
| `elevate tenants` | Tracked tenants with their flags; `discover`, `add`, `remove`, `retry` (clears the manual-roles, azure-off and groups-off latches), and `manual add|list|clear` for tenants that refuse discovery. |
| `elevate config` | The client id (`config set client-id shared` selects the shared Elevate app and states its no-SLA caveat once; `config` then shows `shared Elevate app`), the remembered custom client id, the Linux cache mode and the stale-token hint (`config set token-hint off --account alex` hides it for one account); `config path` prints the data directory. |
| `elevate consent [--tenant <id>] [--open]` | The admin consent link for the configured registration: the `organizations` endpoint by default, one tenant with `--tenant` (a tracked tenant's name, or any id or domain). The shared app's link lands on its consent result page, your own registration's on `nativeclient`. `--open` also opens it in the browser. |
| `elevate catalogue [query]` | The built-in Entra roles, for `tenants manual add --entra`. |
| `elevate diagnostics` | A plain-text report for a bug report. It has no field for a token or client id; it says `Client id: shared Elevate app`, `own registration` or `not set`. |
| `elevate update` | Checks GitHub for a newer CLI release. `status` also mentions one, at most once a day. |

**Scripts.** `--json` on any command prints stable, camelCase JSON on stdout; tables and
progress go to stderr, so `elevate roles --json | jq` is clean. Exit codes:

| Code | Meaning |
|---|---|
| 0 | Done. |
| 1 | The operation failed. |
| 2 | Bad arguments, or a reason, ticket or client id was needed and not given. |
| 3 | No account, or an account needs an interactive sign-in this run could not do. |
| 4 | A role, tenant, account, profile or request did not match, or matched several. |
| 5 | Some of the requested activations or decisions went through, not all. |
| 127 | `run` could not find the command. Otherwise `run` exits with the command's own code. |

`activate` is idempotent, so a script can run `elevate activate Reader --tenant prod --reason ci --wait -q`
before an `az` command and rely on the exit code; `profiles run` does the same for a set. Prompts
appear only when stdin is a terminal and `--json` is off; otherwise the missing value is an error.

`elevate run --role Reader --tenant prod -- az group list` folds that into one step: it activates,
waits (through an approval too, with a line saying so), then runs the command. Its progress goes
to stderr, so the command's stdout can still be piped. Ctrl+C during the wait leaves the activation
in place; during the command it reaches the command, and `run` exits with the command's code.

**Stale tokens.** After an Azure resource role or a group membership activates, the Azure CLI,
Azure PowerShell and kubelogin keep using the token they cached before it, which lacks the new
assignment, so the next command is refused as if nothing had happened. The CLI says so after such
an activation and names the fix: `az login` again (and `kubelogin remove-tokens` for AKS), or a
fresh `Connect-AzAccount`. `elevate config set token-hint
off --account alex` hides the line for one account; `on` brings it back.

Global options: `--json`, `--quiet`, `--no-color` (or `NO_COLOR`), `--device-code`, `--data-dir`.

## Where things live

The CLI keeps its own data directory, separate from the desktop apps on purpose: each app
validates the accounts in its state against its own token cache and signs out those it cannot
find, so two programs with different caches must not share one `state.json`.

| Platform | Default (`ELEVATE_CLI_HOME` or `--data-dir` override) |
|---|---|
| Windows | `%LOCALAPPDATA%\elevate-cli` |
| macOS | `~/Library/Application Support/elevate-cli` |
| Linux | `$XDG_CONFIG_HOME/elevate-cli`, else `~/.config/elevate-cli` |

It holds `state.json` (accounts, tenants, manual roles, remembered reasons, profiles; the same
schema as the apps), `settings.json`, and the MSAL token cache. The cache is protected the way each
platform protects secrets: DPAPI on Windows, the login keychain on macOS, the Secret Service keyring
(GNOME Keyring, KWallet) on Linux. Nothing is written to disk in plain text unless you say so: on a
Linux host without a reachable keyring (SSH, a container), `elevate config set unprotected-cache true`
keeps the cache in a plain file under the data directory, readable by anyone with your user's file
access; sign in again after changing it. Profiles from the desktop app come across with
`elevate profiles import`; entries for accounts not signed in here plan as "tenant not loaded" until
you sign those accounts in.

## Managed configuration

An organization can push the CLI's settings to a fleet: `/etc/elevate/managed.json` on macOS and
Linux (neither the file nor its directory writable by others, or it is ignored with a warning), and
`HKLM\SOFTWARE\Policies\Reothor\Elevate` then `HKCU\...` on Windows. A managed value wins over
yours, and `elevate config` marks it `managed` in the `Source` column; `elevate config managed`
prints the origin, the keys in effect and any warnings, and `--file <path>` checks a file as a dry
run before you deploy it. Administrators start at
[docs/enterprise/README.md](../docs/enterprise/README.md), with the CLI's own page at
[docs/enterprise/cli.md](../docs/enterprise/cli.md).

## Build and test

```bash
cd cli
dotnet test Elevate.Cli.sln
dotnet run --project src/Elevate.Cli -- --help
```

Needs the .NET 10 SDK (`cli/global.json`), on any of the three platforms. The solution references
`../windows/src/Elevate.Core` directly; there is no copy. Tests run with warnings as errors, like the
app suites. To produce the single-file binary for a platform:

```bash
./package.sh publish 0.0.0 linux-x64      # dist/linux-x64/elevate
./package.sh archive 0.0.0 linux-x64      # dist/elevate-cli-0.0.0-linux-x64.tar.gz + .sha256
```

On macOS the release workflow signs the binary with the hardened runtime and
`cli/elevate.entitlements` (JIT and unsigned executable memory for the .NET runtime, no library
validation); the same file signs the copy bundled in `Elevate.app/Contents/Helpers`.

Self-contained single-file publishing cross-compiles across operating systems, so all six RIDs can
be produced on one machine; the release workflow still builds each on its own runner so the macOS
and Windows binaries can be signed there. The binary is about 34 MB: the framework is trimmed
(partial mode, which touches only assemblies that declare themselves trimmable; `Elevate.Core`
and the CLI stay whole, so their reflection-based System.Text.Json keeps working) and pre-compiled
with ReadyToRun, which is what keeps a command under half a second. Without ReadyToRun the file
would be 18 MB and every command would take 1.5 s. Native AOT is not used for the same reason
Core cannot be trimmed: it serialises through reflection.

Layout:

```text
cli/
  src/Elevate.Cli/      the binary: Auth (MSAL over the system browser or a device code, the
                        per-platform cache), Session (a headless port of the apps' AppModel),
                        Commands, Selection (role names, ids, durations), Rendering, Completion
  tests/Elevate.Cli.Tests/
  package.sh            publish and archive one platform
  winget/               templates and script for the Reothor.Elevate.CLI portable manifest
```

## Release

Released with the apps: tag `v<version>` on `main` and the
[release workflow](../.github/workflows/release.yml) builds the six archives on Linux, macOS and
Windows runners, signs the macOS and Windows binaries when the apps' signing secrets are set,
attaches them and a checksums file to the same GitHub release as the DMG and the MSIs. The pkg
carries the signed osx-arm64 binary and the MSIs carry the signed win-x64/win-arm64 binaries, so
the release also ships the CLI inside those installers. The workflow updates
`Formula/elevate-cli.rb` on `main` next to the cask with its deprecation notice, and keeps the
winget manifest as the `winget-cli-manifest` artifact. The full procedure is in
[docs/releasing.md](../docs/releasing.md).
