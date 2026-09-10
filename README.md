<h1><img src="docs/images/icon.png" width="48" alt="" align="absmiddle"> Elevate</h1>

[![macOS CI](https://github.com/FrodeHus/elevate/actions/workflows/macos.yml/badge.svg)](https://github.com/FrodeHus/elevate/actions/workflows/macos.yml) [![Windows CI](https://github.com/FrodeHus/elevate/actions/workflows/windows.yml/badge.svg)](https://github.com/FrodeHus/elevate/actions/workflows/windows.yml) [![Latest release](https://img.shields.io/github/v/release/FrodeHus/elevate)](https://github.com/FrodeHus/elevate/releases/latest) [![License](https://img.shields.io/github/license/FrodeHus/elevate)](LICENSE) [![macOS 26+](https://img.shields.io/badge/macOS-26%2B-blue)](macos/README.md#install) [![Windows 11](https://img.shields.io/badge/Windows-11-blue)](windows/README.md#install)

Just-in-time Microsoft Entra and Azure PIM role activation from your menu bar, system tray or terminal, across accounts and tenants.

![Elevate panel](docs/images/social-preview.png)

Elevate lists every account you have signed in with, each tenant that account can reach, and everything you are eligible for in it: Entra directory roles, Azure resource roles and PIM for Groups memberships. Activate with the policy's default duration and a reason Elevate remembers, select several across tenants and activate them together, or save a selection as a profile and run it with one click. Active roles show a live countdown, can be extended or deactivated, and raise notifications before and at expiry. Sign in with your own app registration, a company app registration, the project's optional shared registration, or the Azure CLI / Azure PowerShell app for Azure resource roles.

| App | Status | Docs |
|---|---|---|
| [macOS](macos/) — SwiftUI menu bar app, macOS 26 | Usable: Entra roles, Azure roles, PIM for Groups, profiles, sign-in methods | [macos/README.md](macos/README.md) |
| [Windows](windows/) — WinUI 3 tray app, Windows 11 | Usable: the same features, unsigned MSI for now | [windows/README.md](windows/README.md) |
| [CLI](cli/) — `elevate` for Linux, macOS and Windows | Usable: activation, profiles, approvals, tenants and settings from the terminal, JSON output and exit codes for scripts | [cli/README.md](cli/README.md) |

## Install

**New to Elevate?** The [Getting started guide](docs/getting-started.md) walks you from install
to your first activated role, on macOS and Windows: choosing a sign-in method, adding accounts and
tenants, and finding your way around the panel. The rest of the user guides are listed in
[docs/README.md](docs/README.md).

- **macOS 26**: Homebrew cask or DMG, see [macos/README.md](macos/README.md#install). The cask
  and the pkg also install the `elevate` CLI; the DMG is the app alone.
- **Windows 11**: per-user MSI, which installs the app and the `elevate` CLI, see
  [windows/README.md](windows/README.md#install).
- **CLI on its own** (Linux, Intel Macs, servers): a single binary from the release, or the
  deprecated Homebrew formula, see [cli/README.md](cli/README.md#install).

All three sign in with an Entra app registration — your own, a company one, or the project's
optional [shared Elevate app](docs/shared-app-registration.md), which has no SLA; the Microsoft
Azure CLI or Azure PowerShell app needs no registration but covers Azure resource roles only — it
cannot read or activate Entra directory roles or PIM for Groups memberships, because Microsoft
grants those apps no Graph PIM permissions. Each app checks the GitHub releases API for a newer
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
shared/     Assets used by both apps: the Entra built-in roles catalogue script
enterprise/ Managed-configuration templates: ADMX/ADML, mobileconfig, Jamf schema, managed.json, an example
Casks/, Formula/   The Homebrew tap: the cask (app + CLI via the pkg) and the deprecated CLI formula
docs/       Design specs and implementation plans (docs/superpowers/specs, docs/superpowers/plans)
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
| App registration, permissions, consent, troubleshooting sign-in errors | [docs/entra-app-registration.md](docs/entra-app-registration.md) |
| The shared Elevate app: what it is, its risks, no SLA, admin consent | [docs/shared-app-registration.md](docs/shared-app-registration.md) |
| macOS app: build, sign-in methods, panel, profiles, manual roles, smoke test | [macos/README.md](macos/README.md) |
| Cutting a release: tagging, the workflow, signing secrets, the cask | [docs/releasing.md](docs/releasing.md) |
| Rolling out to a fleet: Intune, Jamf, Group Policy, the CLI, managed profiles, the key reference | [docs/enterprise/README.md](docs/enterprise/README.md) |
| Windows app: build, install, installer and winget manifest, release | [windows/README.md](windows/README.md) |
| CLI: install, sign-in, commands, JSON and exit codes, data directory, build, release | [cli/README.md](cli/README.md) |
| Design specs | [docs/superpowers/specs/](docs/superpowers/specs/) |
| Implementation plans | [docs/superpowers/plans/](docs/superpowers/plans/) |

## Contributing and support

Contributions are welcome — start with [CONTRIBUTING.md](CONTRIBUTING.md) for the prerequisites,
build and test commands, and the conventions this repository keeps. Bugs and feature requests go to
the [issue tracker](https://github.com/FrodeHus/elevate/issues); what changed in each release is in
[CHANGELOG.md](CHANGELOG.md).

## Roadmap

- **Windows: code signing and winget submission** — releases are unsigned until Azure Artifact
  Signing is set up; see [windows/README.md](windows/README.md).

## License

MIT, see [LICENSE](LICENSE).
