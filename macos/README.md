# Elevate for macOS

Elevate is a macOS 26 menu bar app for activating Microsoft Entra PIM roles across several accounts and tenants.

**Tabs.** The panel has three tabs: Entra (directory roles), Azure (resource roles) and Groups (PIM
for Groups). An "Active now" section pinned above the tabs lists everything active or awaiting
approval across every account, sorted by soonest expiry, each row with its own countdown and
Deactivate; it collapses on click and remembers that choice. The search button in the header
filters the visible rows by name, caption, tenant and account, hiding any account or tenant that has
no match. Extend appears next to the countdown once a role is within 15 minutes of expiring and
re-activates it with the remembered reason; when a role expires anyway, a notification offers
"Activate again". The menu bar icon shows a warning shield when something expires within 5 minutes
and a clock when a request awaits approval.

**Groups.** The Groups tab lists your PIM for Groups memberships and ownerships; activation, the
countdown and Deactivate work exactly as they do for roles. Because a group can carry Entra or Azure
roles, those roles are re-read a few seconds after a group activation so the Entra and Azure tabs
catch up. Groups need the three `*.AzureADGroup` delegated permissions below, so they are available
for your own app registration and for a custom app registration that carries them — never for the
Microsoft first-party Azure CLI or Azure PowerShell apps. A first-party account is never read for
groups at all; its Groups tab explains that the sign-in method supports Azure resource roles only. A
tenant where the group read is refused for permissions shows a "Groups off" pill with the reason.

**Profiles.** Select mode turns the rows into checkboxes so you can pick roles and groups across
every tab and account at once; "Save as profile…" names that set and pins it as a chip under the
tabs. Clicking a chip opens a confirmation sheet that plans the run first: entries already active or
awaiting approval are listed as skipped, the rest keep the duration you last chose for them and the
reason you last gave, and approval-required entries are requested and come back as pending. "Manage…"
renames profiles, reorders them by dragging, deletes them, and Edit reopens the profile's selection
in the panel so you can add or remove entries — "Update profile" saves the change. Profiles are
stored with the rest of the app state in `state.json`.

**Shortcuts.** Option-click on Activate, Extend or a profile chip activates immediately with the
last reason and duration, skipping the dialog; the dialog still opens when a reason is missing, a
ticket is required, approval is required (single roles), or a profile's roles have not loaded. A
notification reports the outcome. "Start at" in the activation dialog and the run sheet schedules
the activation for a future time; scheduled entries appear in Active now with "starts in …" and a
Cancel button, and the menu bar shows the clock badge. Settings → Global shortcut: record a key
combination (at least one of ⌘ ⌃ ⌥) and pick a profile; pressing it runs the profile like
Option-clicking its chip, or opens the run sheet when input is needed. macOS offers no supported
way to open a menu bar panel from a shortcut, so the shortcut runs a profile instead.

**Approvals.** When you are an approver, requests awaiting your decision appear in a pinned
Approvals section above Active now, across all accounts and tenants: requester, role or group,
tenant, requested duration, and the requester's reason on hover. Approve and Deny open a short
sheet with a justification (required for Deny). A notification announces each new request; the
menu bar shows a person-with-clock glyph while any are pending. Extend and renew requests (and any
request type other than an activation) cannot be decided through the Microsoft APIs and are marked
"Decide in the portal". Accounts added with the Azure CLI or Azure PowerShell app see Azure resource
approvals only. A tenant that refuses the approver read simply shows nothing.

## Sign-in methods

> **Limitation:** Microsoft's Azure CLI and Azure PowerShell apps can list PIM schedules but are not pre-authorised for `RoleAssignmentSchedule.ReadWrite.Directory` (admin-consent only). An account added that way **supports Azure resource roles only**: Elevate does not call the Graph PIM APIs for it at all, so no Entra roles are discovered or activated and no permission errors appear on refresh. The add-account dialog says so and the account and its tenants carry an "Azure roles only" pill. No other well-known public client with a loopback redirect carries that scope; your own app registration always works once consented.

Elevate can add an account in two ways, chosen per account in "Add account…":

- **Your own Entra app registration** ("Own app registration"). Uses the client ID from Settings.
  It gives Elevate exactly the permissions you grant it, but each tenant needs an admin to
  consent (see Prerequisites below). A signed build signs in with MSAL through the
  `msauth.<bundle id>://auth` redirect; an **unsigned (ad-hoc) build** signs in through the
  browser with the loopback flow instead — MSAL keeps its token cache in a shared keychain group
  such a build has no entitlement for. Everything else about the method is identical; the
  unsigned build only needs `http://localhost` registered as a redirect URI under the "Mobile and
  desktop applications" platform (the setup script and the guide already add it). Settings shows
  "via loopback" next to the version when that is the active transport.
- **A custom app registration through the loopback flow** ("Custom app (loopback)"). Any
  public-client registration you have a client ID for, such as a company-wide PIM app that has
  no macOS platform configured. It needs `http://localhost` registered as a redirect URI
  under the "Mobile and desktop applications" platform (that platform marks it public-client, so
  no secret is used and the "Allow public client flows" toggle is not required); Elevate reads the granted scopes from the token after sign-in
  and marks the account "Azure roles only" if the Entra write scope is missing. The last ID is remembered.
- **A Microsoft first-party app** ("Azure CLI app" or "Azure PowerShell app"). No registration
  and no consent: these client IDs are already trusted in every tenant that allows the Azure CLI
  or Azure PowerShell. Sign-in opens your default browser and comes back to
  `http://localhost:<random port>`, a loopback redirect Microsoft accepts for public clients
  without any redirect URI being registered. Nothing listens on that port outside the sign-in
  itself, and the refresh token is stored in your Keychain (this device only).

Caveats:

- Conditional Access can block these apps. A tenant that blocks the Azure CLI app will refuse
  sign-in with it — try the Azure PowerShell app, and if the tenant blocks public clients
  altogether, use your own app registration.
- The same account cannot be added twice under different methods; sign it out first.
- Changing the client ID in Settings only affects own-app accounts: they are signed out and
  removed. Azure CLI and Azure PowerShell accounts and their tenants are left alone.
- An own-app account added by a signed build is signed out when the same state is opened by an
  unsigned build (and the other way round): the two keep their tokens in different places. Add
  the account again.

## Install

**Requirements:** macOS 26 (Tahoe), and an Entra app registration — either your own or a company one
— to sign in with; the Microsoft Azure CLI or Azure PowerShell app needs no registration but covers
Azure resource roles only. Building from source is [below](#build-and-run).

Homebrew (the cask lives in this repository, which doubles as a tap):

```bash
brew tap FrodeHus/elevate https://github.com/FrodeHus/elevate
brew trust frodehus/elevate        # Homebrew 6 requires trusting third-party taps
brew install --cask frodehus/elevate/elevate
```

Plain `brew install --cask elevate` does not resolve: this tap is not a `homebrew-`
named repository, so the fully qualified `frodehus/elevate/elevate` name is required.

Or download the DMG from the [latest release](https://github.com/FrodeHus/elevate/releases/latest) —
the asset is named `Elevate-<version>.dmg`, with `Elevate-<version>.dmg.sha256` next to it — open it
and drag **Elevate** to Applications.

**Upgrade:**

```bash
brew upgrade --cask frodehus/elevate/elevate
```

Elevate also checks the GitHub releases API for a newer version once a day and shows a notice in
the panel when one is available.

### Signing

Releases from 1.2.2 on are signed with a Developer ID and notarized, so the DMG and the app open
without Gatekeeper prompts. Releases before 1.2.2 were ad-hoc signed; if you still run one, remove
the quarantine flag once with `xattr -d com.apple.quarantine /Applications/Elevate.app` or approve
it under System Settings → Privacy & Security. Accounts added through the own-app method on an
unsigned build sign in once more after upgrading, because signed builds use MSAL instead of the
loopback flow. How the release is signed: [docs/releasing.md](../docs/releasing.md).

## Prerequisites

Only the own-app method needs an app registration; the first-party methods need none of this.

1. Xcode 26.6 or newer, `brew install xcodegen`.
2. An Entra app registration (multi-tenant, public client) with redirect URI
   `msauth.no.reothor.elevate://auth` under the iOS/macOS platform — see
   [Setting up the Entra app registration](../docs/entra-app-registration.md) for the full
   walkthrough (Azure CLI script or portal steps) and the permission list, including the three
   `*.AzureADGroup` scopes the Groups tab needs.
3. Launch Elevate, open Settings (⌘,) from the panel, and paste the application (client) ID.

## Build and run

```bash
cd macos
xcodegen generate
xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build
open build/Build/Products/Debug/Elevate.app
```

Core tests: `swift test`.

App tests:

```bash
xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates test
```

> **Note:** running `xcodebuild test` with signing disabled (as CI does, to avoid needing a
> signing identity) into the same `-derivedDataPath build` used above replaces the signed Debug
> app with an unsigned one — its own-app sign-in then shows "Unavailable on unsigned builds".
> If you need an unsigned test run locally, point it at a separate derived data path instead,
> e.g. `-derivedDataPath build-unsigned`, so the signed `build/` output for `open
> build/Build/Products/Debug/Elevate.app` is left alone.

## Tenants that refuse consent

If a tenant admin has not consented, discovery fails and the tenant switches to
"manual roles". Use the tenant menu → Configure known PIM roles… to pick the roles
you hold. Activation still requires `RoleAssignmentSchedule.ReadWrite.Directory`;
the tenant menu offers an admin-consent link you can forward.

## Azure resource roles

Eligibilities on management groups, subscriptions, resource groups and resources
appear as rows with the scope under the role name. The first Azure call in a
tenant asks you to consent to `user_impersonation` for Azure Service Management;
no admin consent is needed. A tenant where you have no Azure access simply shows
no Azure rows and no error: the first refused Azure read switches Azure off for
that tenant, marked with a quiet "Azure off" caption in the tenant header whose
tooltip gives the reason. Nothing else is retried there until you pick Retry
discovery from the tenant menu, which clears it. Manual Azure roles are entered
as scope + role name and resolved to the role definition when you activate; the
row is re-keyed to the resolved role definition id at that point.

## Manual smoke test

- Add account → browser sign-in → home tenant appears with eligible roles.
- Activate a role → reason pre-fills on the next activation → green dot and countdown.
- Deactivate → dot clears.
- Select mode → pick roles in two tenants → Activate all → grouped progress.
- Discover tenants… lists other tenants; Add tenant… accepts a domain.
- A role expiring within 5 minutes produces a notification with Extend.

The Entra roles catalogue is regenerated as described in
[CONTRIBUTING.md](../CONTRIBUTING.md#the-entra-roles-catalogue).

UI design canvas: [docs/design/elevate-macos-canvas.html](../docs/design/elevate-macos-canvas.html) (Claude Design artboards for the panel, select mode, activation, profiles).
