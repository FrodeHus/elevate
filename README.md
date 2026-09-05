# Elevate

Just-in-time Microsoft Entra and Azure PIM role activation from your menu bar or system tray, across accounts and tenants.

Elevate lists every account you have signed in with, each tenant that account can reach, and the PIM roles you are eligible for in it: Entra directory roles and Azure resource roles. Activate a role with the policy's default duration and a reason Elevate remembers, or select several roles across tenants and activate them together. Active roles show a live countdown, can be deactivated once Entra's five-minute minimum has passed, and raise a notification before they expire. Sign in with your own app registration, or with the Azure CLI app when a tenant will not grant yours consent.

| App | Status | Docs |
|---|---|---|
| [macOS](macos/) — SwiftUI menu bar app, macOS 26 | Usable; phase 1, phase 2 (Azure roles) and sign-in methods complete | [macos/README.md](macos/README.md) |
| [Windows 11](windows/) — WinUI 3 tray app, .NET 10, MSI + winget | Designed, not yet implemented | [windows/README.md](windows/README.md) |

## Repository layout

```
macos/     Swift package (ElevateCore) + XcodeGen app target (ElevateApp) + tests
windows/   .NET solution (Elevate.Core, Elevate.App, tests, WiX installer, winget manifest)
shared/    Assets used by both apps: the Entra built-in roles catalogue script
docs/      Design specs and implementation plans (docs/superpowers/specs, docs/superpowers/plans)
```

## Design documents

- [Phase 1: core app](docs/superpowers/specs/2026-09-04-pimtray-design.md)
- [Phase 2: Azure resource roles](docs/superpowers/specs/2026-09-04-pimtray-phase2-azure-design.md)
- [Sign-in methods](docs/superpowers/specs/2026-09-05-elevate-signin-methods-design.md)
- [Windows app](docs/superpowers/specs/2026-09-05-elevate-windows-design.md)

## License

MIT, see [LICENSE](LICENSE).
