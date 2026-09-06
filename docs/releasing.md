# Releasing Elevate

Releases are cut by pushing one tag. `.github/workflows/release.yml` builds the
macOS app and the Windows app from that commit, publishes a single GitHub
Release "Elevate x.y.z" with the DMG, the x64 and arm64 MSIs and their SHA-256
files, and updates the Homebrew cask on `main`. Both apps carry the same version
number; a platform without code changes since the last release is simply rebuilt.

## Cutting a release

1. Make sure `main` is green (the macOS and Windows workflows) and holds
   everything the release should contain.
2. Move the entries under `## [Unreleased]` in [../CHANGELOG.md](../CHANGELOG.md)
   to a new `## [x.y.z] - YYYY-MM-DD` heading, update the comparison links at the
   bottom, and commit that before tagging. The release notes quote that section.
3. Tag and push:

   ```bash
   git tag v1.2.0
   git push origin v1.2.0
   ```

   The tag must start with `v`; the version in the release, the DMG and MSI
   names, the cask and the winget manifest is the tag without it
   (`v1.2.0` → `1.2.0`).
4. Watch the run under Actions → Release. When it finishes, the release is at
   `https://github.com/FrodeHus/elevate/releases/tag/v1.2.0` and the generated
   winget manifest is a workflow artifact named `winget-manifest`.
5. Manual checklist after the run finishes: toggle Launch at login on the
   ad-hoc DMG build and confirm it registers; run one MSI on a Windows machine
   and confirm SmartScreen's "Run anyway" opens the app.

To redo a release, delete the tag and the GitHub Release, then push the tag
again — the workflow always overwrites its own assets but `gh release create`
fails if the release already exists.

## What the workflow does

Three jobs. `macos` and `windows` run in parallel; `publish` waits for both.

**macos** (`macos-26`, working directory `macos`):

1. Selects Xcode 26, installs XcodeGen and runs `xcodegen generate`.
2. Derives `VERSION` from the tag and `BUILD` from the workflow run number, and
   passes them to `xcodebuild` as `MARKETING_VERSION` and
   `CURRENT_PROJECT_VERSION` on the command line, so no file is edited for a
   release.
3. Builds the `ElevateApp` scheme in Release. Without signing secrets it builds
   with code signing disabled and then applies an ad-hoc signature
   (`codesign --force --deep -s -`). With secrets it signs with Developer ID
   and hardened runtime, then notarizes and staples the app (see below).
4. Packages `dist/Elevate-$VERSION.dmg` with `hdiutil create` — the app plus an
   `/Applications` symlink so the DMG window supports drag-to-install — and
   writes `dist/Elevate-$VERSION.dmg.sha256`. On the signed path the DMG
   itself is then notarized and stapled too, so the downloaded disk image opens
   without a Gatekeeper prompt even before the user drags the app out.
5. Uploads the DMG and its hash as the `macos` artifact.

**windows** (`windows-latest`):

1. Restores the .NET 10 SDK from `windows/global.json`, runs the test suites and
   installs WiX 5.0.2.
2. Builds the x64 and arm64 MSIs with `windows/installer/build.ps1`, signing
   them with Azure Artifact Signing when the secrets below are set, else
   unsigned.
3. Generates and validates the winget manifest, uploads it as the
   `winget-manifest` artifact, and uploads the MSIs and their hashes as the
   `windows` artifact.

**publish** (`ubuntu-latest`):

1. Downloads both artifacts and reads the three hashes.
2. Writes the notes: the changelog section for the version, then a macOS
   section (notarized or the Open Anyway steps, the Homebrew sequence, the DMG
   hash) and a Windows section (signed or the SmartScreen step, the MSI hashes).
3. Creates the GitHub Release with every asset attached.
4. Runs `scripts/update-cask.sh` on a checkout of `main`, which rewrites
   `Casks/elevate.rb` with the new version, SHA-256 and download URL, and
   commits it to `main` as `github-actions[bot]` using the workflow token.
   The `caveats` block is included only for unsigned builds.

The winget manifest is not submitted automatically: winget moderation requires
signed installers, so submission waits for Azure Artifact Signing. Once releases
are signed, download the `winget-manifest` artifact and run `wingetcreate
submit` on it.

## Optional signing secrets

The workflow works with no secrets at all and produces an ad-hoc signed build; since 1.2.2 the secrets are set and every release is signed and notarized.
Adding all six repository secrets (Settings → Secrets and variables →
Actions) switches it to Developer ID signing and notarization. They require an
Apple Developer Program membership.

| Secret | What it is |
|---|---|
| `MACOS_CERT_P12` | Base64 of a "Developer ID Application" certificate exported as a `.p12` (with its private key) |
| `MACOS_CERT_PASSWORD` | The password set when exporting that `.p12` |
| `APPLE_ID` | The Apple ID email used for notarization |
| `APPLE_TEAM_ID` | The 10-character Developer Team ID |
| `APPLE_APP_PASSWORD` | An app-specific password for that Apple ID |
| `MACOS_PROVISIONING_PROFILE` | Base64 of a "Developer ID Application" provisioning profile for `no.reothor.elevate` (a `.provisionprofile`; needed because the keychain-sharing entitlement is restricted and macOS refuses to launch a Developer ID app that carries it without a profile) |

Creating them:

1. **Certificate.** In Xcode → Settings → Accounts, or on
   developer.apple.com/account → Certificates, create a *Developer ID
   Application* certificate. Open Keychain Access, find it under "My
   Certificates", right-click → Export → Personal Information Exchange
   (`.p12`), and set an export password — that password is
   `MACOS_CERT_PASSWORD`. Then:

   ```bash
   base64 -i DeveloperID.p12 | pbcopy   # paste as MACOS_CERT_P12
   ```

2. **Team ID.** developer.apple.com/account → Membership details, or
   `xcrun altool --list-providers` — the 10-character identifier.
3. **App-specific password.** appleid.apple.com → Sign-In and Security →
   App-Specific Passwords → generate one named e.g. "Elevate notarization".
   That value is `APPLE_APP_PASSWORD`; the Apple ID it belongs to is
   `APPLE_ID`.

4. **Provisioning profile.** The easiest way is to let Xcode create it: with your
   Apple ID signed in to Xcode, run

   ```bash
   cd macos && xcodegen generate
   xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Release -derivedDataPath build-export -archivePath build-export/Elevate.xcarchive -allowProvisioningUpdates archive
   xcodebuild -exportArchive -archivePath build-export/Elevate.xcarchive -exportPath build-export/export -allowProvisioningUpdates -exportOptionsPlist <(printf '<plist version="1.0"><dict><key>method</key><string>developer-id</string><key>teamID</key><string>VLJKN96D7N</string><key>signingStyle</key><string>automatic</string></dict></plist>')
   ```

   The exported app embeds the profile ("Mac Team Direct Provisioning Profile:
   no.reothor.elevate", valid for 18 years) at
   `build-export/export/Elevate.app/Contents/embedded.provisionprofile`; base64 that
   file into `MACOS_PROVISIONING_PROFILE`. Alternatively create a "Developer ID
   Application" profile for the App ID in the developer portal and download it. The
   workflow builds unsigned, copies the profile to `Contents/embedded.provisionprofile`
   and signs with `codesign` (frameworks first, then the app with its entitlements), the
   same result as an Xcode Developer ID export; Xcode itself refuses Xcode-managed
   profiles under manual signing.

### What changes when they are present

- The build uses `CODE_SIGN_STYLE=Manual`,
  `CODE_SIGN_IDENTITY="Developer ID Application"`,
  `DEVELOPMENT_TEAM=$APPLE_TEAM_ID`, `ENABLE_HARDENED_RUNTIME=YES` and
  `--timestamp --options runtime`.
- The app is zipped and submitted with `xcrun notarytool submit --wait`, then
  `xcrun stapler staple` attaches the ticket, so the DMG opens without any
  Gatekeeper prompt.
- The release notes drop the Open Anyway instructions, and the cask drops its
  `caveats`, so installing no longer needs the
  `xattr -d com.apple.quarantine` step.

A step reads `MACOS_CERT_P12` into `HAVE_CERT` before anything is generated or
built, so the decision to sign is made once, explicitly, and out of any `if:`
expression — the `secrets` context isn't available there, and env comparisons
keep the actual certificate value out of the workflow's expressions entirely.
The signing steps then key off `env.SIGNED`, which is only set once the
certificate has actually been imported.

**Ad-hoc builds carry no entitlements.** Because they aren't signed with a
Developer ID, an ad-hoc signed build has none of the app's entitlements
(keychain access groups, associated domains, etc.), so MSAL — whose token cache
lives in a shared data-protection keychain group — cannot be used on them. The
app detects this at launch and signs in with the **own app registration**
through the loopback browser flow instead, using the same client ID from
Settings, so an unsigned build has full functionality. It only needs
`http://localhost` registered as a redirect URI under the "Mobile and desktop
applications" platform, which the setup script and the setup guide already add.
All methods store their tokens in the user's login keychain rather than relying
on the app's entitlements.

What a Developer ID signature adds on top is therefore not functionality but
polish: a silent first launch (no Gatekeeper prompt), MSAL's embedded webview
instead of the default browser, and SSO with other MSAL apps on the Mac.

## The Homebrew cask

`Casks/elevate.rb` lives in this repository, which doubles as a tap:

```bash
brew tap FrodeHus/elevate https://github.com/FrodeHus/elevate
brew trust frodehus/elevate        # Homebrew 6 requires trusting third-party taps
brew install --cask frodehus/elevate/elevate
```

Plain `brew install --cask elevate` does not resolve: this tap is not a `homebrew-`
named repository, so the fully qualified `frodehus/elevate/elevate` name is required.

It is regenerated by `scripts/update-cask.sh <version> <sha256> <owner/repo>
[signed]`, which the release workflow calls. Running it by hand is only needed
to repair a bad cask commit:

```bash
./scripts/update-cask.sh 1.0.0 "$(shasum -a 256 Elevate-1.0.0.dmg | cut -d' ' -f1)" FrodeHus/elevate
ruby -c Casks/elevate.rb
```

`depends_on macos: ">= :tahoe"` is macOS 26. `zap` removes
`~/Library/Application Support/Elevate` and
`~/Library/Preferences/no.reothor.elevate.plist`. The repository ships a
placeholder cask at version `0.0.0` so the tap resolves before the first
release; the first release overwrites it.

## Windows signing secrets

Without them the Windows job publishes unsigned MSIs. With all six, the same job
signs them with Azure Artifact Signing (formerly Trusted Signing):
`AZURE_TRUSTED_SIGNING_ENDPOINT`, `AZURE_TRUSTED_SIGNING_ACCOUNT`,
`AZURE_TRUSTED_SIGNING_PROFILE`, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and
`AZURE_SUBSCRIPTION_ID`. That needs a paid subscription, the
`Microsoft.CodeSigning` provider, an *organization* Public Trust identity
validation (individual validation is US/Canada only) and a certificate profile,
plus an app registration holding the *Artifact Signing Certificate Profile
Signer* role for the workflow's OIDC login.
