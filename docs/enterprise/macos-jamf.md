# Deploying Elevate with Jamf Pro

**What you end up with:** Elevate installed on every Mac in scope, with your organization's app
registration already filled in, and whichever other settings you choose locked. Users open the app,
click **Add account…** and sign in — they are never asked for a client id.

**Prerequisites**

- Jamf Pro, with rights to create packages, policies and configuration profiles.
- An Entra app registration for Elevate and its application (client) id. If you do not have one
  yet, [docs/entra-app-registration.md](../entra-app-registration.md) creates it, with an Azure CLI
  script and a portal walkthrough. Consent is granted once per tenant.
- `Elevate-<version>.pkg` and `Elevate-enterprise-kit-<version>.zip` from the
  [latest release](https://github.com/FrodeHus/elevate/releases/latest). Unzip the kit; you want
  `macos/no.reothor.elevate.mobileconfig` or `macos/jamf-manifest.json` from it.
- Macs running macOS 26 or newer.

The keys you can push, with their types and allowed values, are in [keys.md](keys.md). Push only
what you want to take away from users: every key you send is locked.

## 1. Upload the app

1. **Settings → Computer Management → Packages → New**.
2. Upload `Elevate-<version>.pkg`, give it a display name (`Elevate <version>`) and save.
3. **Computers → Policies → New**. Name it "Install Elevate".
4. Under **Packages**, add the package with action **Install**.
5. Under **Triggers**, tick **Recurring Check-in** (and **Enrollment Complete** for new Macs);
   **Execution Frequency: Once per computer**.
6. Under **Scope**, choose the smart group or machines that should get Elevate. Save.

The pkg installs into `/Applications/Elevate.app` and carries no company values — all of those come
from the configuration profile in the next step. Check the release notes for whether the pkg was
signed and notarized; an unsigned pkg still has to satisfy Gatekeeper on the Mac.

## 2. Build the configuration profile

Elevate reads *managed preferences* for the domain `no.reothor.elevate`, and only values that are
forced. Two ways to produce them; pick one.

### Option A — upload the mobileconfig (fastest)

1. Open `macos/no.reothor.elevate.mobileconfig` from the kit in a text editor.
2. Replace `00000000-0000-0000-0000-000000000000` with your application (client) id, and
   `contoso.com` with your tenant's verified domain or tenant id.
3. **Delete every key you do not want to lock.** The template forces all seven; a key you leave in
   takes the choice away from your users. Most rollouts start with `ClientId` alone.
4. Replace `PayloadOrganization`, and generate fresh UUIDs for the two `PayloadUUID` values:

   ```bash
   uuidgen
   ```

5. In Jamf Pro: **Computers → Configuration Profiles → Upload**, choose the file, then set the
   profile's **Level** to *Computer Level* and its category and description on the General pane.

### Option B — fill in Jamf's form with the custom schema

1. **Computers → Configuration Profiles → New**.
2. In the payload list choose **Application & Custom Settings → External Applications**.
3. **Source: Custom Schema**. **Preference Domain**: `no.reothor.elevate`.
4. Click **Add schema → Upload** and pick `macos/jamf-manifest.json` from the kit.
5. Jamf now renders a form with one field per key, each with a description and a link to the key
   reference. Fill in only the settings you want to lock and leave the rest empty — the schema is
   set to drop empty properties, so an untouched field is not sent.

## 3. Scope and deploy

1. On the **Scope** tab, target the same smart group as the install policy.
2. Save. The profile installs at the next check-in, or immediately for machines you push it to.
3. Users already running Elevate see the values at the **next launch** of the app; ask them to quit
   and reopen it if you do not want to wait.

## 4. Verify

On a Mac in scope:

```bash
# The profile arrived
sudo profiles show -type configuration | grep -A3 no.reothor.elevate

# What Elevate will read (only forced values appear here)
defaults read /Library/Managed\ Preferences/$USER/no.reothor.elevate
```

Then in the app:

- **Settings → Entra app registration**: the field shows your client id, is greyed out, and carries
  the caption **Managed by your organization**.
- If you pushed `DisableUpdateCheck`, the update button is replaced by **Updates are managed by
  your organization** and no update banner appears.
- The bottom of Settings has a **Managed by your organization** section listing the keys in effect,
  the source, and any warnings in orange.
- **Settings → Copy diagnostics** puts a report on the clipboard with a `Managed configuration:`
  section naming the source and the key names (never the values).

On a fresh install with `ClientId` pushed, the setup panel never appears: Elevate is already
configured, so the panel opens on an empty account list whose **Add account…** is the next step.

## Testing without waiting for Jamf

To try a value on your own Mac before you build the profile, write the managed preferences file
directly and relaunch Elevate:

```bash
sudo defaults write /Library/Managed\ Preferences/$USER/no.reothor.elevate \
  ClientId -string "11111111-2222-3333-4444-555555555555"
sudo killall cfprefsd
defaults read /Library/Managed\ Preferences/$USER/no.reothor.elevate
```

`killall cfprefsd` clears the preferences daemon's cache so it picks up the file you just wrote;
skip it and Elevate (and `defaults read`) may still see the old, unmanaged value. Quit and reopen
Elevate; the client-id field is now locked. Undo it with:

```bash
sudo defaults delete /Library/Managed\ Preferences/$USER/no.reothor.elevate ClientId
```

This is a local test only. On a managed Mac the MDM owns that directory and will overwrite what you
write there.

## Next

- Lock which sign-in methods and tenants people may use: the `AllowedSignInMethods`,
  `AllowedTenants` and `PinnedTenants` keys in [keys.md](keys.md).
- Publish role-set profiles to everyone: [profiles.md](profiles.md).
- The `elevate` command-line tool on the same Macs reads `/etc/elevate/managed.json`, not the
  configuration profile: [cli.md](cli.md), which includes a Jamf script that writes it.
- Something missing or greyed out unexpectedly: [troubleshooting.md](troubleshooting.md).
