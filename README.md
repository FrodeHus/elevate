<h1><img src="docs/images/icon.png" width="48" alt="" align="absmiddle"> Elevate</h1>

[![macOS CI](https://github.com/FrodeHus/elevate/actions/workflows/macos.yml/badge.svg)](https://github.com/FrodeHus/elevate/actions/workflows/macos.yml) [![Windows CI](https://github.com/FrodeHus/elevate/actions/workflows/windows.yml/badge.svg)](https://github.com/FrodeHus/elevate/actions/workflows/windows.yml) [![CLI CI](https://github.com/FrodeHus/elevate/actions/workflows/cli.yml/badge.svg)](https://github.com/FrodeHus/elevate/actions/workflows/cli.yml) [![Audit CI](https://github.com/FrodeHus/elevate/actions/workflows/audit.yml/badge.svg)](https://github.com/FrodeHus/elevate/actions/workflows/audit.yml) [![Latest release](https://img.shields.io/github/v/release/FrodeHus/elevate)](https://github.com/FrodeHus/elevate/releases/latest) [![License](https://img.shields.io/github/license/FrodeHus/elevate)](LICENSE) [![macOS 26+](https://img.shields.io/badge/macOS-26%2B-blue)](macos/README.md#install) [![Windows 11](https://img.shields.io/badge/Windows-11-blue)](windows/README.md#install)

Just-in-time Microsoft Entra and Azure PIM role activation from your menu bar, system tray or terminal, across accounts and tenants.

![Elevate panel](docs/images/social-preview.png)

Elevate lists every account you have signed in with, each tenant that account can reach, and everything you are eligible for in it: Entra directory roles, Azure resource roles and PIM for Groups memberships. Activate with a duration the policy allows and a reason Elevate remembers, now or scheduled for later; select several across tenants and activate them together; or save a selection as a profile and run it with one click or a global shortcut. Active roles show a live countdown, can be extended or deactivated, and raise notifications before and at expiry. Requests that wait for your approval appear in the panel with Approve and Deny, and entitlement management access packages can be requested and followed per tenant. Sign in with your own app registration, a company app registration, the project's optional shared registration, or the Azure CLI / Azure PowerShell app for Azure resource roles.

A separate read-only companion, `elevate-audit`, finds the *standing* privileged access in a tenant — permanent Entra role assignments (nested groups resolved), permanent members of PIM-managed groups, permanent Owner and Contributor assignments in Azure — and lists what should become PIM eligibility instead. It needs no app registration and shares nothing with the app or CLI.

| App | Status | Docs |
|---|---|---|
| [macOS](macos/) — SwiftUI menu bar app, macOS 26 | Usable: Entra roles, Azure roles, PIM for Groups, approvals, access packages, profiles and shortcuts, sign-in methods | [macos/README.md](macos/README.md) |
| [Windows](windows/) — WinUI 3 tray app, Windows 11 | Usable: the same features, code-signed MSI | [windows/README.md](windows/README.md) |
| [CLI](cli/) — `elevate` for Linux, macOS and Windows | Usable: activation, profiles, approvals, access packages, tenants and settings from the terminal, JSON output and exit codes for scripts | [cli/README.md](cli/README.md) |
| [Audit](audit/) — `elevate-audit` for Linux, macOS and Windows | Usable: read-only report of standing privileged access, terminal, JSON or HTML output | [docs/audit.md](docs/audit.md) |

## Install

**New to Elevate?** The [Getting started guide](docs/getting-started.md) walks you from install
to your first activated role, on macOS and Windows: choosing a sign-in method, adding accounts and
tenants, and finding your way around the panel. The rest of the user guides are listed in
[docs/README.md](docs/README.md).

- **macOS 26**: Homebrew cask or DMG, see [macos/README.md](macos/README.md#install). The cask
  and the pkg also install the `elevate` CLI; the DMG is the app alone.
- **Windows 11**: per-user MSI, which installs the app and the `elevate` CLI, see
  [windows/README.md](windows/README.md#install).
- **CLI on its own** (Linux, Intel Macs, servers): a single binary from the release, see
  [cli/README.md](cli/README.md#install).
- **Audit tool**: Homebrew formula `frodehus/elevate/elevate-audit`, or a single binary from the
  release, see [docs/audit.md](docs/audit.md#1-install).

The app and the CLI sign in with an Entra app registration — your own, a company one, or the project's
optional [shared Elevate app](docs/shared-app-registration.md), which has no SLA; the Microsoft
Azure CLI or Azure PowerShell app needs no registration but covers Azure resource roles only — it
cannot read or activate Entra directory roles or PIM for Groups memberships, because Microsoft
grants those apps no Graph PIM permissions. The audit tool signs in with Microsoft's public
clients and needs no registration of its own. Each app checks the GitHub releases API for a newer
version once a day and offers it in the panel; the CLI mentions one after `elevate status`.

## Enterprise-ready

Organizations roll Elevate out with Intune, Jamf Pro or Group Policy and push their own values —
the app registration's client id, whether the update check runs, which sign-in methods and tenants
people may use, and the role-set profiles everyone should have. A managed value wins over the
user's, locks the setting with a "Managed by your organization" caption, and needs no company
build. Start at [docs/enterprise/README.md](docs/enterprise/README.md); the templates (ADMX,
mobileconfig, Jamf schema, `managed.json`) are in [enterprise/](enterprise/) and ship with each
release as `Elevate-enterprise-kit-<version>.zip`, alongside a `Elevate-<version>.pkg` for silent
macOS deployment.

## Repository layout

```
macos/      Swift package (ElevateCore) + XcodeGen app target (ElevateApp) + tests
windows/    .NET solution (Elevate.Core, Elevate.App, tests, WiX installer, winget manifest)
cli/        .NET solution (Elevate.Cli over Elevate.Core, tests, packaging script, winget manifest)
audit/      .NET solution (Elevate.Audit over Elevate.Core, tests, packaging script, winget manifest)
shared/     Assets used by both apps: the Entra built-in roles catalogue script and the elevation motion data
enterprise/ Managed-configuration templates: ADMX/ADML and .reg (windows/), mobileconfig, Intune plist and
            Jamf manifest (macos/), managed.json (cli/), and a worked example (example/)
Casks/, Formula/   The Homebrew tap: the cask (app + CLI via the pkg) and the audit formula
docs/       User guides, the enterprise how-tos and key reference (docs/enterprise), design canvases
            (docs/design), design specs and implementation plans (docs/superpowers)
site/       The GitHub Pages product page
scripts/    Release helpers: cask and formula updates, changelog cutting, kit and site validation
```

## Security

- **Tokens stay in the platform's protected store.** On macOS the loopback browser flow keeps its
  refresh token in your login keychain (this device only) and signed builds sign in with MSAL,
  which keeps its own cache in the keychain; on Windows MSAL keeps a DPAPI-protected cache under
  your profile. The CLI uses DPAPI, the keychain or the Linux keyring the same way. Nothing is
  written to disk in plain text, unless you opt the CLI into a plain-file cache on a Linux host
  without a keyring.
- **Where Elevate connects.** Microsoft identity platform (login), Microsoft Graph and the Azure
  Resource Manager endpoints, plus the GitHub releases API for the once-a-day update check. Nothing
  else.
- **No telemetry.** No analytics, no crash reporting, no phone-home of any kind.
- **Diagnostics are safe to paste.** The report behind Settings → Copy diagnostics has no field for
  a token or a client id, so neither can appear in it; it says only whether the client id in
  effect is the shared Elevate app, an own registration, or not set.
- **Reporting a vulnerability:** see [SECURITY.md](SECURITY.md).

## Documentation

The GitHub Pages product page lives in [site/](site/README.md). See that guide for local preview,
validation, and the one-time Pages setup; `.github/workflows/pages.yml` publishes site changes
from `main`.

| Topic | Where |
|---|---|
| Documentation index | [docs/README.md](docs/README.md) |
| Getting started: install, sign in, add accounts and tenants, the panel, Settings | [docs/getting-started.md](docs/getting-started.md) |
| Activating roles: activate, schedule, extend, deactivate, quick activation, notifications | [docs/activating-roles.md](docs/activating-roles.md) |
| Profiles and shortcuts: save role sets, run them with one click or a global shortcut | [docs/profiles-and-shortcuts.md](docs/profiles-and-shortcuts.md) |
| Approving other people's requests from the panel | [docs/approvals.md](docs/approvals.md) |
| Requesting and following access packages | [docs/access-packages.md](docs/access-packages.md) |
| Troubleshooting: manual roles and consent, Azure-only accounts, sign-in banners, diagnostics | [docs/troubleshooting.md](docs/troubleshooting.md) |
| App registration, permissions, consent, troubleshooting sign-in errors, the optional audit read scopes | [docs/entra-app-registration.md](docs/entra-app-registration.md) |
| The shared Elevate app: what it is, its risks, no SLA, admin consent | [docs/shared-app-registration.md](docs/shared-app-registration.md) |
| macOS app: build, sign-in methods, panel, profiles, manual roles, smoke test | [macos/README.md](macos/README.md) |
| Cutting a release: tagging, the workflow, signing secrets, the cask and formulas | [docs/releasing.md](docs/releasing.md) |
| Rolling out to a fleet: Intune, Jamf, Group Policy, the CLI, managed profiles | [docs/enterprise/README.md](docs/enterprise/README.md) |
| Managed configuration keys: every key in plist, registry and JSON form | [docs/enterprise/keys.md](docs/enterprise/keys.md) |
| Windows app: build, install, installer and winget manifest, release | [windows/README.md](windows/README.md) |
| CLI: install, sign-in, commands, JSON and exit codes, data directory, build, release | [cli/README.md](cli/README.md) |
| Audit tool: install, what it asks for, running it, the findings, JSON and HTML output | [docs/audit.md](docs/audit.md) |
| Design canvases: HTML mockups of the macOS and Windows apps | [docs/design/README.md](docs/design/README.md) |
| Design specs | [docs/superpowers/specs/](docs/superpowers/specs/) |
| Implementation plans | [docs/superpowers/plans/](docs/superpowers/plans/) |

## Contributing and support

Contributions are welcome — start with [CONTRIBUTING.md](CONTRIBUTING.md) for the prerequisites,
build and test commands, and the conventions this repository keeps. Bugs and feature requests go to
the [issue tracker](https://github.com/FrodeHus/elevate/issues); what changed in each release is in
[CHANGELOG.md](CHANGELOG.md).

## Roadmap

- **Windows: winget** — the first `Reothor.Elevate`, `Reothor.Elevate.CLI` and
  `Reothor.Elevate.Audit` submissions are in `microsoft/winget-pkgs` review; every release
  after that opens its update pull requests automatically.

## License

MIT, see [LICENSE](LICENSE).
