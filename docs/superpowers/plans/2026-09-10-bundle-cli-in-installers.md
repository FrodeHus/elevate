# Bundle the CLI in the pkg, MSI and cask — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the `elevate` CLI inside the macOS installer package, the Windows MSIs and the Homebrew cask, at the app's version, while the DMG stays app-only and the standalone CLI archives keep serving Linux, headless and Intel-only hosts.

**Architecture:** The release workflow's macOS job publishes the osx-arm64 CLI, embeds it as `Elevate.app/Contents/Helpers/elevate` in a *second copy* of the built app, signs the helper inside-out with hardened-runtime entitlements, and packages that copy as the pkg (with a postinstall that links `/usr/local/bin/elevate`); the first copy still becomes the DMG. `windows/installer/build.ps1` publishes the win-x64/win-arm64 CLI next to the app and the WiX package adds it as a component that appends the install folder to the user's PATH. The cask switches from the DMG to the pkg, so `brew install --cask` also yields the CLI; the formula is deprecated with a notice and removed in a later release.

**Tech Stack:** GitHub Actions (`release.yml`), bash, `codesign`/`notarytool`/`pkgbuild`, WiX v5 (`.wxs`, PowerShell), Homebrew cask/formula Ruby, .NET 10 (`dotnet publish`, xUnit + FluentAssertions in `cli/tests`), Markdown docs.

**Spec:** GitHub issue #122 (https://github.com/FrodeHus/elevate/issues/122). No separate design doc: the issue is the spec. Two deliberate deviations, decided while planning because the issue's text cannot be implemented literally:

1. **The cask installs the pkg, not the DMG.** The issue keeps the DMG app-only *and* wants `binary "#{appdir}/Elevate.app/Contents/Helpers/elevate"` in the cask; the cask downloads the DMG, so both cannot hold. The DMG is 10 MB and the CLI archive 14 MB; bundling into the DMG would more than double the casual download, which the issue's goal forbids. So the cask downloads `Elevate-<version>.pkg` (`pkg` + `uninstall pkgutil:` stanzas), and the pkg's postinstall puts `elevate` on PATH. `brew install --cask` therefore asks for the sudo password, like every pkg cask.
2. **The MSI appends to the user PATH, not the machine PATH.** `Elevate.wxs` is `Scope="perUser"` (no admin rights, installs under `%LOCALAPPDATA%`), so it cannot write `HKLM`. The `Environment` element with `System="no"` writes `HKCU\Environment\Path`, which is what a per-user install can do.

## Global Constraints

- Bundle id `no.reothor.elevate`; pkg identifier `no.reothor.elevate`; install location `/Applications`.
- The bundled macOS helper is **osx-arm64 only** (the issue keeps the standalone archives for Intel-only hosts; .NET single-file binaries cannot be `lipo`'d into a universal binary). Bundle path: `Elevate.app/Contents/Helpers/elevate`. Symlink: `/usr/local/bin/elevate`.
- Helper entitlements (exact keys, from the issue): `com.apple.security.cs.allow-jit`, `com.apple.security.cs.allow-unsigned-executable-memory`, `com.apple.security.cs.disable-library-validation`. Same Developer ID Application identity, `--options runtime --timestamp`. The helper is signed **before** the app; no `--deep` when signing.
- `elevate --version` must print exactly the release version (`x.y.z`), the same string as the app's `CFBundleShortVersionString`; the workflow asserts this.
- DMG unchanged: app only, no helper. Standalone `elevate-cli-<version>-<rid>.tar.gz`/`.zip` archives unchanged for all six RIDs.
- Formula: deprecated (`deprecate! date:, because:`), still regenerated each release; removal is a later release (out of scope here).
- Every CI/release YAML change must keep the existing job names (`check`, `macos`, `windows`, `cli`, `publish`) — the `prepare` job and the branch ruleset refer to them.
- No real client id, tenant id, or account name anywhere. Commits: conventional prefixes (`feat:`, `docs:`, `ci:`), no attribution lines.
- Never `git push` or open a PR from a task; the orchestrator does that at the end.
- Work on branch `bundle-cli` (created from `main` before Task 1).

---

## File map

| File | Responsibility |
|---|---|
| `cli/elevate.entitlements` (new) | Hardened-runtime entitlements for the .NET host, used for the bundled helper and the standalone macOS binaries. |
| `macos/pkg/scripts/postinstall` (new, mode 755) | pkg postinstall: links `/usr/local/bin/elevate` to the helper on Apple Silicon; no-op with a message otherwise. |
| `.github/workflows/release.yml` | macOS job: publish the CLI, embed into a pkg copy of the app, sign inside-out, assert versions, `pkgbuild --scripts`; cli job: entitlements; publish job: release notes. |
| `scripts/update-cask.sh`, `Casks/elevate.rb` | Cask over the pkg with `uninstall`. |
| `scripts/update-formula.sh`, `Formula/elevate-cli.rb` | Formula with the deprecation notice. |
| `windows/installer/Elevate.wxs`, `windows/installer/build.ps1` | CLI component + user PATH; CLI publish and signing. |
| `cli/src/Elevate.Cli/Update/UpgradeHint.cs` (new), `cli/src/Elevate.Cli/Commands/MiscCommands.cs`, `cli/tests/Elevate.Cli.Tests/UpgradeHintTests.cs` (new) | The `elevate update` hint names the right upgrade route for a bundled install. |
| `docs/releasing.md`, `cli/README.md`, `windows/README.md`, `macos/README.md`, `README.md`, `CONTRIBUTING.md`, `docs/README.md`, `docs/enterprise/{README,cli,macos-jamf,macos-intune,windows-intune,windows-group-policy}.md`, `enterprise/README.md`, `CHANGELOG.md` | Install and uninstall instructions. |

---

### Task 1: Helper entitlements and the pkg postinstall script

**Files:**
- Create: `cli/elevate.entitlements`
- Create: `macos/pkg/scripts/postinstall` (executable)

**Interfaces:**
- Produces: `cli/elevate.entitlements` (path used by Task 2 in `codesign --entitlements`), `macos/pkg/scripts/` (directory passed to `pkgbuild --scripts` in Task 2). The postinstall reads `$3` (target volume) only.

- [ ] **Step 1: Create the branch**

```bash
git checkout main && git pull --ff-only && git checkout -b bundle-cli
```

- [ ] **Step 2: Write the entitlements file**

`cli/elevate.entitlements`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<!-- Hardened-runtime entitlements the .NET runtime host needs (JIT, RW/X pages, no library
     validation for the bundled native libraries). Applied to the standalone macOS binaries
     and to the helper inside Elevate.app/Contents/Helpers by the release workflow. -->
<plist version="1.0">
<dict>
	<key>com.apple.security.cs.allow-jit</key>
	<true/>
	<key>com.apple.security.cs.allow-unsigned-executable-memory</key>
	<true/>
	<key>com.apple.security.cs.disable-library-validation</key>
	<true/>
</dict>
</plist>
```

- [ ] **Step 3: Verify it parses**

Run: `plutil -lint cli/elevate.entitlements`
Expected: `cli/elevate.entitlements: OK`

- [ ] **Step 4: Write the postinstall script**

`macos/pkg/scripts/postinstall`:

```sh
#!/bin/sh
# Runs as root after Elevate-<version>.pkg has copied Elevate.app into /Applications (pkgbuild
# --scripts). Puts the bundled CLI on PATH by linking /usr/local/bin/elevate to it.
#
# Installer arguments: $1 package path, $2 target location, $3 target volume, $4 root ("/").
# The bundled CLI is built for Apple Silicon only; Intel Macs use the standalone archive
# (elevate-cli-<version>-osx-x64.tar.gz) instead. This script never fails the install.
VOLUME="${3%/}"
HELPER="$VOLUME/Applications/Elevate.app/Contents/Helpers/elevate"
LINK_DIR="$VOLUME/usr/local/bin"
LINK="$LINK_DIR/elevate"
TARGET="/Applications/Elevate.app/Contents/Helpers/elevate"

if [ ! -x "$HELPER" ]; then
  echo "Elevate: no bundled CLI at $HELPER; not linking $LINK" >&2
  exit 0
fi
if [ "$(uname -m)" != "arm64" ]; then
  echo "Elevate: the bundled CLI is built for Apple Silicon; on this Mac install elevate-cli-<version>-osx-x64.tar.gz from the release instead" >&2
  exit 0
fi
if [ -e "$LINK" ] && [ ! -L "$LINK" ]; then
  echo "Elevate: $LINK exists and is not a symlink; leaving it alone (remove it to use the bundled CLI)" >&2
  exit 0
fi
mkdir -p "$LINK_DIR" || exit 0
ln -sfn "$TARGET" "$LINK" || { echo "Elevate: could not link $LINK" >&2; exit 0; }
echo "Elevate: linked $LINK -> $TARGET"
exit 0
```

Then: `chmod 755 macos/pkg/scripts/postinstall`

- [ ] **Step 5: Test the script against a temporary volume**

```bash
ROOT="$(mktemp -d)"
mkdir -p "$ROOT/Applications/Elevate.app/Contents/Helpers"
printf '#!/bin/sh\necho 1.2.3\n' > "$ROOT/Applications/Elevate.app/Contents/Helpers/elevate"
chmod +x "$ROOT/Applications/Elevate.app/Contents/Helpers/elevate"
# 1. Fresh install: link created.
sh macos/pkg/scripts/postinstall pkg "$ROOT/Applications" "$ROOT/" /
test "$(readlink "$ROOT/usr/local/bin/elevate")" = /Applications/Elevate.app/Contents/Helpers/elevate && echo "link OK"
# 2. Re-run (upgrade): still exit 0, link unchanged.
sh macos/pkg/scripts/postinstall pkg "$ROOT/Applications" "$ROOT" / && echo "rerun OK"
# 3. A regular file in the way is left alone.
rm "$ROOT/usr/local/bin/elevate"; echo old > "$ROOT/usr/local/bin/elevate"
sh macos/pkg/scripts/postinstall pkg "$ROOT/Applications" "$ROOT" / 2>&1 | grep -q "not a symlink" && test "$(cat "$ROOT/usr/local/bin/elevate")" = old && echo "regular file left OK"
# 4. No helper: exit 0, nothing linked.
ROOT2="$(mktemp -d)"; sh macos/pkg/scripts/postinstall pkg "$ROOT2/Applications" "$ROOT2" /; test ! -e "$ROOT2/usr/local/bin/elevate" && echo "no helper OK"
rm -rf "$ROOT" "$ROOT2"
```

Expected: the four `OK` lines (on an Apple Silicon Mac; on Intel case 1 prints the Apple Silicon message instead — note it in the report).

- [ ] **Step 6: Check pkgbuild accepts the scripts directory**

```bash
TMP="$(mktemp -d)"; mkdir -p "$TMP/Fake.app/Contents/MacOS"; printf '#!/bin/sh\n' > "$TMP/Fake.app/Contents/MacOS/Fake"; chmod +x "$TMP/Fake.app/Contents/MacOS/Fake"
pkgbuild --component "$TMP/Fake.app" --install-location /Applications --identifier no.reothor.elevate --version 0.0.0 --scripts macos/pkg/scripts "$TMP/fake.pkg"
pkgutil --expand "$TMP/fake.pkg" "$TMP/expanded" && ls -l "$TMP/expanded/Scripts" && rm -rf "$TMP"
```

Expected: `Scripts/postinstall` listed as executable.

- [ ] **Step 7: Commit**

```bash
git add cli/elevate.entitlements macos/pkg/scripts/postinstall
git commit -m "feat(macos): CLI helper entitlements and the pkg postinstall that links /usr/local/bin/elevate"
```

---

### Task 2: Release workflow — embed, sign and package the helper on macOS; entitlements for the standalone binaries

**Files:**
- Modify: `.github/workflows/release.yml` (the `macos` job, lines ~130-380; the `cli` job's "Sign and notarize (macOS)" step, ~line 500)
- Modify: `docs/releasing.md` ("What the workflow does" → **macos** and **cli** items, and the manual checklist in step 4)

**Interfaces:**
- Consumes: `cli/elevate.entitlements`, `macos/pkg/scripts/` (Task 1); `cli/package.sh publish <version> osx-arm64` (existing; writes `cli/dist/osx-arm64/elevate`).
- Produces: `macos/dist/Elevate-<v>.pkg` now containing the helper and the postinstall; `macos/dist/Elevate-<v>.dmg` unchanged (app without helper). Job outputs unchanged (`signed`, `pkg_signed`).

Background for the implementer: the `macos` job has `working-directory: macos`, so repository paths are `../cli/...`. Two build steps exist today ("Build (Developer ID)" and "Build (unsigned, ad-hoc signature)"), each running `xcodebuild` then signing. Restructure so there is one unsigned `xcodebuild`, then a second copy of the app for the pkg, then signing per path. The DMG is built from `build/Build/Products/Release/Elevate.app` (no helper); the pkg from `build/pkgroot/Elevate.app` (with helper). A code signature seals nested code, so the helper cannot be added after signing or removed from a signed app — hence two copies.

- [ ] **Step 1: Add the .NET SDK and the CLI publish to the macOS job**

Insert after the "Generate project" step:

```yaml
      # The CLI ships inside the pkg copy of the app (Contents/Helpers/elevate), Apple Silicon
      # only; Intel Macs use the standalone archive the cli job builds. Published here rather
      # than pulled from the cli job so the two jobs stay parallel.
      - uses: actions/setup-dotnet@v6
        with:
          global-json-file: cli/global.json

      - name: Publish the CLI helper
        run: |
          ../cli/package.sh publish "$VERSION" osx-arm64
          out="$(../cli/dist/osx-arm64/elevate --version)"
          test "$out" = "$VERSION" || { echo "::error::elevate --version printed '$out', expected '$VERSION'"; exit 1; }
```

- [ ] **Step 2: Replace the two build steps with one build, one embed, and two signing steps**

Replace "Build (Developer ID)" and "Build (unsigned, ad-hoc signature)" with:

```yaml
      # Built once with signing off; signed below, per path. Xcode cannot sign with an
      # Xcode-managed Developer ID profile in manual mode, and a PROVISIONING_PROFILE_SPECIFIER on
      # the command line leaks into the Swift package targets, so signing is done with codesign
      # the way an Xcode "Developer ID" export does.
      - name: Build
        run: |
          set -o pipefail
          xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Release -derivedDataPath build \
            MARKETING_VERSION="$VERSION" CURRENT_PROJECT_VERSION="$BUILD" \
            CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO build | tail -5
          echo "APP=build/Build/Products/Release/Elevate.app" >> "$GITHUB_ENV"
          echo "PKG_APP=build/pkgroot/Elevate.app" >> "$GITHUB_ENV"

      # Two copies of the app: the DMG gets the app alone, the pkg gets the app with the CLI in
      # Contents/Helpers. A signature seals nested code, so the helper cannot be added or removed
      # after signing.
      - name: Embed the CLI helper in the pkg copy
        run: |
          rm -rf build/pkgroot && mkdir -p build/pkgroot
          cp -R "$APP" "$PKG_APP"
          mkdir -p "$PKG_APP/Contents/Helpers"
          cp ../cli/dist/osx-arm64/elevate "$PKG_APP/Contents/Helpers/elevate"
          chmod 755 "$PKG_APP/Contents/Helpers/elevate"
          test ! -e "$APP/Contents/Helpers"

      - name: Sign (Developer ID)
        if: env.SIGNED == '1'
        env:
          APPLE_TEAM_ID: ${{ secrets.APPLE_TEAM_ID }}
        run: |
          test -n "$APPLE_TEAM_ID" || { echo "APPLE_TEAM_ID secret missing"; exit 1; }
          BUNDLE_ID=$(plutil -extract CFBundleIdentifier raw "$APP/Contents/Info.plist")
          cat > entitlements.plist <<EOF
          <?xml version="1.0" encoding="UTF-8"?>
          <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
          <plist version="1.0"><dict>
          <key>com.apple.application-identifier</key><string>$APPLE_TEAM_ID.$BUNDLE_ID</string>
          <key>com.apple.developer.team-identifier</key><string>$APPLE_TEAM_ID</string>
          <key>keychain-access-groups</key><array>
          <string>$APPLE_TEAM_ID.$BUNDLE_ID</string>
          <string>$APPLE_TEAM_ID.com.microsoft.identity.universalstorage</string>
          </array>
          </dict></plist>
          EOF
          # Inside-out, never --deep: frameworks, then the helper with the .NET host entitlements,
          # then each app bundle with its own entitlements and the embedded profile.
          for app in "$APP" "$PKG_APP"; do
            cp "$HOME/Library/MobileDevice/Provisioning Profiles/"*.provisionprofile "$app/Contents/embedded.provisionprofile"
            for fw in "$app"/Contents/Frameworks/*.framework; do
              [ -d "$fw" ] && codesign --force --options runtime --timestamp -s "Developer ID Application" "$fw"
            done
            if [ -f "$app/Contents/Helpers/elevate" ]; then
              codesign --force --options runtime --timestamp --entitlements ../cli/elevate.entitlements -s "Developer ID Application" "$app/Contents/Helpers/elevate"
              codesign -d --entitlements :- "$app/Contents/Helpers/elevate" | grep -q com.apple.security.cs.allow-jit
            fi
            codesign --force --options runtime --timestamp --entitlements entitlements.plist -s "Developer ID Application" "$app"
            codesign --verify --deep --strict --verbose=2 "$app"
            codesign -dvv "$app" 2>&1 | grep -E "Authority=Developer ID Application|TeamIdentifier"
            codesign -d --entitlements :- "$app" | grep -q keychain-access-groups
          done

      - name: Sign (ad-hoc)
        if: env.SIGNED != '1'
        run: |
          codesign --force --deep -s - "$APP"
          codesign --force --deep -s - "$PKG_APP"

      # The helper must run under the hardened runtime with the entitlements above, and report
      # the same version the app's About shows; both are checked after signing, on the runner.
      - name: Check versions
        run: |
          app_version=$(plutil -extract CFBundleShortVersionString raw "$PKG_APP/Contents/Info.plist")
          cli_version=$("$PKG_APP/Contents/Helpers/elevate" --version)
          echo "app $app_version, cli $cli_version, tag $VERSION"
          test "$app_version" = "$VERSION" && test "$cli_version" = "$VERSION"
```

Delete the old `Build (Developer ID)` / `Build (unsigned, ad-hoc signature)` steps entirely; the entitlements heredoc and the `PROFILE_NAME` handling in "Import Developer ID certificate" stay as they are.

- [ ] **Step 3: Notarize both app copies and keep the log**

Replace the "Notarize" step with:

```yaml
      - name: Notarize
        if: env.SIGNED == '1'
        env:
          APPLE_ID: ${{ secrets.APPLE_ID }}
          APPLE_TEAM_ID: ${{ secrets.APPLE_TEAM_ID }}
          APPLE_APP_PASSWORD: ${{ secrets.APPLE_APP_PASSWORD }}
        run: |
          for app in "$APP" "$PKG_APP"; do
            rm -f notarize.zip
            ditto -c -k --keepParent "$app" notarize.zip
            xcrun notarytool submit notarize.zip --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" --wait | tee submit.txt
            id=$(awk '/^ *id:/ { print $2; exit }' submit.txt)
            # The log lists every file the notary service examined; with the helper inside the
            # pkg copy it must come back Accepted with no issues.
            xcrun notarytool log "$id" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" | tee "notarization-$(basename "$(dirname "$app")").json"
            grep -q '"status": "Accepted"' "notarization-$(basename "$(dirname "$app")").json"
            xcrun stapler staple "$app"
          done
```

- [ ] **Step 4: Point the DMG at the app copy and the pkg at the helper copy, with the scripts**

In "Package DMG" replace `cp -R build/Build/Products/Release/Elevate.app dist/dmg/` with `cp -R "$APP" dist/dmg/` (the rest unchanged).

In "Package pkg" replace the `pkgbuild` invocation with:

```bash
          pkgbuild --component "$PKG_APP" --install-location /Applications --scripts pkg/scripts \
            --identifier no.reothor.elevate --version "$VERSION" "dist/Elevate-$VERSION-unsigned.pkg"
          pkgutil --expand "dist/Elevate-$VERSION-unsigned.pkg" build/pkg-expanded
          test -x build/pkg-expanded/Scripts/postinstall
          rm -rf build/pkg-expanded
```

Update the comment above "Package pkg" to say the pkg carries the CLI in `Contents/Helpers` and the postinstall links `/usr/local/bin/elevate`.

- [ ] **Step 5: Entitlements for the standalone macOS binaries in the `cli` job**

In "Sign and notarize (macOS)" change the `codesign --force --options runtime --timestamp -s "Developer ID Application" "$bin"` line to:

```bash
            codesign --force --options runtime --timestamp --entitlements cli/elevate.entitlements -s "Developer ID Application" "$bin"
```

- [ ] **Step 6: Lint the workflow**

Run: `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/release.yml')); print('yaml OK')"` (if PyYAML is missing: `ruby -ryaml -e 'YAML.load_file(".github/workflows/release.yml"); puts "yaml OK"'`). If `actionlint` is installed (`brew list actionlint`), run `actionlint .github/workflows/release.yml` too.
Expected: `yaml OK`, no actionlint errors.

- [ ] **Step 7: Dry-run the shell of the new steps locally**

The runner logic can be exercised on this Mac with the ad-hoc path:

```bash
cd macos && xcodegen generate >/dev/null
xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Release -derivedDataPath build-plan MARKETING_VERSION=1.2.3 CURRENT_PROJECT_VERSION=1 CODE_SIGN_IDENTITY="" CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO build | tail -2
../cli/package.sh publish 1.2.3 osx-arm64
APP=build-plan/Build/Products/Release/Elevate.app; PKG_APP=build-plan/pkgroot/Elevate.app
rm -rf build-plan/pkgroot && mkdir -p build-plan/pkgroot && cp -R "$APP" "$PKG_APP" && mkdir -p "$PKG_APP/Contents/Helpers" && cp ../cli/dist/osx-arm64/elevate "$PKG_APP/Contents/Helpers/elevate"
codesign --force --deep -s - "$PKG_APP" && codesign --verify --deep --strict --verbose=2 "$PKG_APP"
test "$("$PKG_APP/Contents/Helpers/elevate" --version)" = 1.2.3 && test "$(plutil -extract CFBundleShortVersionString raw "$PKG_APP/Contents/Info.plist")" = 1.2.3 && echo "versions OK"
pkgbuild --component "$PKG_APP" --install-location /Applications --scripts pkg/scripts --identifier no.reothor.elevate --version 1.2.3 build-plan/Elevate-1.2.3.pkg
pkgutil --expand build-plan/Elevate-1.2.3.pkg build-plan/expanded && test -x build-plan/expanded/Scripts/postinstall && echo "pkg OK"
ls -la build-plan/Elevate-1.2.3.pkg; rm -rf build-plan ../cli/dist/osx-arm64; cd ..
```

Expected: `versions OK`, `pkg OK`, pkg roughly 25 MB. Do not commit `build-plan`.

- [ ] **Step 8: Update docs/releasing.md**

In "What the workflow does" → **macos**, rewrite items 3–6 to describe: one unsigned build; the osx-arm64 CLI published with `cli/package.sh publish` and copied to `Contents/Helpers/elevate` in a second copy of the app under `build/pkgroot`; signing inside-out (frameworks, the helper with `cli/elevate.entitlements`, then the app), never `--deep`; the `Check versions` assertion (`elevate --version` equals `CFBundleShortVersionString` equals the tag); both copies notarized with the log fetched and required to be Accepted; the DMG from the plain copy (unchanged, app only), the pkg from the helper copy with `--scripts macos/pkg/scripts` whose `postinstall` links `/usr/local/bin/elevate` on Apple Silicon. In **cli** item 3 mention the entitlements file. In the manual checklist (step 4 of "Cutting a release") replace the `brew upgrade frodehus/elevate/elevate-cli` line with `brew upgrade --cask frodehus/elevate/elevate && elevate --version` and add "`sudo installer -pkg Elevate-x.y.z.pkg -target /` on a Mac, then `which elevate` shows `/usr/local/bin/elevate`; run one MSI and `elevate --version` in a new terminal".

- [ ] **Step 9: Commit**

```bash
git add .github/workflows/release.yml docs/releasing.md
git commit -m "ci(release): bundle the CLI in the macOS pkg, sign the helper inside-out, assert matching versions"
```

---

### Task 3: Cask over the pkg, formula deprecation

**Files:**
- Modify: `scripts/update-cask.sh`, `scripts/update-formula.sh`
- Regenerate: `Casks/elevate.rb`, `Formula/elevate-cli.rb`
- Modify: `.github/workflows/release.yml` (publish job: `Versions and hashes` already exports `PKG_SHA256`; the cask update call must pass it instead of `DMG_SHA256`; release notes)

**Interfaces:**
- Consumes: `dist/Elevate-<v>.pkg.sha256` (existing artifact), `PKG_SHA256` env in the publish job.
- Produces: `scripts/update-cask.sh <version> <pkg-sha256> <owner/repo> [signed]` (argument 2 changes meaning from the DMG hash to the pkg hash).

- [ ] **Step 1: Rewrite the cask template**

In `scripts/update-cask.sh` change the usage comment (`sha256    SHA-256 of Elevate-<version>.pkg`), the unsigned caveat, and the heredoc:

```bash
CAVEATS=""
if [ "$SIGNED" != "1" ]; then
  CAVEATS='
  caveats "This build is not signed with a Developer ID. If macOS blocks the app on first launch, open System Settings > Privacy & Security, scroll to the message about Elevate and click Open Anyway."'
fi

cat > "$OUT" <<EOF
cask "elevate" do
  version "$VERSION"
  sha256 "$SHA256"

  url "https://github.com/$REPO/releases/download/v#{version}/Elevate-#{version}.pkg"
  name "Elevate"
  desc "Just-in-time Entra, Azure and PIM for Groups activation from the menu bar and the terminal"
  homepage "https://github.com/$REPO"

  depends_on macos: :tahoe

  # The installer package puts Elevate.app in /Applications and links /usr/local/bin/elevate to
  # the CLI bundled in Elevate.app/Contents/Helpers (Apple Silicon; Intel Macs use the
  # elevate-cli archive). A pkg needs sudo, which is why Homebrew asks for a password.
  pkg "Elevate-#{version}.pkg"

  uninstall quit:    "no.reothor.elevate",
            pkgutil: "no.reothor.elevate",
            delete:  "/usr/local/bin/elevate"

  zap trash: [
    "~/Library/Application Support/Elevate",
    "~/Library/Preferences/no.reothor.elevate.plist",
  ]$CAVEATS
end
EOF
```

- [ ] **Step 2: Add the deprecation to the formula template**

In `scripts/update-formula.sh`, after the `license "MIT"` line inside the heredoc, add:

```ruby

  # The CLI now ships inside the Elevate cask (via the pkg) and the MSI. This formula is kept for
  # one more release for Linux and Intel Macs; the release archives stay available afterwards.
  deprecate! date: "$DEPRECATED_ON", because: "the elevate CLI is installed by the elevate cask and the macOS pkg; Linux and Intel Macs use the elevate-cli archives from the GitHub release"
```

and define `DEPRECATED_ON="2026-09-10"` near the top (a fixed date: the first release carrying the notice, not the run date, so re-runs do not move it).

- [ ] **Step 3: Regenerate both committed files for the current release (1.6.0) and check them**

```bash
PKG_SHA=$(gh release download v1.6.0 --repo FrodeHus/elevate --pattern 'Elevate-1.6.0.pkg.sha256' -O - | cut -d' ' -f1)
./scripts/update-cask.sh 1.6.0 "$PKG_SHA" FrodeHus/elevate 1
mkdir -p /tmp/plan-dist && for rid in osx-arm64 osx-x64 linux-x64 linux-arm64; do gh release download v1.6.0 --repo FrodeHus/elevate --pattern "elevate-cli-1.6.0-$rid.tar.gz.sha256" -D /tmp/plan-dist --clobber; done
./scripts/update-formula.sh 1.6.0 FrodeHus/elevate /tmp/plan-dist
git diff --stat Casks Formula
ruby -c Casks/elevate.rb && ruby -c Formula/elevate-cli.rb
brew style Casks/elevate.rb Formula/elevate-cli.rb
```

Expected: the cask URL ends in `.pkg` with the pkg hash; the formula's four hashes unchanged from before plus the `deprecate!` line; `Syntax OK` twice; `brew style` clean (fix any offence it reports, e.g. alignment in the `uninstall` hash, and re-run).

- [ ] **Step 4: Point the publish job at the pkg hash**

In `release.yml` → `publish` → "Update the cask and the formula on main", change

```bash
            ./scripts/update-cask.sh "$VERSION" "$DMG_SHA256" "${{ github.repository }}" "${MAC_SIGNED:-}"
```

to

```bash
            ./scripts/update-cask.sh "$VERSION" "$PKG_SHA256" "${{ github.repository }}" "${MAC_SIGNED:-}"
```

Update the comment above the step: the cask now installs the pkg.

- [ ] **Step 5: Release notes**

In the "Release notes" step:

- macOS section: after the three `brew` lines drop the `xattr` line for the unsigned case (a pkg install carries no quarantine flag) and add, before "Or download the DMG": `echo 'The cask installs the pkg (Homebrew asks for your password), which also puts the `elevate` CLI on your PATH.'`. Keep the "Or download `Elevate-$VERSION.dmg`…" sentence and append "The DMG is the app alone." to it.
- CLI section: replace the Homebrew block with:

```bash
            echo 'macOS (Apple Silicon): the CLI is installed with the app by the Homebrew cask above or by `Elevate-'"$VERSION"'.pkg`, as `/usr/local/bin/elevate`. The `elevate-cli` formula is deprecated and will be removed in a later release; it still installs on Linux and Intel Macs:'
            echo
            echo '    brew install frodehus/elevate/elevate-cli'
            echo
            echo 'Windows: `Elevate-'"$VERSION"'-x64.msi` (or `-arm64.msi`) installs `elevate.exe` next to the app and adds the folder to your PATH. Standalone: `winget install Reothor.Elevate.CLI` once the manifest is submitted; until then download `elevate-cli-'"$VERSION"'-win-x64.zip` (or `-win-arm64.zip`) below and put `elevate.exe` on your PATH.'
```

- Enterprise section: in both pkg sentences add ", and installs the `elevate` CLI (`/usr/local/bin/elevate`, Apple Silicon)" after "macOS installer package".

- [ ] **Step 6: Lint and commit**

Run the YAML lint from Task 2 Step 6 again, then:

```bash
git add scripts/update-cask.sh scripts/update-formula.sh Casks/elevate.rb Formula/elevate-cli.rb .github/workflows/release.yml
git commit -m "feat(homebrew): the cask installs the pkg (app + CLI); deprecate the elevate-cli formula"
```

---

### Task 4: Windows MSI — CLI component on the user PATH

**Files:**
- Modify: `windows/installer/Elevate.wxs`
- Modify: `windows/installer/build.ps1`
- Modify: `windows/README.md` ("Install" and "Installer and winget manifest" sections)

**Interfaces:**
- Consumes: `cli/src/Elevate.Cli/Elevate.Cli.csproj` (publishes single-file with `-r win-<arch>`; `-p:Version=<v>`).
- Produces: preprocessor variable `CliDir` for `wix build`; `build.ps1` publishes the CLI to `windows/installer/publish\cli-<arch>\elevate.exe`.

- [ ] **Step 1: Add the CLI component to Elevate.wxs**

Under the file header comment add `CliDir  the dotnet publish output of the CLI for this architecture (holds elevate.exe)` to the preprocessor variable list. In `<Feature Id="Main">` add `<ComponentRef Id="Cli" />`. After the `PublishedFiles` component group add:

```xml
    <!-- The elevate CLI next to the app, and the install folder appended to the user's PATH so
         `elevate` resolves in any new terminal. A per-user package can only write HKCU, hence
         System="no"; the entry is removed on uninstall (Permanent="no"). -->
    <Component Id="Cli" Directory="INSTALLFOLDER" Guid="3E7A9C15-2B4D-4F86-A1C3-8D5E6F7A9B02">
      <File Id="ElevateCli" Source="$(var.CliDir)\elevate.exe" KeyPath="yes" />
      <Environment Id="CliOnUserPath" Name="PATH" Value="[INSTALLFOLDER]" Action="set" Part="last" Permanent="no" System="no" />
    </Component>
```

Update the `SummaryInformation Description` and the header comment to mention the CLI ("…into %LOCALAPPDATA%\Programs\Elevate with a Start Menu shortcut and the elevate CLI on the user's PATH").

- [ ] **Step 2: Publish and sign the CLI in build.ps1**

Add `$cliProject = Join-Path $root "..\cli\src\Elevate.Cli\Elevate.Cli.csproj"` after `$project`. Extract the signing block into a function placed before the loop:

```powershell
function Sign-File([string]$Path, [string]$Arch) {
    Write-Host "== Signing $Path"
    $dlib = $env:AZURE_TRUSTED_SIGNING_DLIB
    $metadata = Join-Path $out "signing-$Arch.json"
    @{
        Endpoint = $env:AZURE_TRUSTED_SIGNING_ENDPOINT
        CodeSigningAccountName = $env:AZURE_TRUSTED_SIGNING_ACCOUNT
        CertificateProfileName = $env:AZURE_TRUSTED_SIGNING_PROFILE
    } | ConvertTo-Json | Set-Content -Path $metadata -Encoding ascii
    signtool sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib $dlib /dmdf $metadata $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $Path" }
}
```

Inside the loop, after the app publish and before `wix build`:

```powershell
    # The CLI: self-contained single file (the csproj's publish settings), signed before it goes
    # into the package so the MSI carries a signed elevate.exe.
    $cliDir = Join-Path $installer "publish\cli-$arch"
    if (Test-Path $cliDir) { Remove-Item -Recurse -Force $cliDir }
    Write-Host "== Publishing the CLI for $arch"
    dotnet publish $cliProject -c $Configuration -r "win-$arch" -p:Version=$Version -o $cliDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for the CLI ($arch)" }
    Remove-Item -Force (Join-Path $cliDir "*.pdb") -ErrorAction SilentlyContinue
    if (-not (Test-Path (Join-Path $cliDir "elevate.exe"))) { throw "elevate.exe missing after publish ($arch)" }
    if ($arch -eq "x64" -or $env:PROCESSOR_ARCHITECTURE -eq "ARM64") {
        # Runs natively on an x64 host (x64) or an Arm host (x64 under emulation, arm64 native).
        $reported = (& (Join-Path $cliDir "elevate.exe") --version).Trim()
        if ($reported -ne $Version) { throw "elevate --version printed '$reported', expected '$Version'" }
    }
    if ($Sign) { Sign-File (Join-Path $cliDir "elevate.exe") $arch }
```

Pass `-d "CliDir=$cliDir"` to `wix build`, and replace the old inline signing block after the build with `if ($Sign) { Sign-File $msi $arch }`. Update the `.SYNOPSIS`/`.DESCRIPTION` to mention the CLI ("publishes the app and the CLI…; the MSI installs both and puts the folder on the user's PATH").

- [ ] **Step 3: Validate what can be validated on macOS**

```bash
xmllint --noout windows/installer/Elevate.wxs && echo "wxs well-formed"
pwsh -NoProfile -Command '$null = [scriptblock]::Create((Get-Content -Raw windows/installer/build.ps1)); "ps1 parses"'
```

Then try a WiX build on this Mac (WiX v5 is a cross-platform .NET tool; if `dotnet tool install` or `wix build` fails for platform reasons, record the error in the report and move on — the release workflow on `windows-latest` is the real check):

```bash
dotnet tool install --global wix --version 5.0.2 && wix extension add --global WixToolset.UI.wixext/5.0.2
TMP="$(mktemp -d)"; mkdir -p "$TMP/app" "$TMP/cli"; printf 'x' > "$TMP/app/Elevate.exe"; printf 'x' > "$TMP/cli/elevate.exe"
wix build windows/installer/Elevate.wxs -arch x64 -d "Version=0.0.1" -d "PublishDir=$TMP/app" -d "CliDir=$TMP/cli" -ext WixToolset.UI.wixext -b windows/installer -o "$TMP/Elevate.msi" && ls -la "$TMP/Elevate.msi"
rm -rf "$TMP"
```

Expected: `wxs well-formed`, `ps1 parses`; ideally an MSI is produced. Also confirm the CLI publishes for Windows from here (cross-compile): `./cli/package.sh publish 1.2.3 win-x64 && ls -la cli/dist/win-x64/elevate.exe && rm -rf cli/dist/win-x64`.

- [ ] **Step 4: Windows README**

"Install": after the sentence about the Start Menu entry add: "It also installs the `elevate` command-line tool next to the app and adds that folder to your user PATH, so `elevate --version` works in a new terminal (an already-open terminal needs to be restarted). If you also installed the standalone CLI with winget (`Reothor.Elevate.CLI`), uninstall one of them so a single `elevate` is on the PATH." Change "uninstall from Settings > Apps" to "uninstall from Settings > Apps, which removes the app, the CLI and the PATH entry".

"Installer and winget manifest": say that `build.ps1` also publishes the CLI (`../cli/src/Elevate.Cli`) self-contained per architecture, signs `elevate.exe` when `-Sign` is given, and passes it to WiX as `CliDir`; the MSI's `Cli` component adds the install folder to the user's PATH (HKCU; the package is per-user, so the machine PATH is out of reach).

- [ ] **Step 5: Commit**

```bash
git add windows/installer/Elevate.wxs windows/installer/build.ps1 windows/README.md
git commit -m "feat(windows): install the elevate CLI with the MSI and put it on the user PATH"
```

---

### Task 5: `elevate update` hint for bundled installs

**Files:**
- Create: `cli/src/Elevate.Cli/Update/UpgradeHint.cs`
- Create: `cli/tests/Elevate.Cli.Tests/UpgradeHintTests.cs`
- Modify: `cli/src/Elevate.Cli/Commands/MiscCommands.cs:138`

**Interfaces:**
- Produces: `internal static class UpgradeHint { public static string For(string? executablePath, bool isWindows, bool isMacOS) }` returning one line of plain text (no Spectre markup).

- [ ] **Step 1: Write the failing tests**

`cli/tests/Elevate.Cli.Tests/UpgradeHintTests.cs` (match the existing test style: xUnit + FluentAssertions, `namespace Elevate.Cli.Tests;`, `using Elevate.Cli.Update;`):

```csharp
using Elevate.Cli.Update;
using FluentAssertions;

namespace Elevate.Cli.Tests;

public class UpgradeHintTests
{
    [Fact]
    public void Helper_inside_the_mac_app_bundle_points_at_the_app()
    {
        UpgradeHint.For("/Applications/Elevate.app/Contents/Helpers/elevate", isWindows: false, isMacOS: true)
            .Should().Be("Installed with Elevate.app: upgrade the app (brew upgrade --cask frodehus/elevate/elevate, or the newer Elevate pkg).");
    }

    [Fact]
    public void Standalone_mac_binary_names_the_formula_and_the_archive()
    {
        UpgradeHint.For("/opt/homebrew/bin/elevate", isWindows: false, isMacOS: true)
            .Should().Be("Homebrew: brew upgrade frodehus/elevate/elevate-cli (deprecated; the elevate cask now installs the CLI) · or download the newer elevate-cli archive.");
    }

    [Fact]
    public void Exe_next_to_the_windows_app_points_at_the_msi()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elevate-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "cli"));
        File.WriteAllText(Path.Combine(dir, "Elevate.exe"), "");
        try
        {
            UpgradeHint.For(Path.Combine(dir, "cli", "elevate.exe"), isWindows: true, isMacOS: false)
                .Should().Be("Installed by the Elevate MSI: run the newer Elevate-<version>-x64.msi or -arm64.msi.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Standalone_windows_exe_names_winget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elevate-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            UpgradeHint.For(Path.Combine(dir, "elevate.exe"), isWindows: true, isMacOS: false)
                .Should().Be("winget: winget upgrade Reothor.Elevate.CLI · or download the newer elevate-cli zip.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Linux_names_the_formula_and_the_archive()
    {
        UpgradeHint.For("/usr/local/bin/elevate", isWindows: false, isMacOS: false)
            .Should().Be("Homebrew: brew upgrade frodehus/elevate/elevate-cli · or download the newer elevate-cli archive.");
    }

    [Fact]
    public void Unknown_path_falls_back_to_the_archive()
    {
        UpgradeHint.For(null, isWindows: false, isMacOS: false)
            .Should().Be("Homebrew: brew upgrade frodehus/elevate/elevate-cli · or download the newer elevate-cli archive.");
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test cli/Elevate.Cli.sln --filter "FullyQualifiedName~UpgradeHintTests"`
Expected: build error `The type or namespace name 'UpgradeHint' could not be found`.

- [ ] **Step 3: Implement**

`cli/src/Elevate.Cli/Update/UpgradeHint.cs`:

```csharp
namespace Elevate.Cli.Update;

/// <summary>
/// The one-line "how to upgrade" note under an available update. The CLI is installed four ways
/// (inside Elevate.app by the pkg or the cask, in the cli folder under Elevate.exe by the MSI, the Homebrew
/// formula, a bare archive or winget) and the right instruction depends on which one this
/// executable came from.
/// </summary>
internal static class UpgradeHint
{
    public static string For(string? executablePath, bool isWindows, bool isMacOS)
    {
        if (isMacOS && executablePath is not null
            && executablePath.Contains(".app/Contents/Helpers/", StringComparison.Ordinal))
        {
            return "Installed with Elevate.app: upgrade the app (brew upgrade --cask frodehus/elevate/elevate, or the newer Elevate pkg).";
        }

        if (isWindows)
        {
            var directory = executablePath is null ? null : Path.GetDirectoryName(executablePath);
            // The MSI installs the CLI in the `cli` subfolder of the app's folder (elevate.exe and
            // Elevate.exe cannot share a directory on a case-insensitive file system).
            var parent = directory is null ? null : Path.GetDirectoryName(directory);
            if (parent is not null && File.Exists(Path.Combine(parent, "Elevate.exe")))
            {
                return "Installed by the Elevate MSI: run the newer Elevate-<version>-x64.msi or -arm64.msi.";
            }
            return "winget: winget upgrade Reothor.Elevate.CLI · or download the newer elevate-cli zip.";
        }

        if (isMacOS)
        {
            return "Homebrew: brew upgrade frodehus/elevate/elevate-cli (deprecated; the elevate cask now installs the CLI) · or download the newer elevate-cli archive.";
        }

        return "Homebrew: brew upgrade frodehus/elevate/elevate-cli · or download the newer elevate-cli archive.";
    }
}
```

In `MiscCommands.cs` replace the `context.Output.Note("Homebrew: brew upgrade …")` line with:

```csharp
                context.Output.Note(Markup.Escape(UpgradeHint.For(Environment.ProcessPath, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS())));
```

(`MiscCommands.cs` already has `using Elevate.Cli.Update;` and `using Spectre.Console;`. `Output.Note(string markup)` renders Spectre markup, hence the `Markup.Escape`.)

- [ ] **Step 4: Run the whole CLI suite**

Run: `dotnet test cli/Elevate.Cli.sln`
Expected: all tests pass, warnings-as-errors clean.

- [ ] **Step 5: Commit**

```bash
git add cli/src/Elevate.Cli/Update/UpgradeHint.cs cli/tests/Elevate.Cli.Tests/UpgradeHintTests.cs cli/src/Elevate.Cli/Commands/MiscCommands.cs
git commit -m "feat(cli): upgrade hint names the app installer when elevate was installed with the app"
```

---

### Task 6: User-facing docs and changelog

**Files:**
- Modify: `cli/README.md` ("Install", "Build and test", "Release"), `macos/README.md` ("Install"), `README.md` ("Install", "Repository layout"), `CONTRIBUTING.md` ("Releases"), `docs/README.md` (the releasing bullet), `CHANGELOG.md` (`## [Unreleased]`)

- [ ] **Step 1: cli/README.md**

Replace the "Install" section's first two paragraphs with:

```markdown
**With the app (macOS on Apple Silicon, Windows).** The Homebrew cask and the macOS installer
package put the CLI on your PATH as `/usr/local/bin/elevate` (it lives inside
`Elevate.app/Contents/Helpers`); the Windows MSI installs `elevate.exe` next to the app and adds
the folder to your user PATH. Install the app and you have the CLI at the same version:
[macos/README.md](../macos/README.md#install), [windows/README.md](../windows/README.md#install).

**Homebrew formula (Linux, Intel Macs).** The formula lives in this repository, which doubles as a
tap. It is **deprecated**: the cask now carries the CLI, and the formula will be removed in a later
release. Linux and Intel Macs keep the archives below.

```bash
brew tap FrodeHus/elevate https://github.com/FrodeHus/elevate
brew trust frodehus/elevate        # Homebrew 6 requires trusting third-party taps
brew install frodehus/elevate/elevate-cli
```

Shell completions are installed with it. Upgrade with `brew upgrade frodehus/elevate/elevate-cli`.

**Windows, standalone.** `winget install Reothor.Elevate.CLI` once the manifest is submitted (winget
moderation requires signed binaries, so the manifest is a release artifact until Azure Artifact
Signing is set up, like the app's). Until then, download `elevate-cli-<version>-win-x64.zip` (or
`-win-arm64.zip`) from the [latest release](https://github.com/FrodeHus/elevate/releases/latest)
and put `elevate.exe` on your PATH. Do not combine this with the MSI's copy: two `elevate` entries
on the PATH means whichever comes first wins.
```

Keep the "Any platform" paragraph and the completions block as they are. In "Build and test" add after the `package.sh` block: "On macOS the release workflow signs the binary with the hardened runtime and `cli/elevate.entitlements` (JIT and unsigned executable memory for the .NET runtime, no library validation); the same file signs the copy bundled in `Elevate.app/Contents/Helpers`." In "Release" rewrite the paragraph: the six archives as before, *plus* the osx-arm64 binary bundled in the pkg and the win-x64/win-arm64 binaries in the MSIs; the formula is regenerated with its deprecation notice.

- [ ] **Step 2: macos/README.md "Install"**

After the Homebrew block replace the "Plain `brew install --cask`…" paragraph with two: the existing note about the fully qualified name, then "The cask installs `Elevate-<version>.pkg`, so Homebrew asks for your password. The package puts Elevate in `/Applications` and, on Apple Silicon, links `/usr/local/bin/elevate` to the command-line tool bundled inside the app (`Elevate.app/Contents/Helpers/elevate`); `brew uninstall --cask frodehus/elevate/elevate` removes both." In the DMG paragraph append: "The DMG is the app alone — a drag-install cannot add anything to your PATH; the CLI for that route is [cli/README.md](../cli/README.md#install)."

- [ ] **Step 3: README.md**

"Install" bullets:

```markdown
- **macOS 26**: Homebrew cask or DMG, see [macos/README.md](macos/README.md#install). The cask
  and the pkg also install the `elevate` CLI; the DMG is the app alone.
- **Windows 11**: per-user MSI, which installs the app and the `elevate` CLI, see
  [windows/README.md](windows/README.md#install).
- **CLI on its own** (Linux, Intel Macs, servers): a single binary from the release, or the
  deprecated Homebrew formula, see [cli/README.md](cli/README.md#install).
```

Repository layout line: `Casks/, Formula/   The Homebrew tap: the cask (app + CLI via the pkg) and the deprecated CLI formula`.

- [ ] **Step 4: CONTRIBUTING.md and docs/README.md**

CONTRIBUTING "Releases": "…publishes one GitHub Release with the DMG, the pkg and MSIs (which carry the CLI), the standalone CLI archives, and updates the Homebrew cask and the deprecated formula." docs/README.md releasing bullet: "…the Homebrew cask (which installs the pkg) and the deprecated CLI formula."

- [ ] **Step 5: CHANGELOG.md under `## [Unreleased]`**

Under `### Added`:

```markdown
- macOS: `Elevate-<version>.pkg` now carries the `elevate` CLI inside the app
  (`Elevate.app/Contents/Helpers/elevate`, Apple Silicon) and links `/usr/local/bin/elevate` to
  it, so a Jamf or Intune rollout — or `sudo installer -pkg` — installs the app and the CLI at the
  same version. The helper is signed with the hardened runtime and notarized with the app.
- Windows: the MSI installs `elevate.exe` next to the app and adds the install folder to the
  user's PATH; uninstalling removes both.
- CLI: `elevate update` tells you how to upgrade for the way this copy was installed (with the
  app, the formula, winget or an archive).
```

Under `### Changed`:

```markdown
- Homebrew: the `elevate` cask installs the pkg instead of the DMG, so `brew install --cask
  frodehus/elevate/elevate` now yields the app and the CLI (and asks for your password, as pkg
  casks do). The DMG is unchanged: the app alone.
```

Add a `### Deprecated` section (after Changed, before Fixed):

```markdown
### Deprecated

- Homebrew: the `elevate-cli` formula. The cask and the pkg install the CLI on Apple Silicon Macs;
  the formula stays one more release for Linux and Intel Macs, which keep the
  `elevate-cli-<version>-<rid>` archives afterwards.
```

- [ ] **Step 6: Check links and commit**

Run: `grep -n "elevate-cli\|Helpers/elevate\|/usr/local/bin/elevate" README.md cli/README.md macos/README.md CHANGELOG.md | head -40` and eyeball; then

```bash
git add README.md cli/README.md macos/README.md CONTRIBUTING.md docs/README.md CHANGELOG.md
git commit -m "docs: install the CLI with the app; deprecate the formula"
```

---

### Task 7: Enterprise docs — the CLI in the pkg and the MSI, uninstall guidance

**Files:**
- Modify: `docs/enterprise/README.md`, `docs/enterprise/cli.md`, `docs/enterprise/macos-jamf.md`, `docs/enterprise/macos-intune.md`, `docs/enterprise/windows-intune.md`, `docs/enterprise/windows-group-policy.md`, `enterprise/README.md`

- [ ] **Step 1: docs/enterprise/README.md**

In "What you download", extend the pkg bullet: "It also carries the `elevate` CLI inside the app bundle (Apple Silicon) and links `/usr/local/bin/elevate` to it at install time, so the Macs you deploy to get the CLI at the same version." Replace "The CLI is a single binary from the same release." with "The Windows MSI installs the CLI next to the app and adds the folder to the user's PATH. For Linux, Intel Macs and servers the CLI is a single binary from the same release ([cli.md](cli.md))."

Add a new section before "## How you verify, whatever you used":

```markdown
## Uninstalling

**macOS (installed by the pkg, by Jamf, Intune or `installer`).** The package leaves two things
behind: the app and the `/usr/local/bin/elevate` link. Remove both and forget the receipt:

```bash
sudo rm -rf /Applications/Elevate.app
sudo rm -f /usr/local/bin/elevate
sudo pkgutil --forget no.reothor.elevate
```

Per-user data stays in `~/Library/Application Support/Elevate` and the keychain; the
configuration profile is removed from the MDM like any other. A Jamf policy with a script
running the three commands, or an Intune shell script, does the same across the fleet. Homebrew
users run `brew uninstall --cask frodehus/elevate/elevate`, which performs the same steps.

**Windows.** `msiexec /x Elevate-<version>-x64.msi /qn` (or Settings → Apps) removes the app, the
CLI and the PATH entry the MSI added.
```

- [ ] **Step 2: docs/enterprise/cli.md**

Prerequisite bullet "The CLI installed…" becomes: "The CLI installed. Macs that get `Elevate-<version>.pkg` (Jamf, Intune) and Windows PCs that get the MSI already have it, at `/usr/local/bin/elevate` and next to `Elevate.exe`; Linux workstations and servers take the single binary from the release ([cli/README.md](../../cli/README.md#install))."

- [ ] **Step 3: macos-jamf.md and macos-intune.md**

In each, where the pkg's effect is described ("The pkg installs into `/Applications/Elevate.app` and carries no company values…"): add "It also links `/usr/local/bin/elevate` to the CLI bundled inside the app on Apple Silicon Macs, so the same policy delivers the command-line tool (Intel Macs get the app only; see [cli.md](cli.md) for those)." Add a sentence at the end of the "Verify" section in each: "`elevate --version` in Terminal prints the deployed version; `elevate config managed` reads `/etc/elevate/managed.json`, not the configuration profile — see [cli.md](cli.md)." Add a one-line "To remove Elevate, see [Uninstalling](README.md#uninstalling)." to each page's closing section.

- [ ] **Step 4: windows-intune.md and windows-group-policy.md**

Replace "If the CLI is on the same PCs, it reads the same registry keys:" with "The MSI installs the `elevate` CLI next to the app, on the user's PATH; it reads the same registry keys:". In windows-intune.md step 1 (Program), after the uninstall command add a note: "Uninstall also removes the CLI and the PATH entry." Keep the detection rule as is (Elevate.exe).

- [ ] **Step 5: enterprise/README.md (the kit)**

In the intro after "one signed build per platform reads the values you push." add: "The macOS pkg and the Windows MSI in the same release install the `elevate` CLI with the app; `cli/managed.json` below is how the CLI on macOS and Linux receives your values."

- [ ] **Step 6: Validate the kit and commit**

Run: `python3 scripts/validate-enterprise-kit.py` (the kit README is validated for repo-URL rules; keep any new links pointing at the repo).
Expected: the summary line, exit 0.

```bash
git add docs/enterprise enterprise/README.md
git commit -m "docs(enterprise): the pkg and MSI install the CLI; uninstall guidance"
```

---

## Self-review against the issue

| Issue item | Task |
|---|---|
| pkg: embed in `Contents/Helpers/`, postinstall symlink `/usr/local/bin/elevate` | 1, 2 |
| pkg: uninstall guidance in `docs/enterprise/` | 7 |
| MSI: CLI component + PATH, both architectures | 4 (user PATH; see deviation 2) |
| Cask: `brew install --cask` puts `elevate` on PATH | 3 (via the pkg; see deviation 1) |
| Formula: deprecate first, one release notice | 3, 6 |
| DMG unchanged | 2 (DMG from the helper-less copy), 6 |
| Standalone archives kept, same tag | unchanged; 2 adds entitlements |
| Sign helper before app with the three entitlements, same identity, timestamp; no `--deep`; notarization log shows accepted | 2 |
| Keep the CLI outside a future App Sandbox | the helper's entitlements file has no sandbox key (Task 1); noted in `cli/README.md` (Task 6) |
| `release.yml`: build CLI first, copy before signing, MSI job builds the win binaries | 2, 4 |
| Version stamping: `elevate --version` equals the app's About | 2 (`Check versions` step), 4 (build.ps1 check) |
| Docs: `docs/releasing.md`, `cli/README.md`, `windows/README.md`, `docs/enterprise/*.md`; README drops the formula step | 2, 4, 6, 7 |
