# Elevate CLI

Date: 2026-09-07. A third front end for the same Core: a command-line tool for Linux, macOS and
Windows covering activation, profiles, approvals and settings, with JSON output and exit codes for
scripts. Follows the operations spec (`2026-09-06-elevate-operations-design.md`) for diagnostics,
updates and releases.

## 1. Goal

Everything that makes sense without a window, on every platform, from one binary with no runtime
to install. Success criteria:

- `elevate login` signs in with any of the four methods (own app, custom app, Azure CLI app,
  Azure PowerShell app) through the system browser, or a device code over SSH; `elevate roles`
  lists Entra, Azure and group eligibilities across accounts and tenants with policy notes and
  status; `elevate activate`, `extend`, `deactivate` and `cancel` do what their names say, with
  the remembered duration and reason as defaults and prompts only in a terminal.
- Profiles: save, show, run (planned first, active and pending skipped), rename, delete, import
  from the desktop app. Approvals: list, approve, deny. Tenants: list, discover, add, remove,
  retry, manual roles. Settings: client id, custom client id, cache mode. Diagnostics, update
  check, catalogue lookup, shell completion for bash, zsh, fish and PowerShell.
- `--json` on every read and write command, stable exit codes (0 ok, 1 failure, 2 usage, 3
  sign-in needed, 4 not found or ambiguous, 5 partial), stdout for results and stderr for
  everything else.
- `elevate status` in under a second on a warm cache; a self-contained single file per platform;
  tests on all three operating systems in CI.

Not in scope: the tray glyph, toasts and expiry notifications (watch mode shows countdowns
instead), the global shortcut, start at login, Ctrl-click shortcuts, panel state such as the
collapsed sections.

## 2. Framework

.NET 10, because `Elevate.Core` is dependency-free `net10.0` and already builds on the three
platforms: the CLI references it directly and reimplements nothing of Graph, ARM, policies,
profiles or approvals. Parsing with System.CommandLine 2.0; tables, spinners, live countdowns and
prompts with Spectre.Console. A Go or Rust framework would have meant a third port of the PIM
logic. Terminal.Gui is not used; the CLI has no full-screen mode.

Distribution: `dotnet publish` self-contained, single-file, ReadyToRun, partially trimmed, one
archive per RID (`linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`, `win-x64`, `win-arm64`).
Partial trimming removes unused framework code and leaves `Elevate.Core` and the CLI whole, so
their reflection-based System.Text.Json keeps working (the JSON reflection switch is turned back
on explicitly, since trimming flips it off); 93 MB untrimmed becomes 34 MB. ReadyToRun keeps a
command around 0.4 s; without it the file would be 18 MB but every command 1.5 s. Native AOT is
not used because Core serialises through reflection; it can follow once Core gains JSON source
generation.

## 3. Auth

`MsalCliProvider` is an `ITokenProvider` over one MSAL.NET public client per client id, the same
identity and error mapping as the Windows app's providers, without a broker or parent window:
`AcquireTokenInteractive` with the system browser and `http://localhost`, or
`AcquireTokenWithDeviceCode` when `--device-code` is given or Linux has no display. Own-app
requests the PIM scopes by name; first-party and custom clients request each resource's
`.default`, as in the apps. `CliTokenProvider` routes by the identity's sign-in method and keeps
one `InteractiveGate` so two tenants needing a prompt queue for the browser.

`TokenCacheStore` uses MSAL Extensions with the platform store: DPAPI file on Windows, keychain on
macOS, libsecret keyring on Linux. `VerifyPersistence` runs on first use; a failure on Linux names
the `unprotected-cache` setting, which switches to `WithLinuxUnprotectedFile` and is off by
default.

## 4. Data directory

Separate from the apps: `%LOCALAPPDATA%\elevate-cli`, `~/Library/Application Support/elevate-cli`,
`$XDG_CONFIG_HOME/elevate-cli`. The apps reconcile the identities in `state.json` against their
own token cache at launch and sign out what they cannot find, so a shared state file with a
different cache would lose accounts in both directions. Profiles cross over with
`elevate profiles import`, which reads the app's `state.json` on Windows and macOS (or `--from`).
`--data-dir` and `ELEVATE_CLI_HOME` override the default; `settings.json` preserves keys it does
not know, so pointing the CLI at the Windows app's directory on purpose keeps that app's settings.

## 5. Session

`ElevateSession` is a headless port of the apps' `AppModel`: load with quarantine of a corrupt
file, refresh per tenant with the same latches (consent refused switches the tenant to manual
roles; a refused Azure list read switches Azure off; a refused group read switches groups off;
the Entra token's scopes decide view-only), policies cached per run and fetched four at a time,
approvals read opportunistically, activation with the deactivate-first path for extend, re-keying
of manual Azure roles, memory of reason and duration, consent latching on a refused activation,
profile planning through `ProfilePlanner`. No timers, notifications, generation counters or UI
thread; parallel tenant reads mutate shared collections under one lock.

## 6. Command surface

Role terms resolve through `RoleSelector`: an eight-character id (`ShortId`, a SHA-256 prefix of
the serialised `RoleKey`, stable across runs and printed in every listing), else an exact name,
else a substring; `--account`, `--tenant`, `--kind`, `--scope` narrow; more than one match is
an error that lists the candidates. Durations accept `2h`, `30m`, `1h30m`, `90`, `2:30`, `PT2H`;
start times `+2h`, `14:30`, ISO 8601.

`activate` skips roles that are already active or pending and says so, which makes it idempotent
for scripts; `extend` is the explicit deactivate-and-reactivate. `--wait` polls the tenant until
the role reports active, up to five minutes. `profiles run` prints the plan, then the outcomes.
Missing reasons and tickets are prompted for only when stdin is a terminal and `--json` is off.

## 7. Release

`.github/workflows/cli.yml` tests on Ubuntu, macOS and Windows and publishes the host RID once.
In `release.yml` a `cli` matrix job builds each platform's archives on its own runner, signs the
macOS binaries with Developer ID and notarizes them (a bare binary cannot be stapled) and the
Windows binaries with Azure Artifact Signing when the apps' secrets are present, generates and
validates the `Reothor.Elevate.CLI` portable winget manifest, and the `publish` job attaches the
six archives, their `.sha256` files and one checksums file to the release, then commits
`Formula/elevate-cli.rb` to `main` in the same commit as the cask. Homebrew installs from the tap
as `frodehus/elevate/elevate-cli` with completions generated from the binary.
