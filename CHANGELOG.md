# Changelog

All notable changes to Elevate are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- macOS and Windows: the run and deactivate reviews for a profile show a checkbox next to each
  role, checked by default. Unchecking a role omits it from that one run or deactivation pass;
  nothing is remembered onto the profile, and an omitted role stays available to a later pass.
### Fixed

- macOS, Windows and CLI: deactivating a role no longer fails with "Revoked". Microsoft Graph and
  Azure Resource Manager report a completed self-deactivation with that status, and the
  confirmation check added for profile deactivation only accepted "Provisioned".

## [1.6.3] - 2026-09-12

### Added

- macOS and Windows: the tenant menu gains an **Open…** submenu that opens the Azure Portal, the
  Entra and Intune admin centers, and the Defender and Purview portals directly in that tenant.
  The browser session picks the account; Microsoft's sign-in page prompts with the tenant already
  fixed when it has none.

### Changed

- macOS and Windows: profile runs can now deactivate only the exact role assignments they
  activated. Per-role eligibility and results survive restarts, partial failures can be retried,
  and confirmed deactivation uses Elevate's chevrons pulsing downward before showing success.
- macOS and Windows: per-role progress in activation dialogs and profile runs now uses Elevate's
  chevrons pulsing upward. Confirmed activation morphs them into a green circle and check mark;
  pending approval, scheduled requests and failures keep their distinct statuses. The animation
  respects reduced-motion settings, and activation dialogs briefly hold the success result before closing.

## [1.6.2] - 2026-09-10

### Added

- CLI: `elevate config set client-id shared` selects the shared Elevate app registration without
  pasting its GUID and states the no-SLA caveat once; `elevate config` then shows
  `shared Elevate app` (and `--json` gains `clientIdKind`: `shared`, `own` or null). New
  `elevate consent [--tenant <id>] [--open]` prints the admin consent link for the configured
  registration — the `organizations` endpoint by default, the shared app's consent result page as
  the redirect when the shared id is in effect and `nativeclient` otherwise, matching the apps.
  `elevate diagnostics` gains the `Client id: shared Elevate app` / `own registration` /
  `not set` line.
### Fixed

- Windows: background refreshes (the timer, wake, launch, network restore and opening the
  flyout) and the access package poll no longer open a browser tab or account-picker dialog when
  a tenant's silent token refresh needs a sign-in — a tenant whose Conditional Access demands
  fresh MFA on every refresh could reopen the tab every minute while a role was active. Such a
  tenant keeps its known rows, is not shown as a red error, and its pill lists "Sign-in needed to
  refresh" with the instruction to press Refresh; only user actions (Refresh, Sign in again, add
  tenant, activation, approvals, Retry discovery, the access packages window) may prompt. Same
  trade-off as the macOS fix: a tenant that cannot refresh silently goes stale until the user
  refreshes it.
### Added

- Windows: the optional shared Elevate app registration, matching macOS 1.6.1. The setup panel
  gains "Quick start with the shared Elevate app…" between "Open Settings…" and "Continue with the
  Azure CLI app", behind a confirmation dialog stating the no-SLA caveat; it applies the shared id
  through the normal client-id path. The "No accounts" state gains "Add account…" and "Grant admin
  consent…" while the shared id is in effect, and Settings › Entra app registration gains the same
  quick-start button (hidden when `ClientId` is managed), a "Shared Elevate app — no SLA" caption
  and "Grant admin consent…", which opens the `organizations` admin consent link. Per-tenant
  consent links redirect to the consent result page for the shared app and keep `nativeclient` for
  own registrations. Diagnostics reports `Client id: shared Elevate app`, `own registration` or
  `not set` — never the id.

## [1.6.1] - 2026-09-10

### Added

- macOS: an optional shared Elevate app registration. The Elevate project publishes one
  multi-tenant, public-client registration (`c9011cc5-7422-4630-a432-73ff4df5834e`, no secret, no
  app-only permissions, the same delegated scopes as the guide's permission table) that any tenant
  can consent to, so trying Elevate no longer requires creating a registration first. The setup
  panel gains "Quick start with the shared Elevate app…" between "Open Settings…" and "Continue
  with the Azure CLI app", behind a confirmation dialog stating the caveat; the "No accounts" state
  and Settings › Entra app registration gain "Grant admin consent…", which opens the
  `organizations` admin consent link. Settings shows "Shared Elevate app — no SLA" while the shared
  id is in effect, and Diagnostics reports `Client id: shared Elevate app`, `own registration` or
  `not set` — never the id.
- Docs: [docs/shared-app-registration.md](docs/shared-app-registration.md) — what the shared app
  is, its security model, what the maintainer can and cannot do, a "Known risks of a shared
  multi-tenant registration" section, the no-SLA statement, when to register your own instead, the
  admin consent link and what an administrator sees (including Microsoft's unverified-publisher
  warning), and the tenant-side controls. The app registration guide gains a "Do you need your
  own?" section, and the getting-started guide, troubleshooting, the READMEs, the managed
  `ClientId` key reference and the product page point at it.
- Site: `consent.html`, the admin consent result page the shared registration redirects to, and a
  privacy-page paragraph stating that using the shared registration sends nothing to the
  maintainer.
- macOS: `Elevate-<version>.pkg` now carries the `elevate` CLI inside the app
  (`Elevate.app/Contents/Helpers/elevate`, Apple Silicon) and links `/usr/local/bin/elevate` to
  it, so a Jamf or Intune rollout — or `sudo installer -pkg` — installs the app and the CLI at the
  same version. The helper is signed with the hardened runtime and notarized with the app.
- Windows: the MSI installs `elevate.exe` in a `cli` folder under the app and adds it to the
  user's PATH; uninstalling removes both.
- CLI: `elevate update` tells you how to upgrade for the way this copy was installed (with the
  app, the formula, winget or an archive).

### Changed

- macOS and Windows: the sign-in method previously shown as "Own app registration" is now called
  "Entra app registration", since the registration in effect may be your own, your company's, or
  the shared Elevate app. The documentation follows the new name.
- macOS: the tenant menu's "Open admin consent link…" is now offered for every account signed in
  with the Entra app registration method, not only after discovery fell back to manual roles or
  groups became unavailable, so an administrator can re-consent after a scope is added before
  anything fails.
- macOS and Windows: the "Custom app" sign-in method is now "Company app (client ID)", with a
  caption naming the two cases it is for: a registration that lists only `http://localhost`, or a
  second registration alongside the one in Settings.
- Homebrew: the `elevate` cask installs the pkg instead of the DMG, so `brew install --cask
  frodehus/elevate/elevate` now yields the app and the CLI (and asks for your password, as pkg
  casks do). The DMG is unchanged: the app alone.

### Deprecated

- Homebrew: the `elevate-cli` formula. The cask and the pkg install the CLI on Apple Silicon Macs;
  the formula stays one more release for Linux and Intel Macs, which keep the
  `elevate-cli-<version>-<rid>` archives afterwards.

### Fixed

- macOS: background refreshes no longer open the browser or an auth sheet. The refresh timer,
  wake, launch, the network coming back, panel opens and the access package poll now acquire
  tokens silently only; a tenant whose sign-in cannot be renewed silently keeps its last rows and
  shows a "Sign-in needed to refresh" limitation until you press Refresh or open its access
  packages, which still prompt as before. Previously an Azure CLI, Azure PowerShell or custom
  client-id account in a tenant that demands fresh MFA could pop a browser tab every minute while a
  role was active.

## [1.6.0] - 2026-09-09

### Added

- macOS, Windows and CLI: organization-managed configuration. Seven keys — `ClientId`,
  `DisableUpdateCheck`, `AllowedSignInMethods`, `AllowedTenants`, `PinnedTenants`,
  `ManagedProfiles` and `ManagedProfilesUrl` — arrive from macOS managed preferences
  (`no.reothor.elevate`), `HKLM`/`HKCU\SOFTWARE\Policies\Reothor\Elevate` on Windows, or
  `/etc/elevate/managed.json` for the CLI on macOS and Linux. A managed value wins over the user's
  and over the default; the setting renders disabled with a "Managed by your organization" caption
  and writes to it are refused. Locking is per key: a key you do not push leaves the choice to the
  user, and an invalid value is ignored on its own with a warning.
- macOS, Windows and CLI: with `ClientId` pushed, a fresh install skips setup entirely;
  `DisableUpdateCheck` stops the daily GitHub check and replaces the update button with a caption.
  Settings gains a "Managed by your organization" section listing the keys in effect and any
  warnings, and Diagnostics a "Managed configuration:" section with the source, the key names and
  the warnings — never the values.
- macOS, Windows and CLI: `AllowedSignInMethods` hides the other methods from Add account and
  refuses them in `elevate login --method`; an account added earlier with a method that is no
  longer permitted keeps working, with a caption. `AllowedTenants` limits tenant discovery and
  manual adds (an account's home tenant is always allowed, and tenants that are no longer permitted
  are dropped at launch); `PinnedTenants` are tracked automatically for every account that can
  reach them and cannot be removed.
- macOS, Windows and CLI: an organization can publish activation profiles, inline with
  `ManagedProfiles` or from an https URL with `ManagedProfilesUrl` (fetched once a day, the last
  copy kept when a fetch fails, the two merged by id with the fetched one winning). Published
  profiles are listed, runnable and bindable to the shortcut, but cannot be renamed, edited, pinned
  or deleted; a role the account is not eligible for plans as "not eligible · skipped". New
  `elevate profiles export <name>` prints one of your own profiles in the published format.
- CLI: `elevate config` gains a `Source` column (`managed`, `user`, `default`) and
  `elevate config managed [--file <path>]` prints the origin, the keys in effect and the warnings,
  with `--file` as a dry run for a `managed.json` before you deploy it.
- Releases carry `Elevate-<version>.pkg` for silent macOS deployment through Jamf or Intune
  (signed and notarized when the installer signing secrets are set, unsigned otherwise) and
  `Elevate-enterprise-kit-<version>.zip` with the ADMX/ADML, a mobileconfig, an Intune preference
  plist, a Jamf custom schema, a `managed.json` template, the key reference and a worked example.
- Docs: `docs/enterprise/` — the model and the kit, the key reference, and rollout guides for Jamf
  Pro, Intune on macOS, Intune on Windows, Group Policy, the CLI (with an Ansible task and a Jamf
  script), publishing profiles, and troubleshooting.
- CLI: `elevate run [--profile NAME] [--role ROLE…] [--deactivate-after] -- <command>` activates
  what is named, waits until it is active (approvals and scheduled starts included, with a line
  saying what it waits for), pauses for a group claim to propagate, then runs the command with the
  terminal's own stdin and stdout and exits with its code. Activations last 10 minutes by default,
  just enough for one command; `--duration` overrides without touching the remembered durations.
- CLI: `elevate init bash|zsh|fish|pwsh` prints a shell hook that wraps az, kubectl, terraform and
  helm and suggests `elevate run` when one of them fails with an authorization error.
- macOS, Windows and CLI: after an Azure resource role or a group membership activates, a one-line
  hint says the Azure CLI, Azure PowerShell and kubelogin caches predate it, with the commands that
  refresh them (`az login`, `kubelogin remove-tokens`). The apps
  offer "Copy command"; the hint is dismissable per account, and the CLI hides it with
  `elevate config set token-hint off --account <name>`.

### Fixed

- macOS: the stale-token hint banner grew no taller than one line, so its caption was drawn over
  the "Active now" section and the rows below it. It now wraps to the height it needs and pushes
  the list down, with the message and the advice on separate lines.
- macOS, Windows and CLI: the stale-token hint named `az account get-access-token --force-refresh`,
  a flag the Azure CLI does not have. It now says to run `az login` again, which is what actually
  gets a token carrying the new assignment.

### Changed

- Core (macOS and Windows): the tenant status glyph now shows an informational icon for mere
  capability limitations (Entra roles view-only, Azure resource roles off, PIM for Groups off);
  the red/critical warning triangle is reserved for an actual discovery or refresh failure.

## [1.5.0] - 2026-09-08

### Added

- Core (macOS and Windows): a profile can be pinned, at most four at a time, ahead of the pinned
  profile row in the panel. The flag is written to `state.json` only when set, so files stay
  readable by older versions and macOS-written files round-trip unchanged on Windows.
- macOS: the profiles row shows only pinned profiles, in one row that never wraps, so many
  profiles no longer push the roles down. Chips show the name only; the count is in the
  tooltip and the All list. "All N" opens a searchable list where Return runs the
  first match, the star pins or unpins, and each row and chip has a menu with run, run with the last
  reason, pin, manage and delete.
- macOS: the Profiles window edits a profile in place. Select it on the left; rename, pin, bind the
  global shortcut, change the duration each role proposes, remove roles, and "Add roles…" picks more
  from every account and tenant with search and a kind filter. "Edit…" on a chip opens the window
  on that profile. The old flow, where Edit reloaded the panel's selection and "Update profile"
  saved it, is gone.
- Windows: the same profiles redesign as macOS. The flyout row shows pinned profiles as chips in
  one row and "All N" opens a searchable list with Enter to run, the star to pin, and a right-click
  menu; the Profiles window edits a profile in place, with "Add roles…" picking from every account
  and tenant. Ctrl-click keeps running a chip with the last reason and durations.

## [1.4.0] - 2026-09-08

### Added

- Windows Core: the access package layer (scope, models, provider, diff, new-role tracker and the
  per-tenant state records) is ported to `Elevate.Core`, so `state.json` keeps one schema across
  the macOS app, the Windows app and the CLI.
- CLI: `elevate packages list|requests|assigned|request|cancel` for access packages. Tables by
  default, `--json` for scripts; requests need a justification and, when several policies apply,
  `--policy`; packages whose policy asks questions are handed to My Access with a link.
- Access packages: each tenant whose sign-in carries the `EntitlementMgmt-SubjectAccess.ReadWrite`
  permission shows a box glyph and an "Access packages…" menu item that open a window with
  Available, Requested, Assigned and Declined tabs. Request with a justification and, when several
  apply, a policy; packages that ask questions hand off to the My Access portal. Cancel pending
  requests. Notifications when a request is approved, denied or fails, and when an assignment is
  revoked or expires. Polled on panel open (at most every 15 minutes) and every 8 hours.
- New eligible roles are marked in the panel with a "new" badge and announced once per tenant;
  the marker clears the second time the panel opens.
- The first-run setup panel and the app registration section in Settings link to the
  getting-started and app registration guides on GitHub, on macOS and Windows.
- Windows: the access packages window, the tenant box glyph and menu item, the request dialog with
  the policy picker and My Access hand-off, the polling and toasts, and the "new" role marker,
  matching the macOS app. The own-app sign-in asks for the entitlement permission alongside
  User.Read.

### Fixed

- Windows: the flyout's right edge no longer clips the row buttons and the header tools; the
  window sized its outer frame, not its content, so the client area came out one frame narrower.
- Access packages: an assignment the service already reports as expired is announced as expired,
  not revoked, when this machine's clock has not reached the end date yet; and an assignment with
  a known end date no longer gets a second "expired" toast from the next poll on top of the timed
  one.
- New-role marker: panel opens counted while a marker was showing no longer carry over after the
  marked roles disappear, so the next new role is highlighted for its full two opens.
- Windows: a saved `state.json` holding an access package state this build has no name for (one
  written by a newer build) loads with that state as unknown instead of being set aside as
  unreadable along with every account, tenant and profile in it.

### Changed

- The app registration gains one user-consentable Graph permission; see
  `docs/entra-app-registration.md`.

## [1.3.0] - 2026-09-07

### Added

- CLI: `elevate`, a command-line counterpart for Linux, macOS and Windows built on the same
  Core as the Windows app. Sign in with any of the four methods through the browser or a device
  code; list roles, activate, extend, deactivate and cancel; save, run and import profiles;
  approve and deny requests; manage tenants and manual roles; `--json` output and exit codes for
  scripts; shell completion. Installed with Homebrew (`frodehus/elevate/elevate-cli`), winget
  (`Reothor.Elevate.CLI`, manifest prepared) or as a single binary from the release.

### Changed

- Windows: the installer dialogs show the Elevate icon on a navy side panel and in the banner
  instead of the stock WiX artwork.

## [1.2.9] - 2026-09-07

### Fixed

- Windows: 1.2.8 crashed at startup while registering the Ctrl+, shortcut; the shortcut is read
  from the key event instead.

## [1.2.8] - 2026-09-07

### Changed

- Windows: durations read as units ("2 h 41 min", "46 min", "< 1 min") instead of HH:MM, in the
  Active now rows, approvals, the decision window, Save profile and the activation toast.
- Windows: account rows lead with the person's name; the address, tenant and "home" marker are
  the caption. The sign-in method moved to a tooltip on the avatar and a heading in the account
  menu. "N active" on account and tenant rows is secondary text.
- Windows: Sign out, Remove tenant and Delete profile ask first and say what Elevate forgets;
  active assignments in Entra are never changed by them.
- Windows: Settings applies the client ID on Enter or when the field loses focus, still behind the
  "Sign out and change" confirmation; the Save button is gone.
- Windows: the flyout answers Ctrl+F (filter), F5 (refresh) and Ctrl+, (Settings), named in the
  tooltips; the Entra / Azure / Groups segments no longer carry active counts (the Active now
  header and the tenant rows keep theirs); the Profiles row has a plain section label and a
  Manage… button; the "manual roles" pill is neutral; "No roles configured." offers Configure…
  inline; the setup buttons share one width, with the accent on Open Settings… only.

### Fixed

- Windows: Narrator reads the state of each role's status dot ("Active", "Awaiting approval", …)
  and names the role on select-mode checkboxes. Pending states are orange rather than the caution
  yellow that vanished on a light background; the "Privileged" tag in Configure roles uses primary
  text; the flyout list skips its row animations while animations are off in Accessibility settings.

## [1.2.7] - 2026-09-07

### Changed

- macOS: durations read as units ("2 h 41 min", "46 min", "< 1 min") instead of HH:MM, in the
  Active now rows, approvals, the decision sheet, Save profile, Run profile and the activation
  notification.
- macOS: account rows lead with the person's name; the address, tenant and "home" marker are the
  caption. The sign-in method moved to a tooltip on the account glyph and a heading in the account
  menu. "Sign-in needed" is a warning glyph beside the Sign in button instead of a pill.
- macOS: every task window names its task in the title bar (Add account, Activate 3 roles, Deny
  request, …) and no longer repeats a heading inside.
- macOS: Settings applies the client ID on Return or when the field loses focus, still behind the
  "Sign out and change" confirmation; the Save button is gone.
- macOS: Sign out, Remove tenant and Delete profile ask first and say what Elevate forgets; active
  assignments in Entra are never changed by them.
- macOS: the panel answers ⌘F (filter), ⌘R (refresh), ⌘, (Settings) and ⌘Q (Quit).
- macOS: the bulk activation sheet lays each tenant out as a grid, so role names take the width
  the picker and labels leave instead of truncating.
- macOS: the Entra / Azure / Groups segments no longer carry active counts; the Active now header
  and the tenant rows keep theirs.
- macOS: the Profiles row has a plain section label and a Manage… button; Manage profiles drops
  the drag-handle glyph; the "manual roles" pill is neutral with a tooltip; "No roles configured."
  offers Configure… inline; the decision sheet aligns its labels; Configure roles insets its tabs;
  the setup buttons share one width.

### Fixed

- macOS: VoiceOver reads the state of each role's status dot ("Active", "Awaiting approval", …)
  and names the role on select-mode checkboxes. Pending states are orange rather than a yellow
  that vanished on a light background; small green and orange text became secondary text or a
  pill; the tenant · account captions are one size larger; collapse animations respect Reduce
  Motion; role names no longer wrap in select mode.

## [1.2.6] - 2026-09-07

### Changed

- macOS: an account whose saved sign-in is gone at launch is no longer removed together with its
  tenants and configured roles. It stays in the list marked "Sign-in needed" with a Sign in
  button (also under the account menu) that signs in again with the same method; refreshes skip
  it until then. Sign out remains the way to remove the account.

## [1.2.5] - 2026-09-06

### Changed

- macOS: tenant and account headers show one status glyph (orange for limitations, red for a
  failed discovery or refresh) instead of a run of pills; hover for a summary, click for a popover
  listing each limitation with its reason. The "manual roles" pill stays.
- macOS: tenant headers no longer repeat the account address and sign-in method.
- macOS: the activation sheet and the profile run sheet box each tenant's rows under the tenant
  name, with the account as a caption.
- macOS: the custom client id sign-in method reads "Custom app".

### Fixed

- macOS: pinned account and section headers are opaque, so rows scrolling underneath no longer
  show through them in a Liquid Glass window.

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

[Unreleased]: https://github.com/FrodeHus/elevate/compare/v1.6.3...HEAD
[1.6.3]: https://github.com/FrodeHus/elevate/compare/v1.6.2...v1.6.3
[1.6.2]: https://github.com/FrodeHus/elevate/compare/v1.6.1...v1.6.2
[1.6.1]: https://github.com/FrodeHus/elevate/compare/v1.6.0...v1.6.1
[1.6.0]: https://github.com/FrodeHus/elevate/compare/v1.5.0...v1.6.0
[1.5.0]: https://github.com/FrodeHus/elevate/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/FrodeHus/elevate/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/FrodeHus/elevate/compare/v1.2.9...v1.3.0
[1.2.9]: https://github.com/FrodeHus/elevate/compare/v1.2.8...v1.2.9
[1.2.8]: https://github.com/FrodeHus/elevate/compare/v1.2.7...v1.2.8
[1.2.7]: https://github.com/FrodeHus/elevate/compare/v1.2.6...v1.2.7
[1.2.6]: https://github.com/FrodeHus/elevate/compare/v1.2.5...v1.2.6
[1.2.5]: https://github.com/FrodeHus/elevate/compare/v1.2.4...v1.2.5
[1.2.4]: https://github.com/FrodeHus/elevate/compare/v1.2.3...v1.2.4
[1.2.3]: https://github.com/FrodeHus/elevate/compare/v1.2.2...v1.2.3
[1.2.2]: https://github.com/FrodeHus/elevate/compare/v1.2.1...v1.2.2
[1.2.1]: https://github.com/FrodeHus/elevate/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/FrodeHus/elevate/compare/v1.0.2...v1.2.0
[1.0.2]: https://github.com/FrodeHus/elevate/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/FrodeHus/elevate/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/FrodeHus/elevate/releases/tag/v1.0.0
