# Changelog

All notable changes to Elevate are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Elevate for Windows: a Windows 11 system-tray flyout with the macOS app's phase-1 and phase-2
  functionality, built with WinUI 3 on .NET 10. Multi-account sign-in with your own app
  registration through the Windows account picker (WAM), a custom client ID, or the Azure CLI and
  Azure PowerShell apps; tenant discovery; Entra directory, Azure resource and PIM-for-Groups
  roles with policies, remembered reasons, bulk activation, scheduled starts, deactivation with
  the five-minute lock, cancelling pending requests, live countdowns and expiry toasts with
  Extend; manual roles for tenants that refuse discovery; offline awareness; start with sign-in.
  Distributed as unsigned per-user x64 and arm64 MSIs from `windows-v*` releases (a winget
  manifest for `Reothor.Elevate` is generated but not yet submitted). The Windows and macOS apps
  share the `state.json` schema.
- Elevate for Windows, phase 3: activation profiles (save from the bulk bar, a chip row under the
  pivots, Manage and Run windows), Ctrl-click quick activation and a global shortcut that runs a
  profile, a pinned Approvals group with Approve/Deny, approval toasts and a tray badge, Copy
  diagnostics and a daily update check with an in-flyout banner. The flyout's bulk bar and footer
  stay visible when the list is tall.

### Changed

- The macOS GitHub release is titled "Elevate for Mac x.y.z", matching "Elevate for Windows".
- Entra directory role reads follow `@odata.nextLink` on both platforms.

## [1.0.2] - 2026-09-06

### Added

- A menu bar icon drawn from the app icon's double chevron, rendered as a template image so it
  follows the menu bar's light, dark and tinted appearance. An exclamation badge appears next to
  the count when an activation is about to expire.
- Community files: CONTRIBUTING, SECURITY, CODE_OF_CONDUCT, issue and pull request templates,
  Dependabot for GitHub Actions, and this changelog.

### Changed

- README: badges, requirements, upgrade instructions, security and contributing sections; the
  design document list moved to `docs/README.md`.

## [1.0.1] - 2026-09-06

### Added

- The Elevate app icon.

### Fixed

- Own app registration sign-in on unsigned (ad-hoc signed) builds: those builds carry no
  entitlements, so MSAL's shared keychain group is unavailable. They now sign in with the same
  client ID through the loopback browser flow instead, and Settings shows "via loopback" next to
  the version. Signing out a duplicate account no longer deletes the shared refresh token.
- Install instructions for Homebrew 6: the tap has to be trusted (`brew trust frodehus/elevate`)
  and the cask referred to by its fully qualified name (`frodehus/elevate/elevate`), because this
  repository is not a `homebrew-` named tap. `--no-quarantine` is gone; unsigned builds use
  `xattr -d com.apple.quarantine` instead.
- The cask's macOS dependency now uses the symbolic form (`depends_on macos: :tahoe`).

## [1.0.0] - 2026-09-06

First release. A macOS 26 menu bar app for just-in-time Microsoft Entra and Azure PIM role
activation.

### Added

- Entra directory role activation: eligible roles per tenant, policy-driven durations, remembered
  reasons, ticket fields, extend, deactivate and live countdowns.
- Azure resource role activation across management groups, subscriptions, resource groups and
  resources, including manually configured roles for tenants that refuse discovery.
- PIM for Groups: eligible memberships and ownerships, activated and deactivated like roles, with
  the Entra and Azure tabs re-read afterwards.
- Multiple accounts and multiple tenants at once, with tenant discovery and manual tenant add.
- Sign-in methods: your own Entra app registration, a custom app registration through the loopback
  flow, and the Microsoft Azure CLI / Azure PowerShell apps (Azure resource roles only).
- Activation profiles: save a selection across tabs, tenants and accounts, and run it from a chip
  with a planning sheet.
- Global keyboard shortcut that runs a chosen profile, and Option-click to activate without the
  dialog; scheduled ("start at") activations.
- Approvals: pending requests from other users, approve and deny with a justification, with
  notifications and a menu bar badge.
- Launch at login, expiry and approval notifications, and a menu bar glyph that reflects state.
- Diagnostics report (Settings → Copy diagnostics) that carries no tokens and no client ids.
- Daily update check against the GitHub releases API, with a notice in the panel.
- Distribution: ad-hoc signed DMG published by the tag-driven release workflow, and a Homebrew
  cask served from this repository as a tap.

[Unreleased]: https://github.com/FrodeHus/elevate/compare/v1.0.2...HEAD
[1.0.2]: https://github.com/FrodeHus/elevate/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/FrodeHus/elevate/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/FrodeHus/elevate/releases/tag/v1.0.0
