# Changelog

All notable changes to Elevate are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- Windows: the primary action in a role row is the one prominent button ("Activate", or "Request"
  when the policy needs an approver); Deactivate stays a quiet button. The activation window's
  confirm button says "Request" when every role in it waits for approval.
- Windows: eligible rows show what the PIM policy will ask for ("approval", "MFA", "Conditional
  Access") under the role name, with a tooltip that names the authentication context; the
  activation window lists the same notices, and the bulk table and the profile run window show the
  combined caption in their status column.
- Windows: the bulk activation table and the profile run window box the rows of each tenant, headed
  by the tenant name with the account address as a caption, instead of a small "account · tenant"
  caption line.
- Windows: tenant and account headers show one warning glyph instead of a run of pills ("Azure
  off", "Groups off", "error", "Azure roles only"): orange when the tenant has limitations, red when
  discovery or refresh failed. Hover lists them; clicking opens a flyout with each reason in full.
  The "manual roles" pill stays, since it is a mode rather than a problem. The account-level badge
  only appears while no tenant is known yet.
- Windows: the custom client id sign-in method reads "Custom app", without a "(loopback)" note.

### Fixed

- Windows: the bulk activation table and the profile run window keep their duration and status
  columns aligned when a row has nothing to report, and longer status texts trim instead of
  overflowing the column.
- Windows: Azure resource roles in the activation window, the bulk table and the profile run window
  show the scope under the role name, with the full ARM path as a tooltip.

## [1.2.4] - 2026-09-06

### Fixed

- macOS: the bulk activation table and the profile run sheet keep their duration and status
  columns aligned when a row has nothing to report, and longer durations are no longer truncated.
- macOS: Azure resource roles in the activation sheet, the bulk table and the profile run sheet show
  the scope under the role name, with the full ARM path as a tooltip.

## [1.2.3] - 2026-09-06

### Changed

- macOS: the primary action in a role row is the one prominent button ("Activate", or "Request"
  when the policy needs an approver); Deactivate stays a quiet button. The activation sheet's
  confirm button says "Request" when every role in it waits for approval.
- macOS: eligible rows show what the PIM policy will ask for ("approval", "MFA", "Conditional
  Access") under the role name, with a tooltip that names the authentication context; the
  activation sheet lists the same notices.

## [1.2.2] - 2026-09-06

### Changed

- macOS: the DMG and the app are signed with a Developer ID and notarized. The disk image opens
  without a Gatekeeper prompt, and the `xattr -d com.apple.quarantine` step is no longer needed.
  Own-app accounts added on an earlier unsigned build sign in once more, since signed builds use
  the Microsoft authentication broker instead of the loopback flow.

## [1.2.1] - 2026-09-06

### Fixed

- Windows: the MSI's license dialog showed an empty box; it now shows the MIT license.
- The release workflow proposes the Homebrew cask bump as a pull request instead of pushing to
  `main`, which the branch rules refuse.

## [1.2.0] - 2026-09-06

The first release with one version number for both apps: from here on a `v<version>` tag
builds the macOS DMG and the Windows MSIs from the same commit and publishes them as one
release. Elevate for Windows 1.0.0 (tag `windows-v1.0.0`) stays as history.

### Added

- Elevate for Windows: a Windows 11 system-tray flyout with the macOS app's phase-1 and phase-2
  functionality, built with WinUI 3 on .NET 10. Multi-account sign-in with your own app
  registration through the Windows account picker (WAM), a custom client ID, or the Azure CLI and
  Azure PowerShell apps; tenant discovery; Entra directory, Azure resource and PIM-for-Groups
  roles with policies, remembered reasons, bulk activation, scheduled starts, deactivation with
  the five-minute lock, cancelling pending requests, live countdowns and expiry toasts with
  Extend; manual roles for tenants that refuse discovery; offline awareness; start with sign-in.
  Distributed as unsigned per-user x64 and arm64 MSIs (a winget manifest for `Reothor.Elevate`
  is generated but not yet submitted). The Windows and macOS apps
  share the `state.json` schema.
- Elevate for Windows, phase 3: activation profiles (save from the bulk bar, a chip row under the
  pivots, Manage and Run windows), Ctrl-click quick activation and a global shortcut that runs a
  profile, a pinned Approvals group with Approve/Deny, approval toasts and a tray badge, Copy
  diagnostics and a daily update check with an in-flyout banner. The flyout's bulk bar and footer
  stay visible when the list is tall.

### Changed

- One release for both platforms, titled "Elevate x.y.z", with the changelog section, the
  install steps and the SHA-256 of every asset in its notes.
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

[Unreleased]: https://github.com/FrodeHus/elevate/compare/v1.2.4...HEAD
[1.2.4]: https://github.com/FrodeHus/elevate/compare/v1.2.3...v1.2.4
[1.2.3]: https://github.com/FrodeHus/elevate/compare/v1.2.2...v1.2.3
[1.2.2]: https://github.com/FrodeHus/elevate/compare/v1.2.1...v1.2.2
[1.2.1]: https://github.com/FrodeHus/elevate/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/FrodeHus/elevate/compare/v1.0.2...v1.2.0
[1.0.2]: https://github.com/FrodeHus/elevate/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/FrodeHus/elevate/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/FrodeHus/elevate/releases/tag/v1.0.0
