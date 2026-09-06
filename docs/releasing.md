# Releasing Elevate

Releases are cut by pushing a tag. `.github/workflows/release.yml` builds the
macOS app, packages a DMG, publishes a GitHub Release and updates the Homebrew
cask on `main`.

## Cutting a release

1. Make sure `main` is green (the macOS workflow) and holds everything the
   release should contain.
2. Tag and push:

   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

   The tag must start with `v`; the version in the release, the DMG name and
   the cask is the tag without it (`v1.0.0` → `1.0.0`).
3. Watch the run under Actions → Release. When it finishes, the release is at
   `https://github.com/FrodeHus/elevate/releases/tag/v1.0.0`.
4. Manual checklist after the run finishes: toggle Launch at login on the
   ad-hoc DMG build and confirm it registers.

To redo a release, delete the tag and the GitHub Release, then push the tag
again — the workflow always overwrites its own DMG but `gh release create`
fails if the release already exists.

## What the workflow does

Runs on `macos-26`, working directory `macos`:

1. Checks out the repo (full history — the cask commit needs `main`), selects
   Xcode 26, installs XcodeGen and runs `xcodegen generate`.
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
   itself is then notarized and stapled too (`xcrun notarytool submit
   --wait` followed by `xcrun stapler staple`, same credentials as the app
   notarize step), so the downloaded disk image opens without a Gatekeeper
   prompt even before the user drags the app out.
5. Creates the GitHub Release with both files attached. The notes say whether
   the build is notarized or unsigned; the unsigned notes carry the macOS 26
   Open Anyway instructions and the `--no-quarantine` Homebrew line.
6. Runs `scripts/update-cask.sh` on a checkout of `main`, which rewrites
   `Casks/elevate.rb` with the new version, SHA-256 and download URL, and
   commits it to `main` as `github-actions[bot]` using the workflow token.
   The `caveats` block is included only for unsigned builds.

## Optional signing secrets

The workflow works with no secrets at all and produces an ad-hoc signed build.
Adding all five repository secrets (Settings → Secrets and variables →
Actions) switches it to Developer ID signing and notarization. They require an
Apple Developer Program membership.

| Secret | What it is |
|---|---|
| `MACOS_CERT_P12` | Base64 of a "Developer ID Application" certificate exported as a `.p12` (with its private key) |
| `MACOS_CERT_PASSWORD` | The password set when exporting that `.p12` |
| `APPLE_ID` | The Apple ID email used for notarization |
| `APPLE_TEAM_ID` | The 10-character Developer Team ID |
| `APPLE_APP_PASSWORD` | An app-specific password for that Apple ID |

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

### What changes when they are present

- The build uses `CODE_SIGN_STYLE=Manual`,
  `CODE_SIGN_IDENTITY="Developer ID Application"`,
  `DEVELOPMENT_TEAM=$APPLE_TEAM_ID`, `ENABLE_HARDENED_RUNTIME=YES` and
  `--timestamp --options runtime`.
- The app is zipped and submitted with `xcrun notarytool submit --wait`, then
  `xcrun stapler staple` attaches the ticket, so the DMG opens without any
  Gatekeeper prompt.
- The release notes drop the Open Anyway instructions, and the cask drops its
  `caveats`, so `brew install --cask elevate` no longer needs
  `--no-quarantine`.

A step reads `MACOS_CERT_P12` into `HAVE_CERT` before anything is generated or
built, so the decision to sign is made once, explicitly, and out of any `if:`
expression — the `secrets` context isn't available there, and env comparisons
keep the actual certificate value out of the workflow's expressions entirely.
The signing steps then key off `env.SIGNED`, which is only set once the
certificate has actually been imported.

**Ad-hoc builds carry no entitlements.** Because they aren't signed with a
Developer ID, an ad-hoc signed build has none of the app's entitlements
(keychain access groups, associated domains, etc.), so sign-in with Elevate's
own app registration (MSAL, using the system webview) does not work on these
builds. The Azure CLI and custom/company-app-registration sign-in methods
still work, but store their tokens in the user's login keychain rather than
relying on the app's entitlements.

## The Homebrew cask

`Casks/elevate.rb` lives in this repository, which doubles as a tap:

```bash
brew tap FrodeHus/elevate https://github.com/FrodeHus/elevate
brew install --cask --no-quarantine elevate
```

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
