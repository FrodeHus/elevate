# Deploying Elevate to Windows PCs with Intune

**What you end up with:** Elevate installed on every assigned PC, with your organization's app
registration already filled in and whichever other settings you choose locked. Users open the tray
app, click **Add account…** and sign in — they are never asked for a client id.

**Prerequisites**

- Microsoft Intune, with the Intune Administrator or Policy and Profile Manager role.
- An Entra app registration for Elevate and its application (client) id. If you do not have one
  yet, [docs/entra-app-registration.md](../entra-app-registration.md) creates it, with an Azure CLI
  script and a portal walkthrough. Consent is granted once per tenant.
- `Elevate-<version>-x64.msi` (or `-arm64.msi`) and `Elevate-enterprise-kit-<version>.zip` from the
  [latest release](https://github.com/FrodeHus/elevate/releases/latest). Unzip the kit; you want
  `windows/Elevate.admx` and `windows/en-US/Elevate.adml`.
- The [Microsoft Win32 Content Prep Tool](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool)
  to wrap the MSI as an `.intunewin` package.
- Windows 11 (build 22000) or newer, with the .NET 10 runtime — deploy
  `Microsoft.DotNet.Runtime.10` first, or make it a dependency of the app below.

The MSI installs per user into `%LOCALAPPDATA%\Programs\Elevate` and needs no administrator
rights, so it is deployed as a Win32 app in the **user** install context. Releases are not
code-signed yet; see [windows/README.md](../../windows/README.md#install).

The keys you can push are in [keys.md](keys.md). Push only what you want to take away from users:
every key you send is locked.

## 1. Package and add the app

1. Put the MSI in an empty folder and wrap it:

   ```powershell
   .\IntuneWinAppUtil.exe -c C:\pkg\elevate -s Elevate-<version>-x64.msi -o C:\pkg\out
   ```

2. In the [Intune admin center](https://intune.microsoft.com): **Apps → Windows → Add**, app type
   **Windows app (Win32)**, and upload the `.intunewin` file.
3. **Program**:
   - Install command: `msiexec /i "Elevate-<version>-x64.msi" /qn`
   - Uninstall command: `msiexec /x "Elevate-<version>-x64.msi" /qn`
   - **Install behavior: User** (the MSI is a per-user install).
4. **Requirements**: 64-bit, Windows 11 22H2 or later.
5. **Detection rules**: rule type **File**,
   path `%LOCALAPPDATA%\Programs\Elevate`, file `Elevate.exe`, detection method **File or folder
   exists** — with **Associated with a 32-bit app on 64-bit clients** left off. (A version-based
   rule on the same file works too, and makes upgrades explicit.)
6. Assign it to a **user** group, not a device group, and create the app.

## 2. Import the administrative template

Elevate's settings live under `Software\Policies\Reothor\Elevate` in the registry. The ADMX in the
kit writes them, and Intune can ingest it.

1. **Devices → Configuration → Import ADMX → Import**.
2. Upload `windows/Elevate.admx` as the ADMX file and `windows/en-US/Elevate.adml` as the ADML file
   (language `en-US`). Vendor and product names come from the file: Reothor, Elevate.
3. Wait for the status to become **Available**. It takes a few minutes.

## 3. Set the policies

1. **Devices → Configuration → Create → New policy**. Platform **Windows 10 and later**, profile
   type **Templates → Imported Administrative templates (Preview)** (or the **Settings catalog**,
   where the imported settings appear under the category **Reothor \ Elevate**).
2. Under **Computer Configuration → Reothor → Elevate** (the same policies also exist under **User
   Configuration**; where both are set, the machine value wins), enable what you need:
   - **Application (client) id** — set it to your registration's GUID. This is the one most fleets
     push.
   - **Disable the update check** — enable it when Intune keeps Elevate up to date.
   - **Allowed sign-in methods**, **Allowed tenants**, **Pinned tenants** — list settings; add one
     entry per row.
   - **Managed profiles (inline)** and **Managed profiles URL** — see [profiles.md](profiles.md).
   Leave everything else **Not configured**: that leaves the choice to the user.
3. Assign the policy to the same group as the app and create it.

## 4. Verify

Intune applies the policy at the next check-in; you can force a sync from **Settings → Accounts →
Access work or school → Info → Sync** on the PC. Elevate reads the values at its **next launch**.

In a command prompt on the PC:

```
reg query "HKLM\SOFTWARE\Policies\Reothor\Elevate" /s
reg query "HKCU\SOFTWARE\Policies\Reothor\Elevate" /s
```

List settings appear as a subkey of the same name holding values named `1`, `2`, …

Then in the app:

- **Settings → Entra app registration**: the field shows your client id, is disabled, and carries
  the caption **Managed by your organization**.
- With the update check disabled, the button is replaced by **Updates are managed by your
  organization**.
- A **Managed by your organization** group at the bottom of Settings lists the keys in effect, the
  source, and any warnings.
- **Copy diagnostics** produces a report with a `Managed configuration:` section naming the source
  (`Windows policy`) and the key names — never the values.

If the CLI is on the same PCs, it reads the same registry keys:

```
elevate config
elevate config managed
```

`Source` reads `managed` for every value your policy supplies.

## Next

- Group Policy instead of, or alongside, Intune: [windows-group-policy.md](windows-group-policy.md).
- Publish role-set profiles to everyone: [profiles.md](profiles.md).
- Something missing or greyed out unexpectedly: [troubleshooting.md](troubleshooting.md).
