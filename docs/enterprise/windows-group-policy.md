# Elevate settings through Group Policy

**What you end up with:** every domain-joined PC in the scope of a GPO reads your organization's
app registration and whichever other settings you lock, without anyone touching a machine. Users
open the tray app, click **Add account…** and sign in.

**Prerequisites**

- Active Directory, and rights to edit Group Policy — plus write access to the central store if you
  use one.
- An Entra app registration for Elevate and its application (client) id
  ([docs/entra-app-registration.md](../entra-app-registration.md) if you need to create one).
- `Elevate-enterprise-kit-<version>.zip` from the
  [latest release](https://github.com/FrodeHus/elevate/releases/latest), unzipped. You want
  `windows/Elevate.admx` and `windows/en-US/Elevate.adml`.
- Elevate installed on the PCs. The per-user MSI is described in
  [windows/README.md](../../windows/README.md#install); deploy it however you deploy other per-user
  software, or with Intune ([windows-intune.md](windows-intune.md)).

The keys, their types and their registry shapes are in [keys.md](keys.md). Push only what you want
to take away from users: every policy you enable is locked, and one you leave **Not configured**
leaves the choice to the user.

## 1. Put the template in the central store

Copy the two files into the domain's PolicyDefinitions folder, keeping the language subfolder:

```
\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions\Elevate.admx
\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions\en-US\Elevate.adml
```

From a machine with the Group Policy tools:

```powershell
$store = "\\contoso.com\SYSVOL\contoso.com\Policies\PolicyDefinitions"
Copy-Item .\windows\Elevate.admx        $store
Copy-Item .\windows\en-US\Elevate.adml  "$store\en-US"
```

If your domain has no central store, copy the same two files to
`C:\Windows\PolicyDefinitions` (and `C:\Windows\PolicyDefinitions\en-US`) on the machine you edit
GPOs from instead.

## 2. Set the policies

1. Open **Group Policy Management**, then **Edit** the GPO you want (or create one and link it to
   the OU holding the PCs or the users).
2. Go to **Computer Configuration → Policies → Administrative Templates → Reothor → Elevate**.
   The same policies exist under **User Configuration** — they write `HKCU` instead of `HKLM`, and
   where a key is set in both, the machine value wins.
3. Enable what you need:

   | Policy | What it does |
   |---|---|
   | Application (client) id | Fills in your registration's GUID; users never enter one. |
   | Disable the update check | Stops the daily GitHub check and the update UI, for fleets you update yourself. |
   | Allowed sign-in methods | Limits Add account to the methods you list (`ownApp`, `azureCLI`, `azurePowerShell`, `custom`). |
   | Allowed tenants | Limits which tenants may be added or discovered. An account's own home tenant is always allowed. |
   | Pinned tenants | Tenants tracked automatically for every account that can reach them; users cannot remove them. |
   | Managed profiles (inline) | A profile set published to every machine, see [profiles.md](profiles.md). |
   | Managed profiles URL | The same set served over https and refreshed daily. |

   The list policies (allowed methods, allowed and pinned tenants) take one entry per row; the
   inline profile document is a multi-line text box holding the JSON.
4. Close the editor. Every policy's **explain** text repeats the accepted values, so you do not need
   this page open while you edit.

## 3. Apply and verify

On a PC in scope:

```
gpupdate /target:computer /force
reg query "HKLM\SOFTWARE\Policies\Reothor\Elevate" /s
```

You should see `ClientId` as `REG_SZ`, `DisableUpdateCheck` as `REG_DWORD` `0x1`, and each list as
a subkey of the same name holding values named `1`, `2`, …:

```
HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Reothor\Elevate
    ClientId    REG_SZ    11111111-2222-3333-4444-555555555555
    DisableUpdateCheck    REG_DWORD    0x1

HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Reothor\Elevate\AllowedTenants
    1    REG_SZ    contoso.com
    2    REG_SZ    fabrikam.com
```

For a user-configuration GPO, `gpupdate /target:user /force` and `reg query
"HKCU\SOFTWARE\Policies\Reothor\Elevate" /s`.

Elevate reads the policy at its **next launch**: quit the tray app and start it again. Then:

- **Settings → Entra app registration** shows your client id, disabled, captioned **Managed by your
  organization**.
- The update button reads **Updates are managed by your organization** when you disabled the check.
- The **Managed by your organization** group at the bottom of Settings lists the keys in effect and
  any warnings.
- **Copy diagnostics** includes a `Managed configuration:` section naming the source
  (`Windows policy`) and the key names — never the values.
- The CLI on the same PC reads the same keys: `elevate config` marks each value's `Source` as
  `managed`, and `elevate config managed` prints the origin, the keys and the warnings.

## Without a GPO: a .reg file

For a script-driven rollout, a lab or a quick test, `enterprise/example/example.reg` in the kit
writes the same HKLM values. Import it from an elevated prompt:

```
reg import example.reg
```

The inline profile document is a `REG_MULTI_SZ`, which a `.reg` file has to spell as `hex(7)`
bytes; the example carries the encoded form. To set it from a script instead:

```
reg add "HKLM\SOFTWARE\Policies\Reothor\Elevate" /v ManagedProfiles ^
  /t REG_MULTI_SZ /d "{\"version\":1,\"profiles\":[]}" /f
```

Remove a test policy with `reg delete "HKLM\SOFTWARE\Policies\Reothor\Elevate" /f`.

## Next

- Publish role-set profiles to everyone: [profiles.md](profiles.md).
- Intune instead of, or alongside, Group Policy: [windows-intune.md](windows-intune.md).
- Something missing or greyed out unexpectedly: [troubleshooting.md](troubleshooting.md).
