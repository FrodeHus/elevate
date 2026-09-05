# Elevate

Formerly PimTray. Elevate is a macOS 26 menu bar app for activating Microsoft Entra PIM roles across several accounts and tenants.

## Prerequisites

1. Xcode 26.6 or newer, `brew install xcodegen`.
2. An Entra app registration (multi-tenant, public client):
   - Authentication → Add a platform → iOS/macOS, bundle ID `no.frodehus.elevate`.
     The redirect URI becomes `msauth.no.frodehus.elevate://auth`.
   - Authentication → Add a platform → Web, redirect URI
     `https://login.microsoftonline.com/common/oauth2/nativeclient`, so the
     admin-consent link the app hands out lands on a valid page.
   - Authentication → Advanced settings → Allow public client flows: Yes.
   - API permissions (delegated, Microsoft Graph): `User.Read`,
     `RoleEligibilitySchedule.Read.Directory`, `RoleAssignmentSchedule.ReadWrite.Directory`,
     `RoleManagementPolicy.Read.Directory`. All of these need admin consent per tenant.
   - Azure Service Management → `user_impersonation` (delegated). Required: it covers both
     tenant discovery and every Azure resource role read and activation. User consent is enough.
3. Launch Elevate, open Settings (⌘,) from the panel, and paste the application (client) ID.

## Build and run

```bash
xcodegen generate
xcodebuild -project Elevate.xcodeproj -scheme PimTrayApp -configuration Debug -derivedDataPath build build
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
and run `perl Scripts/update-role-catalogue.pl page.md > Sources/PimTrayCore/Resources/EntraBuiltInRoles.json`.
