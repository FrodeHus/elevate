# Elevate Operations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Launch at login, a diagnostics report, an update check, unsigned-build support, and a tag-driven release workflow producing a DMG, a GitHub Release and a Homebrew cask.

**Architecture:** Core gets three pure helpers (`AppVersion`, `DiagnosticsReport`, `ErrorLog`) with tests. The app gets `BuildInfo` (version, signing state from the code signature), a keychain fallback for ad-hoc builds, launch-at-login via `SMAppService`, an error log fed from the existing error sites, a throttled GitHub release check, and a "General" settings section. A new GitHub Actions workflow builds, signs (Developer ID when secrets exist, ad-hoc otherwise), packages, releases and updates the cask.

**Tech Stack:** Swift 6.2, SwiftUI macOS 26, ServiceManagement, Security, Swift Testing, GitHub Actions, `hdiutil`, `notarytool`, Homebrew cask DSL.

**Spec:** `docs/superpowers/specs/2026-09-06-elevate-operations-design.md`

## Global Constraints

- Paths relative to `macos/` unless they start with `.github/`, `Casks/`, `docs/` or `README.md`. Tests `swift test` (197). Build `xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build 2>&1 | grep -E "error:|BUILD"`. Relaunch `pkill -x Elevate; sleep 1; open build/Build/Products/Debug/Elevate.app`.
- `ElevateCore` imports only Foundation. Swift 6 strict concurrency; no `@unchecked Sendable` in `AppModel`. Never commit `Elevate.xcodeproj`. Swift Testing.
- Diagnostics must never include tokens, client ids, or the user's hot-key-bound profile beyond its name. Error log capped at 50.
- Update check: `https://api.github.com/repos/FrodeHus/elevate/releases/latest`, headers `Accept: application/vnd.github+json`, `User-Agent: Elevate`; 404 → "No releases yet"; throttle 24 h; dismissed version remembered.
- Signing states: `.developerID` (leaf certificate common name starts with "Developer ID Application"), `.development` (application-identifier entitlement present, not Developer ID), `.adHoc` (no application-identifier entitlement).
- Branch `operations` from `main`; commit after every task with the given message.

## File structure

```
Sources/ElevateCore/Support/AppVersion.swift          new
Sources/ElevateCore/Support/DiagnosticsReport.swift   new
Sources/ElevateCore/Support/ErrorLog.swift            new
Sources/ElevateApp/App/BuildInfo.swift                new
Sources/ElevateApp/App/UpdateChecker.swift            new (GitHub latest-release fetch)
Sources/ElevateApp/App/{AppModel,AppSettings}.swift   error log, launch at login, update state, diagnostics
Sources/ElevateApp/Auth/KeychainRefreshTokenStore.swift  ad-hoc fallback
Sources/ElevateApp/Views/{SettingsView,PanelView,AddAccountView}.swift
project.yml, Sources/ElevateApp/Info.plist            versions, Release config
.github/workflows/release.yml                          new
Casks/elevate.rb                                       new (placeholder until the first release)
docs/releasing.md                                      new
README.md, macos/README.md                             install sections
Tests/ElevateCoreTests/{AppVersionTests,DiagnosticsReportTests,ErrorLogTests}.swift
```

---

### Task 1: Core helpers — AppVersion, ErrorLog, DiagnosticsReport

**Files:** the three Core files and their tests.

**Interfaces (produces):**
```swift
public struct AppVersion: Comparable, Equatable, Sendable { public let major: Int, minor: Int, patch: Int
    public init?(_ text: String)   // accepts "1.2.3", "v1.2.3", "1.2.3+45", "1.2"; rejects garbage
    public static func isNewer(latestTag: String, current: String) -> Bool }
public struct ErrorLog: Sendable, Equatable { public init(capacity: Int = 50); public mutating func append(_ message: String, at date: Date = .now); public var entries: [(date: Date, message: String)] }
public struct DiagnosticsInput: Sendable { appVersion, build, signing: String, os: String,
    accounts: [(upn: String, method: String, tenants: Int)], tenants: [(name: String, id: String, mode: String, flags: [String])],
    profiles: [String], hotKey: String?, errors: [(Date, String)] }   // use small public structs, not tuples
public enum DiagnosticsReport { public static func render(_ input: DiagnosticsInput, now: Date = .now) -> String }
```

- [ ] Tests: `AppVersionTests` (parse variants, comparison, `isNewer` incl. equal and older, garbage → false); `ErrorLogTests` (cap at 50 keeps newest, order oldest→newest); `DiagnosticsReportTests` (header lines present, each section, ISO-8601 timestamps, an input with an error message containing "token=abc" is rendered verbatim — the report does not filter, callers must not pass secrets — and a test that the renderer output never contains a given client-id-looking string that was NOT passed, i.e. the input type has no field for it).
- [ ] Implement; `swift test`; commit `git commit -m "Add AppVersion, ErrorLog and DiagnosticsReport"`.

---

### Task 2: BuildInfo, keychain fallback, ad-hoc verification, version numbers

**Files:** `App/BuildInfo.swift` (new), `Auth/KeychainRefreshTokenStore.swift`, `project.yml`, `Sources/ElevateApp/Info.plist`, `App/AppModel.swift` (`isAvailable(.ownApp)`), `Views/AddAccountView.swift` (caption).

- [ ] `BuildInfo`: `static let version: String` (`CFBundleShortVersionString` or "0.0.0"), `static let build: String`, `enum SigningState { case developerID, development, adHoc }`, `static let signingState: SigningState` computed once via `SecTaskCreateFromSelf`/`SecTaskCopyValueForEntitlement("application-identifier")` (nil → `.adHoc`) and `SecCodeCopySelf` + `SecCodeCopySigningInformation(_, SecCSFlags(rawValue: kSecCSSigningInformation), &info)` reading `kSecCodeInfoCertificates` first certificate's `SecCertificateCopySubjectSummary` prefix "Developer ID Application" → `.developerID`; else `.development`. `static var signingDescription: String`.
- [ ] `KeychainRefreshTokenStore.baseQuery`: when `BuildInfo.signingState == .adHoc`, do not set `kSecAttrAccessGroup` nor `kSecUseDataProtectionKeychain`; `allIdentityIds` query likewise. Add a doc comment explaining why.
- [ ] `project.yml`: under `settings.base` add `MARKETING_VERSION: "1.0.0"`, `CURRENT_PROJECT_VERSION: "1"`; Info.plist properties `CFBundleShortVersionString: $(MARKETING_VERSION)`, `CFBundleVersion: $(CURRENT_PROJECT_VERSION)`; add `configs: { Debug: debug, Release: release }` at the top level if XcodeGen needs it for a Release build (it creates both by default; verify with `xcodebuild -list`).
- [ ] **Ad-hoc verification (spike, results recorded in the report):** build a Release copy signed ad-hoc: `xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Release -derivedDataPath build-adhoc CODE_SIGN_IDENTITY="-" CODE_SIGNING_ALLOWED=YES CODE_SIGN_STYLE=Manual DEVELOPMENT_TEAM="" CODE_SIGN_ENTITLEMENTS="" build`, then `codesign -dv --entitlements - build-adhoc/Build/Products/Release/Elevate.app` to confirm no application-identifier. Launch it (`open`), and check via `log stream --predicate 'process == "Elevate"'` for keychain errors while adding an Azure CLI account (loopback store) — this needs the user; instead write a tiny `swift` script? No: verify the store in-process by adding a temporary debug log line, launching, and reading `log show` for `errSecMissingEntitlement` (-34018). Report whether `KeychainRefreshTokenStore` works ad-hoc with the fallback and whether `MSALPublicClientApplication` init succeeds (it is created at launch when a client id is configured; check the log for MSAL keychain errors). Decide per spec: if MSAL fails on ad-hoc, make `AppModel.isAvailable(.ownApp)` return false when `BuildInfo.signingState == .adHoc` and set the Add account caption "Unavailable on unsigned builds; use a custom app registration". Kill the ad-hoc copy and relaunch the Debug build afterwards; do not commit `build-adhoc/` (it is under `build*`? check `.gitignore`; add `macos/build-adhoc/` if needed).
- [ ] Build (Debug), commit `git commit -m "BuildInfo with signing state, ad-hoc keychain fallback, version numbers"`.

---

### Task 3: AppModel/Settings — error log, launch at login, update check, diagnostics

**Files:** `App/UpdateChecker.swift` (new), `App/AppModel.swift`, `App/AppSettings.swift`, `Views/SettingsView.swift`, `Views/PanelView.swift`.

- [ ] `UpdateChecker` (`Sendable` struct with `http: any HTTPClient`): `func latest() async throws -> (tag: String, url: URL)?` — GET the releases URL with the headers from the constraints; 404 → nil; decode `tag_name`, `html_url`.
- [ ] `AppSettings`: `launchAtLogin` is NOT stored (read from `SMAppService.mainApp.status`); add `lastUpdateCheck: Date?` and `dismissedUpdateVersion: String?` with the `didSet` pattern.
- [ ] `AppModel`: `errorLog = ErrorLog()`; a private `logError(_:)` called from every place that assigns `tenantErrors[...] = <non-nil>`, `approvalErrors[...] = <non-nil>`, `notice = <non-nil>` (except the "Copied"/info notices), and in `activate`'s `.failed` branch (`"<summaryName>: <userMessage>"`). `launchAtLoginStatus: SMAppService.Status` (recomputed on demand via a computed property), `func setLaunchAtLogin(_ on: Bool) throws` (`register()`/`unregister()`), `launchAtLoginError: String?`. `updateAvailable: (version: String, url: URL)?`, `updateCheckMessage: String?`, `func checkForUpdates(force: Bool) async` — throttled by `lastUpdateCheck` unless forced; compares with `AppVersion.isNewer(latestTag:current: BuildInfo.version)`; skips when the tag equals `dismissedUpdateVersion`; sets `updateCheckMessage` ("You have the latest version", "No releases yet", or the error); called from `bootstrap()` after the first refresh. `func dismissUpdate()`. `func diagnosticsText() -> String` building `DiagnosticsInput` from state (accounts, tenants with flags such as "manual roles", "Azure off", "Groups off", "Entra view only", profiles' names, hot key display + profile name, `errorLog.entries`), `BuildInfo`, `ProcessInfo.processInfo.operatingSystemVersionString`.
- [ ] `SettingsView`: new `Section("General")` above the app registration section: `Toggle("Launch at login")` bound to a `@State` mirror initialised from the model, calling `setLaunchAtLogin` and showing `launchAtLoginError` or "Approve in System Settings → General → Login Items" when `.requiresApproval`; `LabeledContent("Version") { Text("\(BuildInfo.version) (\(BuildInfo.build)) · \(BuildInfo.signingDescription)") }`; `Button("Check for updates")` + `updateCheckMessage`; `Button("Copy diagnostics")` writing `diagnosticsText()` to the pasteboard and showing "Copied" for a few seconds.
- [ ] `PanelView`: when `model.updateAvailable` is set, a second banner (blue tint) "Elevate <version> is available" with "Open" (`NSWorkspace.shared.open(url)`) and "Dismiss" (`dismissUpdate`). Keep the existing notice banner.
- [ ] Build, relaunch, commit `git commit -m "Launch at login, diagnostics report, update check and error log"`.

---

### Task 4: Release workflow, cask, docs

**Files:** `.github/workflows/release.yml`, `Casks/elevate.rb`, `docs/releasing.md`, `README.md`, `macos/README.md`, `.gitignore` (add `macos/build-adhoc/`, `macos/dist/`).

- [ ] Workflow per spec §4. Skeleton:

```yaml
name: Release
on:
  push:
    tags: ['v*']
permissions:
  contents: write
jobs:
  release:
    runs-on: macos-26
    defaults: { run: { working-directory: macos } }
    env:
      TAG: ${{ github.ref_name }}
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }
      - name: Select Xcode
        run: sudo xcode-select -s /Applications/Xcode_26.app || sudo xcode-select -s "$(ls -d /Applications/Xcode*.app | sort -V | tail -1)"
      - name: Install XcodeGen
        run: brew install xcodegen
      - name: Versions
        run: |
          echo "VERSION=${TAG#v}" >> "$GITHUB_ENV"
          echo "BUILD=${{ github.run_number }}" >> "$GITHUB_ENV"
      - name: Generate project
        run: xcodegen generate
      - name: Import Developer ID certificate
        if: ${{ secrets.MACOS_CERT_P12 != '' }}
        env:
          P12: ${{ secrets.MACOS_CERT_P12 }}
          P12_PASSWORD: ${{ secrets.MACOS_CERT_PASSWORD }}
        run: |
          echo "$P12" | base64 --decode > cert.p12
          security create-keychain -p ci build.keychain
          security default-keychain -s build.keychain
          security unlock-keychain -p ci build.keychain
          security import cert.p12 -k build.keychain -P "$P12_PASSWORD" -T /usr/bin/codesign
          security set-key-partition-list -S apple-tool:,apple: -s -k ci build.keychain
          echo "SIGNED=1" >> "$GITHUB_ENV"
      - name: Build (Developer ID)
        if: env.SIGNED == '1'
        run: >
          xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Release -derivedDataPath build
          MARKETING_VERSION="$VERSION" CURRENT_PROJECT_VERSION="$BUILD"
          CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY="Developer ID Application" DEVELOPMENT_TEAM="${{ secrets.APPLE_TEAM_ID }}"
          ENABLE_HARDENED_RUNTIME=YES OTHER_CODE_SIGN_FLAGS="--timestamp --options runtime" build | tail -5
      - name: Build (unsigned)
        if: env.SIGNED != '1'
        run: |
          xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Release -derivedDataPath build \
            MARKETING_VERSION="$VERSION" CURRENT_PROJECT_VERSION="$BUILD" \
            CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO build | tail -5
          codesign --force --deep -s - build/Build/Products/Release/Elevate.app
      - name: Notarize
        if: env.SIGNED == '1'
        env:
          APPLE_ID: ${{ secrets.APPLE_ID }}
          APPLE_TEAM_ID: ${{ secrets.APPLE_TEAM_ID }}
          APPLE_APP_PASSWORD: ${{ secrets.APPLE_APP_PASSWORD }}
        run: |
          ditto -c -k --keepParent build/Build/Products/Release/Elevate.app Elevate.zip
          xcrun notarytool submit Elevate.zip --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" --wait
          xcrun stapler staple build/Build/Products/Release/Elevate.app
      - name: Package DMG
        run: |
          mkdir -p dist/dmg && cp -R build/Build/Products/Release/Elevate.app dist/dmg/ && ln -s /Applications dist/dmg/Applications
          hdiutil create -volname "Elevate" -srcfolder dist/dmg -ov -format UDZO "dist/Elevate-$VERSION.dmg"
          shasum -a 256 "dist/Elevate-$VERSION.dmg" | tee "dist/Elevate-$VERSION.dmg.sha256"
          echo "SHA256=$(cut -d' ' -f1 dist/Elevate-$VERSION.dmg.sha256)" >> "$GITHUB_ENV"
      - name: Release notes
        run: |
          if [ "$SIGNED" = "1" ]; then
            printf 'Signed with Developer ID and notarized.\n' > notes.md
          else
            printf 'Unsigned build (ad-hoc signature). On first launch, right-click Elevate.app and choose Open, or run:\n\n    xattr -d com.apple.quarantine /Applications/Elevate.app\n\nHomebrew: brew install --cask --no-quarantine elevate\n' > notes.md
          fi
      - name: Create GitHub Release
        env: { GH_TOKEN: ${{ github.token }} }
        run: gh release create "$TAG" "dist/Elevate-$VERSION.dmg" "dist/Elevate-$VERSION.dmg.sha256" --title "Elevate $VERSION" --notes-file notes.md
      - name: Update cask
        working-directory: ${{ github.workspace }}
        env: { GH_TOKEN: ${{ github.token }} }
        run: |
          ./scripts/update-cask.sh "$VERSION" "$SHA256" "${{ github.repository }}" "$SIGNED"
          git config user.name "github-actions[bot]"; git config user.email "github-actions[bot]@users.noreply.github.com"
          git checkout -B main origin/main && git add Casks/elevate.rb && git commit -m "Cask: Elevate $VERSION" && git push origin main
```

  `scripts/update-cask.sh` writes `Casks/elevate.rb`:

```ruby
cask "elevate" do
  version "VERSION"
  sha256 "SHA256"
  url "https://github.com/OWNER/REPO/releases/download/v#{version}/Elevate-#{version}.dmg"
  name "Elevate"
  desc "Just-in-time Entra, Azure and PIM for Groups activation from the menu bar"
  homepage "https://github.com/OWNER/REPO"
  depends_on macos: ">= :tahoe"
  app "Elevate.app"
  zap trash: ["~/Library/Application Support/Elevate", "~/Library/Preferences/no.reothor.elevate.plist"]
  caveats "This build is not notarized. Install with --no-quarantine or right-click Elevate.app and choose Open on first launch."   # only when unsigned
end
```

  Verify the `depends_on macos` symbol for macOS 26 (`:tahoe`) in the Homebrew docs; fall back to a version string if unsure. Commit a placeholder `Casks/elevate.rb` with version "0.0.0" so the tap resolves.

- [ ] `docs/releasing.md` and README sections per spec §4; `.gitignore` additions.
- [ ] Validate YAML (`python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/release.yml'))"` if PyYAML is present, else `ruby -ryaml -e 'YAML.load_file(".github/workflows/release.yml")'`), `bash -n scripts/update-cask.sh`, and run the DMG packaging step locally against the Debug build to confirm `hdiutil` works. Commit `git commit -m "Release workflow with optional Developer ID signing, Homebrew cask and releasing guide"`.

---

### Task 5: Final checks

- [ ] `swift test`, build, relaunch; README index links; ledger triage; merge; then tag `v1.0.0` from main and watch the workflow (the user's call to tag — the plan stops before tagging).
