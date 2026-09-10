# Shared Elevate app registration — plan

Spec: this file's "Design" section is the spec (no separate spec document).

## Design

The Elevate project publishes one multi-tenant public-client registration that any tenant can
consent to, so users no longer have to create their own to get Entra roles, groups and access
packages:

- Display name **Elevate**, application (client) ID `c9011cc5-7422-4630-a432-73ff4df5834e`,
  publisher domain `reothor.no`, home tenant owned by the project. Sign-in audience is
  multi-tenant organizations only. It has no secret, no certificate, no application (app-only)
  permissions, and exactly the nine delegated scopes the app registration guide already lists
  (plus `openid`, `profile`, `offline_access`). Redirect URIs: `msauth.no.reothor.elevate://auth`,
  `http://localhost`, `ms-appx-web://microsoft.aad.brokerplugin/<client id>` and the
  `nativeclient` web URI used only by the consent link. Verified publisher: none (an individual
  cannot enrol in Partner Center); the consent prompt therefore shows "unverified".
- **No SLA.** The registration is offered as a convenience by the maintainer, with no service
  level, no support commitment, and the possibility that it changes or goes away. Organizations
  that need control over the registration (its scopes, redirect URIs, lifetime, Conditional
  Access targeting by their own app id) must create their own following the existing guide and
  push it with the managed `ClientId` key or type it into Settings. Every mention of the shared
  app in the app and the docs carries this caveat.
- The admin consent URL for the shared app before any account exists uses the `organizations`
  tenant segment: `https://login.microsoftonline.com/organizations/v2.0/adminconsent?client_id=…&scope=…&redirect_uri=https://login.microsoftonline.com/common/oauth2/nativeclient`.
  The existing per-tenant consent link (tenant menu) keeps its tenant id.

### macOS app behaviour

- `AppSettings.sharedClientId` = `c9011cc5-7422-4630-a432-73ff4df5834e`.
  `AppSettings.usesSharedClientId` is true when the client id in effect (managed or stored)
  equals it, compared case-insensitively after trimming.
- **Setup panel** (`SetupView`, shown while no client id and no accounts): a third route.
  The shared app is strictly optional: a quick start for trying Elevate or for small tenants,
  never the default. Order top to bottom: "Open Settings…" (borderedProminent, unchanged — own
  or company registration remains the primary path), "Quick start with the shared Elevate app…"
  (plain), "Continue with the Azure CLI app" (plain). The intro caption changes to explain the
  three routes in one sentence each. Clicking the shared-app button opens a confirmation dialog
  (`.confirmationDialog`, title "Use the shared Elevate app?") whose message is exactly:

  > The Elevate project provides an optional multi-tenant app registration for quick starts and testing, so you do not have to create your own. It has no client secret and only the delegated permissions listed in the app registration guide. It is offered as a convenience with no SLA: it may change or be withdrawn at any time. Organizations that need full control should register their own app. An administrator must grant consent once per tenant before sign-in works.

  with actions "Use shared app" and Cancel. Confirming calls `model.applyClientId(AppSettings.sharedClientId)`;
  an error is shown in a red caption below the buttons like Settings does.
- Because `isConfigured` becomes true, the panel switches to the "No accounts" state. That
  `ContentUnavailableView` gains, when `usesSharedClientId` is true, two actions below the
  description: "Add account…" (opens `PanelRoute.addAccount`, prominent) and "Grant admin
  consent…" (opens the `organizations` consent URL in the browser). Description text when the
  shared app is in effect: "Configured with the shared Elevate app. Ask an administrator to grant
  consent once per tenant, then add an account." Without the shared app the state is unchanged.
- **Settings › Entra app registration**: when the client id is not managed, add a row under the
  text field: `Button("Quick start with the shared Elevate app…")` that opens the same confirmation dialog and
  applies the id on confirm (the field's draft follows). When `usesSharedClientId` is true and
  the id is not managed, show `Label("Shared Elevate app — no SLA", systemImage: "person.2")` in
  caption style and a `Button("Grant admin consent…")` that opens the `organizations` consent URL.
  When it *is* managed and equals the shared id, show the same label without the consent button
  (the organization chose it; consent is their admin's job) — actually keep the consent button
  too, it is harmless and useful; only hide the "Use the shared app" button when managed.
  Existing captions about redirect URIs stay for own registrations; when the shared app is in
  effect replace the redirect-URI caption with "The shared registration already lists this
  redirect URI." (the Redirect URI row itself stays).
- `AppModel.sharedAppAdminConsentURL` (no arguments) builds the `organizations` URL with the
  same scope list as `adminConsentURL(identityId:tenantId:)`; refactor so both share one
  private builder taking the tenant segment. It returns nil when `!isConfigured`.
- Diagnostics (`diagnosticsText`) gains one line under the existing app/build info:
  `Client id: shared Elevate app` or `Client id: own registration` or `Client id: not set` —
  never the id itself (the report deliberately has no field for it).
- Consent link in the tenant menu: unchanged (already works for `.ownApp` accounts regardless of
  which id is in effect).

### Documentation

- New `docs/shared-app-registration.md`: what the shared app is, the client id, the security
  model in the admin's terms (delegated only, no credential, tokens stay on the device, what
  the maintainer can and cannot do, what happens if the registration is edited or deleted),
  the "unverified publisher" explanation, the no-SLA statement, a "Known risks of a shared
  multi-tenant registration" section (owner-account compromise enabling a phishing flow with an
  already-consented client id; the maintainer can change scopes or redirect URIs — new scopes
  need fresh consent, redirect changes do not; deletion or disablement breaks sign-in for every
  tenant; no verified publisher; no SLA; tenant cannot restrict the registration's own settings,
  only its service principal), when to register your own instead (frame the shared app as a
  quick start or test path, never the recommended production choice), how to onboard (the in-app button, the consent link with the `organizations`
  segment and a per-tenant form, `az ad app permission admin-consent` does **not** apply to a
  foreign app — use the link; what the admin sees: Microsoft's "this app may be risky"
  warning because there is no verified publisher, then the consent prompt, then the result
  page at https://elevate.reothor.no/consent.html — see Task 4), tenant-side controls (user assignment required, Conditional
  Access targeting the app, reviewing and revoking consent under Enterprise applications),
  and the permission table copied from the registration guide.
- `docs/entra-app-registration.md`: new section 0 "Do you need your own?" pointing at the
  shared app doc, and the intro adjusted so the guide is clearly the own-registration path.
- `docs/getting-started.md` section 2: the shared app becomes the first route, the own
  registration the second, Azure CLI third; screenshots are not regenerated (say the setup
  panel now offers "Use the shared Elevate app…").
- `README.md` quick start: step 1 offers the shared app with the caveat and links the new doc.
- `docs/README.md`, `docs/troubleshooting.md` (consent section mentions the shared app's
  `organizations` link), `docs/enterprise/keys.md` (`ClientId` may be the shared id; state the
  caveat), `enterprise/README.md` placeholder table row, `cli/README.md` (own registration row:
  "or the shared Elevate app: `elevate config set client-id c9011cc5-…`"), `windows/README.md`
  (same), `site/index.html` setup note (offer the shared app first, own registration for
  control, with link), `site/privacy.html` (one paragraph: signing in with the shared
  registration sends nothing to the maintainer; consent is between the tenant and Microsoft).
- `CHANGELOG.md` Unreleased → Added entries for the app and the docs.

## Global Constraints

- Client id literal appears in code exactly once (`AppSettings.sharedClientId`); docs use it in
  full where an admin must copy it.
- Every user-facing mention of the shared app in app copy and docs includes the no-SLA caveat or
  links to the doc that states it.
- Native SwiftUI, no new dependencies. Swift 6 strict concurrency as the target already uses.
- Tests: `swift test` in `macos/` (Core) must stay green; app-layer tests live in
  `macos/Tests/ElevateAppTests` (hosted, run via `xcodebuild test -scheme ElevateApp` from
  `macos/`, see existing files for the `makeModel`/`makeSettings` helpers). Add tests for the
  new model logic; views are not unit-tested.
- Commit per task on branch `shared-app-registration`; no attribution lines in commit messages.

## Tasks

### Task 1: Model support for the shared client id

Files: `macos/Sources/ElevateApp/App/AppSettings.swift`,
`macos/Sources/ElevateApp/App/AppModel.swift`,
`macos/Sources/ElevateApp/App/AppModel+Operations.swift`,
new `macos/Tests/ElevateAppTests/AppModelSharedAppTests.swift`.

1. Add `static let sharedClientId = "c9011cc5-7422-4630-a432-73ff4df5834e"` and
   `var usesSharedClientId: Bool` to `AppSettings` (trimmed, case-insensitive comparison of
   `clientId`).
2. In `AppModel`, add `var usesSharedApp: Bool { settings.usesSharedClientId }` and
   `func sharedAppAdminConsentURL() -> URL?`; extract the URL building from
   `adminConsentURL(identityId:tenantId:)` into a private `adminConsentURL(tenantSegment:)`
   used by both. `sharedAppAdminConsentURL` returns nil when `!isConfigured`.
3. Diagnostics line as specified in Design (find where the build/version lines are emitted in
   `diagnosticsText()` and add it there).
4. Tests (Swift Testing, `@MainActor struct`, use the existing `makeModel`/`makeSettings`
   helpers): `usesSharedClientId` true for the id in any case with whitespace, false for another
   GUID and for empty; `sharedAppAdminConsentURL` nil when unconfigured, and when configured
   with the shared id has host `login.microsoftonline.com`, path
   `/organizations/v2.0/adminconsent`, `client_id` equal to the shared id, `redirect_uri` equal
   to the nativeclient URL and a `scope` containing `RoleAssignmentSchedule.ReadWrite.Directory`;
   `adminConsentURL(identityId:tenantId:)` still uses the tenant id in the path; diagnostics
   text contains `Client id: shared Elevate app` when the shared id is set and never contains
   the id itself.
5. Run the app test target and the Core suite; commit `feat(macos): shared Elevate app client id support`.

### Task 2: Setup panel, empty state and Settings UI

Files: `macos/Sources/ElevateApp/Views/SetupView.swift`,
`macos/Sources/ElevateApp/Views/PanelView.swift`,
`macos/Sources/ElevateApp/Views/SettingsView.swift`, optionally a new
`macos/Sources/ElevateApp/Views/SharedAppConsentDialog.swift` holding the dialog text and a
`ViewModifier` so SetupView and SettingsView share it.

Implement exactly the behaviour in Design › macOS app behaviour. Keep the 260-pt button stack
in SetupView. Use `NSWorkspace.shared.open` for the consent URL. Build with
`xcodebuild -scheme ElevateApp -configuration Debug build` from `macos/` (see `macos/README.md`
for the exact invocation and `-allowProvisioningUpdates`). Commit
`feat(macos): offer the shared Elevate app in setup and Settings`.

### Task 3: Documentation, site and changelog

All the files listed in Design › Documentation. Write `docs/shared-app-registration.md` first,
then the cross-references. Keep the existing voice of the docs (second person, short
paragraphs, tables for facts). Verify links resolve (relative paths exist). Commit
`docs: shared Elevate app registration and onboarding guidance`.

### Task 4: Consent result page for the shared app

Background: the v2 admin consent endpoint redirects to `redirect_uri` with either
`admin_consent=True&tenant=<id>&scope=…` or `error=…&error_description=…`. With the
`nativeclient` redirect the admin lands on Microsoft's "This is not the right page" screen even
though consent succeeded. Before that, admins see Microsoft's "this app may be risky"
interstitial (AADSTS900981) because the registration has no verified publisher; that cannot be
removed and the docs must say so.

Files: `site/consent.html` (new), `macos/Sources/ElevateApp/App/AppModel.swift`,
`macos/Tests/ElevateAppTests/AppModelSharedAppTests.swift`, `docs/shared-app-registration.md`
(the onboarding section, if Task 3 is already merged; otherwise Task 3 writes it with this in
mind).

1. `site/consent.html`: a static page in the product site's style (reuse `styles.css`, no
   external resources, no scripts beyond reading `location.search`) that reads the query string
   and shows one of: success ("Consent granted for tenant <tenant id>. Users in this tenant can
   now add their account in Elevate.") with the granted scopes listed; failure with the
   `error` and `error_description` text; or, with no parameters, a short explanation of what the
   page is. Nothing is sent anywhere; say so on the page.
2. `AppModel`: when `usesSharedApp` is true, both `sharedAppAdminConsentURL()` and
   `adminConsentURL(identityId:tenantId:)` use `https://elevate.reothor.no/consent.html` as the
   `redirect_uri`; own registrations keep the `nativeclient` URI. Add
   `AppSettings.sharedConsentRedirectURI` for the literal. Tests for both cases.
3. The registration must list `https://elevate.reothor.no/consent.html` as a **Web** redirect
   URI; the maintainer registers it (command in the docs and the final summary).
4. Commit `feat: consent result page for the shared Elevate app`.
