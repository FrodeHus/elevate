# Releasing Elevate

Releases are cut by one workflow run. `.github/workflows/release.yml` moves the
Unreleased changelog entries under the new version, tags `main`, builds the
macOS app, the Windows app and the CLI from that commit, publishes a single
GitHub Release "Elevate x.y.z" with the DMG, the macOS installer package, the
x64 and arm64 MSIs, the six CLI archives, the enterprise kit, their SHA-256
files and a CLI checksums file, and commits the Homebrew cask and formula to
`main`. All three carry the same version number; a platform
without code changes since the last release is simply rebuilt.

## Cutting a release

1. Make sure everything the release should contain is merged, and that
   [../CHANGELOG.md](../CHANGELOG.md) has its notes under `## [Unreleased]`.
   The workflow refuses to cut a release with an empty Unreleased section.
2. Actions → Release → *Run workflow*, enter the version without the leading
   `v` (for example `1.2.7`), and run it on `main`. That run:
   - checks that the latest macOS, Windows and CLI CI runs on `main` passed, and
     stops if one failed or is still running;
   - runs `scripts/cut-changelog.sh` to move the Unreleased entries to
     `## [x.y.z] - YYYY-MM-DD` and rewrite the comparison links;
   - commits that to `main` as "Changelog: x.y.z" and pushes the tag `vx.y.z`.
3. The tag push starts a second Release run, which builds, publishes and
   commits the cask and the formula. Watch it under Actions → Release. When it
   finishes, the release is at `https://github.com/FrodeHus/elevate/releases/tag/vx.y.z`,
   the cask and formula bump is on `main`, and the generated winget manifests
   are workflow artifacts named `winget-manifest` (the app) and
   `winget-cli-manifest` (the CLI).
4. Manual checklist after the run finishes: toggle Launch at login on the DMG
   build and confirm it registers; run one MSI on a Windows machine and
   confirm SmartScreen's "Run anyway" opens the app; `brew upgrade --cask
   frodehus/elevate/elevate && elevate --version` on a Mac or Linux box;
   `sudo installer -pkg Elevate-x.y.z.pkg -target /` on a Mac, then `which
   elevate` shows `/usr/local/bin/elevate`; run one MSI and `elevate
   --version` in a new terminal.

Pushing a `v*` tag by hand still works and skips step 2, as long as the tag
points at a commit on `main` whose `CHANGELOG.md` already has the `## [x.y.z]`
section: cut the changelog yourself with `scripts/cut-changelog.sh <version>` on
a pull request, merge it, then tag the merge commit. A tag on any other commit
is refused before the builds start.

To redo a release, delete the tag and the GitHub Release, then push the tag
again, at a commit on `main` — the workflow always overwrites its own assets but
`gh release create` fails if the release already exists.

### The deploy key and the rulesets

Both pushes to `main` (the changelog commit and the cask bump) and the tag push
use the `RELEASE_DEPLOY_KEY` repository secret: the private half of a deploy key
with write access. The `main` ruleset lists *Deploy keys* as a bypass actor, which
is what lets the workflow commit without a pull request; the workflow token has
no such bypass, and a tag it pushed would not start a workflow run anyway. To
rotate it, generate a new key pair, add the public half under Settings → Deploy
keys with write access, and replace the secret.

A second ruleset, `release`, covers `refs/tags/v*` and forbids deleting or moving
a release tag; it has the same two bypass actors (deploy keys and administrators),
which is what lets a maintainer delete a tag to redo a release. Neither ruleset
restricts who may create a `v*` tag, so the workflow's `check` job is what keeps
a tag on another branch, or on a commit without its changelog section, away from
the signing certificates, the release and the deploy key.

## What the workflow does

Six jobs. `prepare` runs only for a manual run and ends with the tag push;
`check`, `macos`, `windows`, `cli` and `publish` run only for a tag push: `check`
first, then `macos`, `windows` and the three `cli` matrix legs in parallel, and
`publish` waits for all of them.

**check** (`ubuntu-latest`):

1. Checks that the tagged commit is on `main` and that `CHANGELOG.md` at that
   commit has the `## [x.y.z]` section, and stops otherwise. Nothing is built,
   signed or published for a tag that fails here.

**macos** (`macos-26`, working directory `macos`):

1. Selects Xcode 26, installs XcodeGen and runs `xcodegen generate`.
2. Derives `VERSION` from the tag and `BUILD` from the workflow run number, and
   passes them to `xcodebuild` as `MARKETING_VERSION` and
   `CURRENT_PROJECT_VERSION` on the command line, so no file is edited for a
   release.
3. Builds the `ElevateApp` scheme in Release once, with code signing disabled
   (`CODE_SIGNING_ALLOWED=NO`). Restores the .NET 10 SDK from `cli/global.json`
   and runs `cli/package.sh publish <version> osx-arm64` to build the CLI
   helper (published here, in parallel with the `cli` job, rather than pulled
   from it), then makes a second copy of the app under `build/pkgroot` with
   the helper copied to `Contents/Helpers/elevate` — the DMG copy
   (`build/Build/Products/Release/Elevate.app`) never gets the helper, because
   a code signature seals nested code and the helper cannot be added or
   removed afterwards.
4. Signs both copies. Without signing secrets each gets an ad-hoc signature
   (`codesign --force --deep -s -`). With secrets, each is signed inside-out
   and never with `--deep`: the frameworks first, then (on the pkg copy) the
   helper with `cli/elevate.entitlements` — the hardened-runtime entitlements
   the .NET host needs (JIT, RW/X pages, no library validation) — then the app
   bundle itself with its own entitlements and the embedded provisioning
   profile. A `Check versions` step then asserts `elevate --version` from the
   helper, `CFBundleShortVersionString` from the app, and the tag all match,
   before either copy is notarized.
5. Notarizes both copies (signed path only): each is zipped, submitted to the
   notary service, and its notarization log is fetched and required to contain
   `"status": "Accepted"` — with the helper inside the pkg copy, the log is
   what confirms the notary service accepted it too — before both are stapled.
6. Packages `dist/Elevate-$VERSION.dmg` with `hdiutil create` from the plain
   copy (app only, unchanged) — the app plus an `/Applications` symlink so the
   DMG window supports drag-to-install — and writes
   `dist/Elevate-$VERSION.dmg.sha256`. On the signed path the DMG itself is
   then notarized and stapled too, so the downloaded disk image opens without
   a Gatekeeper prompt even before the user drags the app out.
7. Packages `dist/Elevate-$VERSION.pkg` with `pkgbuild` from the helper copy
   (into `/Applications`, identifier `no.reothor.elevate`, `--scripts
   macos/pkg/scripts`) — the installer package Jamf and Intune deploy. The
   `postinstall` script links `/usr/local/bin/elevate` to the bundled helper on
   Apple Silicon Macs (Intel Macs use the standalone CLI archive instead). With
   `MACOS_INSTALLER_CERT_P12` and `MACOS_INSTALLER_CERT_PASSWORD` set it is
   signed with `productsign --sign "Developer ID Installer" --timestamp` and,
   with the notarization secrets, notarized and stapled like the DMG; without
   them it ships unsigned, which Jamf and `installer -pkg` accept but Intune
   does not. The `dist/Elevate-$VERSION.pkg.sha256` is written after stapling,
   and the job output `pkg_signed` (`1` or `0`) tells the publish job which
   wording the release notes get.
8. Uploads the DMG, the pkg and their hashes as the `macos` artifact.

**windows** (`windows-latest`):

1. Restores the .NET 10 SDK from `windows/global.json`, runs the test suites and
   installs WiX 5.0.2.
2. Builds the x64 and arm64 MSIs with `windows/installer/build.ps1`, signing
   them with Azure Artifact Signing when the secrets below are set, else
   unsigned.
3. Generates and validates the winget manifest, uploads it as the
   `winget-manifest` artifact, and uploads the MSIs and their hashes as the
   `windows` artifact.

**cli** (a matrix: `ubuntu-latest` for `linux-x64` and `linux-arm64`, `macos-26`
for `osx-arm64` and `osx-x64`, `windows-latest` for `win-x64` and `win-arm64`):

1. Restores the .NET 10 SDK from `cli/global.json` and runs the CLI tests.
2. Runs `cli/package.sh publish <version> <rid>` for each of the leg's two RIDs:
   a self-contained, single-file, ReadyToRun `elevate` binary. The publish
   itself could cross-compile every RID on one machine; the matrix exists so
   each platform's binary can be signed on its own runner.
3. macOS: with the Developer ID secrets below, signs each binary with hardened
   runtime, a timestamp and the entitlements in `cli/elevate.entitlements` (the
   same ones applied to the helper bundled in the macOS pkg), then submits it
   to the notary service (a bare binary cannot be stapled; Gatekeeper checks
   the ticket online). Windows: with the
   Azure Artifact Signing secrets, signs each `elevate.exe` with `signtool`.
   Without secrets the binaries ship unsigned; Homebrew and winget downloads
   carry no quarantine flag, so only a browser download meets Gatekeeper or
   SmartScreen.
4. Runs `cli/package.sh archive <version> <rid>`: `elevate-cli-<version>-<rid>.tar.gz`
   (`.zip` on Windows) holding just the executable, plus its `.sha256`.
5. Windows only: generates and validates the `Reothor.Elevate.CLI` portable
   winget manifest with `cli/winget/New-Manifest.ps1` and uploads it as the
   `winget-cli-manifest` artifact.
6. Uploads the archives and hashes as `cli-linux`, `cli-macos` and `cli-windows`.

**publish** (`ubuntu-latest`):

1. Downloads every artifact, reads the app hashes and writes one
   `elevate-cli-<version>-checksums.txt` from the six CLI `.sha256` files.
2. Builds the enterprise kit from the checkout: `scripts/validate-enterprise-kit.py`
   first, so a kit that drifted from `docs/enterprise/keys.md` fails the release
   instead of shipping, then `enterprise/` plus `docs/enterprise/keys.md` into
   `Elevate-enterprise-kit-<version>/`, with `{{VERSION}}` in the kit's
   `README.md` replaced by the version, zipped as
   `dist/Elevate-enterprise-kit-<version>.zip` with a `.sha256`.
3. Writes the notes: the changelog section for the version, then a macOS
   section (notarized or the Open Anyway steps, the Homebrew sequence, the DMG
   hash), a Windows section (signed or the SmartScreen step, the MSI hashes)
   and a CLI section (the Homebrew formula, winget, the archives and the
   checksums file) and an Enterprise section (the pkg, signed and notarized or
   unsigned, the kit and its contents, and a link to `docs/enterprise/README.md`).
4. Creates the GitHub Release with every asset attached.
5. Runs `scripts/update-cask.sh` and `scripts/update-formula.sh` on a checkout
   of `main`, which rewrite `Casks/elevate.rb` and `Formula/elevate-cli.rb`
   with the new version, SHA-256 values and download URLs, and commits both to
   `main` as `github-actions[bot]` over the deploy key in one commit, retrying
   once or twice if main moved meanwhile. The cask's `caveats` block is
   included only for unsigned builds.

Neither winget manifest is submitted automatically: winget moderation requires
signed installers, so submission waits for Azure Artifact Signing. Once releases
are signed, download the `winget-manifest` and `winget-cli-manifest` artifacts
and run `wingetcreate submit` on them.

## Optional signing secrets

The workflow works with no secrets at all and produces an ad-hoc signed build; since 1.2.2 the secrets are set and every release is signed and notarized.
Adding all six repository secrets (Settings → Secrets and variables →
Actions) switches it to Developer ID signing and notarization. They require an
Apple Developer Program membership. Two further secrets, listed after the table,
sign the installer package; they are independent of the six.

| Secret | What it is |
|---|---|
| `MACOS_CERT_P12` | Base64 of a "Developer ID Application" certificate exported as a `.p12` (with its private key) |
| `MACOS_CERT_PASSWORD` | The password set when exporting that `.p12` |
| `APPLE_ID` | The Apple ID email used for notarization |
| `APPLE_TEAM_ID` | The 10-character Developer Team ID |
| `APPLE_APP_PASSWORD` | An app-specific password for that Apple ID |
| `MACOS_PROVISIONING_PROFILE` | Base64 of a "Developer ID Application" provisioning profile for `no.reothor.elevate` (a `.provisionprofile`; needed because the keychain-sharing entitlement is restricted and macOS refuses to launch a Developer ID app that carries it without a profile) |
| `MACOS_INSTALLER_CERT_P12` | Base64 of a "Developer ID **Installer**" certificate exported as a `.p12` (with its private key) — a different certificate from `MACOS_CERT_P12`, created the same way and exported from Keychain Access the same way |
| `MACOS_INSTALLER_CERT_PASSWORD` | The password set when exporting that `.p12` |

The two installer secrets are optional on their own: without them the pkg is
still built, just unsigned, and the release notes say so. The workflow imports
that certificate into the same temporary `build.keychain` as the application
one — creating the keychain first if the unsigned build path skipped it, since
`pkgbuild` itself needs no keychain — signs with `productsign --sign "Developer
ID Installer" --timestamp`, and, when the notarization secrets are set too,
notarizes and staples the pkg before the checksum is taken. Intune refuses an
unsigned pkg; Jamf and `sudo installer -pkg Elevate-x.y.z.pkg -target /` accept
one.

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

## The Homebrew cask and formula

`Casks/elevate.rb` (the macOS app) and `Formula/elevate-cli.rb` (the CLI, macOS
and Linux) live in this repository, which doubles as a tap:

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

The CLI's formula installs from the same tap on macOS and Linux:

```bash
brew install frodehus/elevate/elevate-cli
```

It is regenerated by `scripts/update-formula.sh <version> <owner/repo> <dist dir>`,
where the dist directory holds the `.sha256` files of the four Homebrew archives
(`osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`); the formula picks the
archive with `on_macos` / `on_linux` and `on_arm` / `on_intel`, installs the one
`elevate` binary and generates bash, zsh and fish completions from
`elevate completion`. Like the cask it ships as a `0.0.0` placeholder until the
first release that includes the CLI.

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
