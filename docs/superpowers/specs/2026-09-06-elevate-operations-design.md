# Elevate — operations

Date: 2026-09-06. Approved in chat. The user has no Apple Developer Program
membership today, so releases are ad-hoc signed; the workflow switches to
Developer ID signing and notarization when secrets are added.

## 1. Goal

Make Elevate installable by others and operable day to day.

Success criteria:

- **Launch at login** toggle in Settings via `SMAppService.mainApp`, reflecting
  the real state (`.enabled`, `.requiresApproval` → "Approve in System
  Settings → Login Items", `.notRegistered`), with errors shown inline.
- **Copy diagnostics** in Settings copies a plain-text report: app version and
  build, signing state (Developer ID / ad-hoc / development), macOS version,
  accounts (address, sign-in method, tenant count), tenants (display name,
  tenant id, discovery mode, Azure/Groups breakers, Entra activation state),
  profile names, hot key, and the last 50 errors with timestamps. Never tokens,
  client ids or secrets.
- **Check for updates** in Settings, plus an automatic check at most once per
  24 h, compares the running version with the latest GitHub release tag and
  shows a panel notice "Elevate 1.1.0 is available" with an "Open" link to the
  release page. Dismissible; not shown again for that version.
- **Unsigned builds work**: when the app has no application-identifier
  entitlement (ad-hoc signature), refresh tokens go to the legacy keychain
  without an access group; the own-app (MSAL) method is verified on an ad-hoc
  build and either works or is marked unavailable with a reason.
- **Releases**: pushing a tag `vX.Y.Z` builds a Release DMG, creates a GitHub
  Release with the DMG and its SHA-256, and updates `Casks/elevate.rb` on
  main. Developer ID signing and notarization run when the secrets exist;
  otherwise the DMG is ad-hoc signed and the release notes say so. README
  explains installing an unsigned build and the cask command.

## 2. Core (`ElevateCore`)

- `AppVersion` (`Support/AppVersion.swift`): `struct AppVersion: Comparable`
  parsing "1.2.3" (optional leading "v", ignores build metadata),
  `static func isNewer(latestTag:, current:) -> Bool`. Tested.
- `DiagnosticsReport` (`Support/DiagnosticsReport.swift`): pure renderer taking
  a `DiagnosticsInput` value (version, build, signing, os, accounts, tenants,
  profiles, hotKey, errors) and returning the text. Tested for redaction
  (no client id passed in at all) and formatting.
- `ErrorLog` (`Support/ErrorLog.swift`): ring buffer of `(Date, String)`
  capped at 50, `append`, `entries`. Tested.

## 3. App

- `BuildInfo` (`App/BuildInfo.swift`): version/build from the bundle,
  `signingState` from `SecTaskCopyValueForEntitlement("application-identifier")`
  and the presence of a Developer ID signature (`codesign` is not available at
  runtime; use `SecCodeCopySigningInformation` `kSecCodeInfoCertificates` and
  check the leaf's common name prefix "Developer ID Application") →
  `.developerID`, `.development` (has application-identifier but not
  Developer ID), `.adHoc` (no application-identifier).
- `KeychainRefreshTokenStore`: when `BuildInfo.signingState == .adHoc`, omit
  `kSecAttrAccessGroup` and `kSecUseDataProtectionKeychain` from every query
  (legacy login keychain). Existing behaviour otherwise.
- MSAL on ad-hoc: Task 2 of the plan builds an ad-hoc signed copy and tries the
  own-app sign-in. If MSAL fails, `AppModel.isAvailable(.ownApp)` returns
  false on ad-hoc builds and the Add account caption reads "Unavailable on
  unsigned builds; use a custom app registration". If it works, no change.
- `AppModel`: `errorLog` (appended wherever `tenantErrors`, `approvalErrors`,
  `progress[.failed]` and `notice` are set), `diagnosticsText()`,
  `launchAtLogin` state + `setLaunchAtLogin(_:)`, `updateAvailable:
  (version: String, url: URL)?` with `checkForUpdates(force:)` using
  `HTTPClient` against `https://api.github.com/repos/FrodeHus/elevate/releases/latest`
  (`tag_name`, `html_url`), throttled by `AppSettings.lastUpdateCheck` and
  `dismissedUpdateVersion`.
- Settings: sections "General" (Launch at login toggle, version line,
  "Check for updates" button + result line, "Copy diagnostics" button with a
  "Copied" confirmation). Panel: the update notice reuses the existing notice
  banner with an "Open" button (`NSWorkspace.open`).
- `project.yml`: `MARKETING_VERSION: 1.0.0`, `CURRENT_PROJECT_VERSION: 1`,
  Info.plist `CFBundleShortVersionString $(MARKETING_VERSION)`,
  `CFBundleVersion $(CURRENT_PROJECT_VERSION)`; a `Release` configuration.

## 4. Release workflow

`.github/workflows/release.yml` on `push: tags: ['v*']`:

1. Checkout, select Xcode, install XcodeGen, `xcodegen generate`.
2. Derive `VERSION` from the tag (strip `v`) and `BUILD` from
   `github.run_number`; pass `MARKETING_VERSION=$VERSION CURRENT_PROJECT_VERSION=$BUILD`.
3. If `secrets.MACOS_CERT_P12` is set: import the certificate into a temporary
   keychain, build with `CODE_SIGN_IDENTITY="Developer ID Application"`,
   `DEVELOPMENT_TEAM=$APPLE_TEAM_ID`, `CODE_SIGN_STYLE=Manual`, hardened
   runtime, then `xcrun notarytool submit --wait` with `APPLE_ID`,
   `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD`, and `xcrun stapler staple`.
   Otherwise build with signing disabled and `codesign --force --deep -s -`
   (ad-hoc) afterwards.
4. `hdiutil create` a DMG `Elevate-$VERSION.dmg` containing `Elevate.app` and
   an `Applications` symlink; compute SHA-256.
5. `gh release create "$TAG" Elevate-$VERSION.dmg --notes` with a body that
   states signed/notarized or unsigned (with the right-click Open steps).
6. Regenerate `Casks/elevate.rb` (version, sha256, url to the release asset,
   `app "Elevate.app"`, `caveats` for unsigned builds) and commit it to
   `main` with the workflow's token.

`docs/releasing.md`: tagging steps, the five optional secrets and how to
create them, what changes when they are present, and the cask install line.
README (root and macOS): "Install" section with the cask command, the
unsigned-build right-click steps, and the DMG download link pattern.

## 5. Testing

Core: `AppVersionTests`, `DiagnosticsReportTests`, `ErrorLogTests`. App:
build; manual by the user — toggle launch at login, copy diagnostics, check
for updates (before the first release it reports "no releases yet"), and the
ad-hoc verification in Task 2. The workflow is verified by pushing `v1.0.0`
once the branch is merged.
