# Elevate for macOS

Elevate is a macOS 26 menu bar app for activating Microsoft Entra PIM roles across several accounts and tenants.

**Groups.** The panel has a Roles tab and a Groups tab. The Groups tab lists your PIM for Groups
memberships and ownerships; activation, the countdown and Deactivate work exactly as they do for
roles. Because a group can carry Entra or Azure roles, those roles are re-read a few seconds after
a group activation so the Roles tab catches up. Groups need the three `*.AzureADGroup` delegated
permissions below, so they are available for your own app registration and for a custom app
registration that carries them — never for the Microsoft first-party Azure CLI or Azure PowerShell
apps. A first-party account is never read for groups at all; its Groups tab explains that the
sign-in method supports Azure resource roles only. A tenant where the group read is refused for
permissions shows a "Groups off" pill with the reason.

## Sign-in methods

> **Limitation:** Microsoft's Azure CLI and Azure PowerShell apps can list PIM schedules but are not pre-authorised for `RoleAssignmentSchedule.ReadWrite.Directory` (admin-consent only). An account added that way **supports Azure resource roles only**: Elevate does not call the Graph PIM APIs for it at all, so no Entra roles are discovered or activated and no permission errors appear on refresh. The add-account dialog says so and the account and its tenants carry an "Azure roles only" pill. No other well-known public client with a loopback redirect carries that scope; your own app registration always works once consented.

Elevate can add an account in two ways, chosen per account in "Add account…":

- **Your own Entra app registration** ("Own app registration"). Sign-in goes through MSAL and
  the client ID from Settings. It gives Elevate exactly the permissions you grant it, but each
  tenant needs an admin to consent (see Prerequisites below).
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

## Regenerating the role catalogue

Save the markdown of https://learn.microsoft.com/entra/identity/role-based-access-control/permissions-reference
and run `perl shared/entra-roles/update-role-catalogue.pl page.md > macos/Sources/ElevateCore/Resources/EntraBuiltInRoles.json (from the repo root)`.
