# Elevate

Just-in-time Microsoft Entra and Azure PIM role activation from your menu bar or system tray, across accounts and tenants.

Elevate lists every account you have signed in with, each tenant that account can reach, and everything you are eligible for in it: Entra directory roles, Azure resource roles and PIM for Groups memberships. Activate with the policy's default duration and a reason Elevate remembers, select several across tenants and activate them together, or save a selection as a profile and run it with one click. Active roles show a live countdown, can be extended or deactivated, and raise notifications before and at expiry. Sign in with your own app registration, a company app registration, or the Azure CLI / Azure PowerShell app for Azure resource roles.

| App | Status | Docs |
|---|---|---|
| [macOS](macos/) — SwiftUI menu bar app, macOS 26 | Usable: Entra roles, Azure roles, PIM for Groups, profiles, sign-in methods | [macos/README.md](macos/README.md) |
| [Windows 11](windows/) — WinUI 3 tray app, .NET 10, MSI + winget | Designed, not yet implemented | [windows/README.md](windows/README.md) |

## Repository layout

```
macos/     Swift package (ElevateCore) + XcodeGen app target (ElevateApp) + tests
windows/   .NET solution (Elevate.Core, Elevate.App, tests, WiX installer, winget manifest)
shared/    Assets used by both apps: the Entra built-in roles catalogue script
docs/      Design specs and implementation plans (docs/superpowers/specs, docs/superpowers/plans)
```

## Getting started

1. **Create the Entra app registration** that Elevate signs in with. The step-by-step guide covers
   both the Azure CLI route (a script and a permissions manifest are included) and the portal:
   [docs/entra-app-registration.md](docs/entra-app-registration.md).
   - Script: [docs/entra-app/create-app-registration.sh](docs/entra-app/create-app-registration.sh)
   - Permissions manifest for `az ad app create`: [docs/entra-app/required-resource-access.json](docs/entra-app/required-resource-access.json)
2. **Build and run the macOS app**: [macos/README.md](macos/README.md) — prerequisites, build
   steps, sign-in methods, and what each account type can and cannot activate.
3. **Consent per tenant**: an admin grants the delegated permissions once per tenant, either with
   `az ad app permission admin-consent` or through the consent link Elevate offers from each
   tenant's menu. Details in the guide's "Consent" section.

Accounts that cannot use your registration can be added with the Azure CLI or Azure PowerShell
app (Azure resource roles only) or with another company app registration through the loopback
flow; see [Sign-in methods](macos/README.md#sign-in-methods).

## Documentation

| Topic | Where |
|---|---|
| App registration, permissions, consent, troubleshooting sign-in errors | [docs/entra-app-registration.md](docs/entra-app-registration.md) |
| macOS app: build, sign-in methods, panel, profiles, manual roles, smoke test | [macos/README.md](macos/README.md) |
| Windows app plan | [windows/README.md](windows/README.md) |
| Design specs | [docs/superpowers/specs/](docs/superpowers/specs/) |
| Implementation plans | [docs/superpowers/plans/](docs/superpowers/plans/) |

## Design documents

- [Phase 1: core app](docs/superpowers/specs/2026-09-04-pimtray-design.md)
- [Phase 2: Azure resource roles](docs/superpowers/specs/2026-09-04-pimtray-phase2-azure-design.md)
- [Sign-in methods](docs/superpowers/specs/2026-09-05-elevate-signin-methods-design.md)
- [Phase 3: PIM for Groups](docs/superpowers/specs/2026-09-05-elevate-phase3-groups-design.md)
- [Daily-use panel](docs/superpowers/specs/2026-09-05-elevate-daily-panel-design.md)
- [Activation profiles](docs/superpowers/specs/2026-09-05-elevate-profiles-design.md)
- [Activation shortcuts](docs/superpowers/specs/2026-09-05-elevate-shortcuts-design.md)
- [Windows app](docs/superpowers/specs/2026-09-05-elevate-windows-design.md)

## License

MIT, see [LICENSE](LICENSE).
