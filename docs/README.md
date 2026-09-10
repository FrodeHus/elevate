# Elevate documentation

## Guides

User guides for the macOS and Windows apps, with screenshots of the macOS app:

- [Getting started](getting-started.md) — install, choose a sign-in method, add accounts and
  tenants, find your way around the panel and Settings.
- [Activating roles](activating-roles.md) — activate, schedule, extend and deactivate Entra, Azure
  and group roles; quick activation; search; notifications and the menu bar icon.
- [Profiles and shortcuts](profiles-and-shortcuts.md) — save role sets, run them with one click
  or a global shortcut, edit and manage them.
- [Approving requests](approvals.md) — decide other people's activation requests from the panel.
- [Troubleshooting](troubleshooting.md) — manual roles and consent, Azure-only accounts, sign-in
  banners, refused activations, diagnostics.

Reference:

- [Setting up the Entra app registration](entra-app-registration.md) — permissions, consent,
  the Azure CLI script and the portal walkthrough, and troubleshooting sign-in errors.
- [The shared Elevate app registration](shared-app-registration.md) — the optional multi-tenant
  app the project provides for quick starts and testing: what it is, its security model, its
  known risks, the no-SLA caveat, how an administrator consents, and when to register your own
  instead.
- [Releasing Elevate](releasing.md) — tagging, what the release workflows do (macOS, Windows
  and the CLI), the optional signing secrets, the Homebrew cask and formula.
- [Requesting access packages](access-packages.md) — finding, requesting and following
  entitlement management access packages per tenant; notifications and the new-role marker.

Enterprise — for the administrator rolling Elevate out to a fleet:

- [Managed configuration](enterprise/README.md) — the model, what the enterprise kit contains,
  which how-to to read, and how to verify a rollout.
- [Managed configuration keys](enterprise/keys.md) — every key with its type, allowed values and
  syntax in plist, registry and JSON form.
- [Jamf Pro](enterprise/macos-jamf.md) and [Intune for macOS](enterprise/macos-intune.md) — the pkg
  and the configuration profile.
- [Intune for Windows](enterprise/windows-intune.md) and
  [Group Policy](enterprise/windows-group-policy.md) — the MSI, the ADMX and the policy keys.
- [The CLI](enterprise/cli.md) — `/etc/elevate/managed.json` with an Ansible task and a Jamf
  script, the registry on Windows, and `elevate config managed`.
- [Publishing profiles](enterprise/profiles.md) — the profile-set format, inline or by URL, and
  what users see.
- [Enterprise troubleshooting](enterprise/troubleshooting.md) — why a value did not arrive, and
  every warning with its cause.

## Design documents

- [Phase 1: core app](superpowers/specs/2026-09-04-pimtray-design.md)
- [Phase 2: Azure resource roles](superpowers/specs/2026-09-04-pimtray-phase2-azure-design.md)
- [Sign-in methods](superpowers/specs/2026-09-05-elevate-signin-methods-design.md)
- [Phase 3: PIM for Groups](superpowers/specs/2026-09-05-elevate-phase3-groups-design.md)
- [Daily-use panel](superpowers/specs/2026-09-05-elevate-daily-panel-design.md)
- [Activation profiles](superpowers/specs/2026-09-05-elevate-profiles-design.md)
- [Activation shortcuts](superpowers/specs/2026-09-05-elevate-shortcuts-design.md)
- [Approvals](superpowers/specs/2026-09-05-elevate-approvals-design.md)
- [Operations: launch at login, diagnostics, updates, releases](superpowers/specs/2026-09-06-elevate-operations-design.md)
- [Windows app](superpowers/specs/2026-09-05-elevate-windows-design.md)
- [Windows app, phase 3: parity and release hardening](superpowers/specs/2026-09-06-elevate-windows-phase3-design.md)
- [CLI](superpowers/specs/2026-09-07-elevate-cli-design.md)
- [Access packages](superpowers/specs/2026-09-08-elevate-access-packages-design.md)
- [Managed configuration through Intune and Jamf](superpowers/specs/2026-09-09-elevate-managed-configuration-design.md)
- [Design canvases (macOS and Windows)](design/README.md) — HTML mockups saved from Claude Design

Implementation plans live next to them in [superpowers/plans/](superpowers/plans/).
