# Deploying Elevate to Macs with Intune

**What you end up with:** Elevate installed on every enrolled Mac in the assignment, with your
organization's app registration already filled in and whichever other settings you choose locked.
Users open the app, click **Add account…** and sign in — they are never asked for a client id.

**Prerequisites**

- Microsoft Intune, with the Intune Administrator or Policy and Profile Manager role.
- An Entra app registration for Elevate and its application (client) id. If you do not have one
  yet, [docs/entra-app-registration.md](../entra-app-registration.md) creates it, with an Azure CLI
  script and a portal walkthrough. Consent is granted once per tenant.
- `Elevate-<version>.pkg` and `Elevate-enterprise-kit-<version>.zip` from the
  [latest release](https://github.com/FrodeHus/elevate/releases/latest). Unzip the kit; you want
  `macos/no.reothor.elevate.mobileconfig` or `macos/intune-preference-file.plist` from it.
- Macs running macOS 26 or newer, enrolled in Intune.

Intune deploys **signed** macOS packages only. The release notes say whether
`Elevate-<version>.pkg` was signed and notarized; if it was not, either deploy the app another way
(Jamf, or your own wrapper) or sign it yourself with a Developer ID Installer certificate:

```bash
productsign --sign "Developer ID Installer: Your Company (TEAMID)" \
  Elevate-<version>.pkg Elevate-<version>-signed.pkg
```

The keys you can push are in [keys.md](keys.md). Push only what you want to take away from users:
every key you send is locked.

The pkg also links `/usr/local/bin/elevate` to the CLI bundled inside the app on Apple Silicon
Macs, so the same policy delivers the command-line tool (Intel Macs get the app only; see
[cli.md](cli.md) for those).

## 1. Add the app

1. In the [Intune admin center](https://intune.microsoft.com): **Apps → macOS → Add**.
2. App type: **macOS app (PKG)**. Click **Select**.
3. Upload `Elevate-<version>.pkg`.
4. On **App information**, set the name (`Elevate`), publisher and a description.
5. On **Requirements**, set **Minimum operating system** to the macOS version your fleet runs
   (Elevate needs macOS 26).
6. On **Detection rules**, add the app bundle: **App bundle ID (CFBundleIdentifier)**
   `no.reothor.elevate`, with the build number or version of the release you uploaded.
7. Assign it to the group that should get Elevate, then **Create**.

## 2. Push the settings

Elevate reads *managed preferences* for the domain `no.reothor.elevate`, and only values that are
forced by a profile. Two ways to deliver them; pick one.

### Option A — custom profile from the mobileconfig

1. Open `macos/no.reothor.elevate.mobileconfig` from the kit in a text editor.
2. Replace `00000000-0000-0000-0000-000000000000` with your application (client) id, and
   `contoso.com` with your tenant's verified domain or tenant id.
3. **Delete every key you do not want to lock.** The template forces all seven. Most rollouts start
   with `ClientId` alone.
4. Replace `PayloadOrganization`, and generate fresh UUIDs for the two `PayloadUUID` values
   (`uuidgen`).
5. **Devices → macOS → Configuration → Create → New policy**, profile type **Templates**, template
   name **Custom**.
6. Give it a name. **Custom configuration profile name**: `Elevate managed settings`. **Deployment
   channel**: *Device channel*. Upload the mobileconfig as the **Configuration profile file**.
7. Assign it to the same group as the app and create it.

### Option B — preference file

Simpler to edit, same result.

1. Open `macos/intune-preference-file.plist` from the kit, fill in your values and delete the keys
   you do not want to lock.
2. **Devices → macOS → Configuration → Create → New policy**, profile type **Templates**, template
   name **Preference file**.
3. **Preference domain name**: `no.reothor.elevate`. Upload the edited plist as the **Property list
   file**.
4. Assign and create.

## 3. Verify

Intune delivers the profile at the next check-in; you can force one from Company Portal. Users
already running Elevate pick the values up at the **next launch** of the app.

On a Mac in the assignment:

```bash
# The profile arrived
sudo profiles show -type configuration | grep -A3 no.reothor.elevate

# What Elevate will read (only forced values appear here)
defaults read /Library/Managed\ Preferences/$USER/no.reothor.elevate
```

Then in the app:

- **Settings → Entra app registration**: the field shows your client id, is greyed out, and carries
  the caption **Managed by your organization**.
- With `DisableUpdateCheck` pushed, the update button is replaced by **Updates are managed by your
  organization** and no update banner appears.
- The bottom of Settings has a **Managed by your organization** section listing the keys in effect,
  the source, and any warnings in orange.
- **Settings → Copy diagnostics** produces a report with a `Managed configuration:` section naming
  the source and the key names (never the values).

On a fresh install with `ClientId` pushed, the setup panel never appears: the app is already
configured, so the panel opens on an empty account list whose **Add account…** is the next step.

`elevate --version` in Terminal prints the deployed version; `elevate config managed` reads
`/etc/elevate/managed.json`, not the configuration profile — see [cli.md](cli.md).

## Testing without waiting for Intune

To try a value on your own Mac first, write the managed preferences file directly and relaunch
Elevate:

```bash
sudo defaults write /Library/Managed\ Preferences/$USER/no.reothor.elevate \
  ClientId -string "11111111-2222-3333-4444-555555555555"
sudo killall cfprefsd
defaults read /Library/Managed\ Preferences/$USER/no.reothor.elevate
```

`killall cfprefsd` clears the preferences daemon's cache so it picks up the file you just wrote;
skip it and Elevate (and `defaults read`) may still see the old, unmanaged value. Quit and reopen
Elevate. Undo with `sudo defaults delete /Library/Managed\ Preferences/$USER/no.reothor.elevate ClientId`.
On an enrolled Mac the MDM owns that directory and will overwrite what you write there.

## Next

- Lock which sign-in methods and tenants people may use: `AllowedSignInMethods`, `AllowedTenants`
  and `PinnedTenants` in [keys.md](keys.md).
- Publish role-set profiles to everyone: [profiles.md](profiles.md).
- The `elevate` command-line tool on the same Macs reads `/etc/elevate/managed.json`, not the
  configuration profile: [cli.md](cli.md).
- Something missing or greyed out unexpectedly: [troubleshooting.md](troubleshooting.md).
- To remove Elevate, see [Uninstalling](README.md#uninstalling).
