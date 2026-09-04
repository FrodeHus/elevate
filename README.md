# PimTray

macOS 26 menu bar app for activating Microsoft Entra PIM roles across several accounts and tenants.

## Prerequisites

1. Xcode 26.6 or newer, `brew install xcodegen`.
2. An Entra app registration (multi-tenant, public client):
   - Authentication → Add a platform → iOS/macOS, bundle ID `no.frodehus.pimtray`.
     The redirect URI becomes `msauth.no.frodehus.pimtray://auth`.
   - Authentication → Advanced settings → Allow public client flows: Yes.
   - API permissions (delegated, Microsoft Graph): `User.Read`,
     `RoleEligibilitySchedule.Read.Directory`, `RoleAssignmentSchedule.ReadWrite.Directory`,
     `RoleManagementPolicy.Read.Directory`. All of these need admin consent per tenant.
   - Optional: Azure Service Management → `user_impersonation` for tenant discovery.
3. `cp PimTrayConfig.plist.example PimTrayConfig.plist` and put the application (client) ID in `ClientId`.

## Build and run

```bash
xcodegen generate
xcodebuild -project PimTray.xcodeproj -scheme PimTrayApp -configuration Debug -derivedDataPath build build
open build/Build/Products/Debug/PimTray.app
```

Core tests: `swift test`.

## Tenants that refuse consent

If a tenant admin has not consented, discovery fails and the tenant switches to
"manual roles". Use the tenant menu → Configure known PIM roles… to pick the roles
you hold. Activation still requires `RoleAssignmentSchedule.ReadWrite.Directory`;
the tenant menu offers an admin-consent link you can forward.

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
