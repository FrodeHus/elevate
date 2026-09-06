# Elevate for Windows

The Windows 11 counterpart of the macOS menu bar app: a system-tray flyout for just-in-time
Microsoft Entra and Azure PIM role activation, built with WinUI 3 on .NET 10, installed from a
per-user MSI downloaded from the GitHub releases (a winget package is prepared for later).

UI design: [docs/design/elevate-windows.html](../docs/design/elevate-windows.html) (Fluent mockups of the
flyout, windows, tray states and tokens; open the file in a browser, it follows the OS theme).

## Install

Download `Elevate-<version>-x64.msi` (or `-arm64.msi` for Arm PCs) from the
[latest release](https://github.com/FrodeHus/elevate/releases/latest) and run it. The MSI
installs for the current user into `%LOCALAPPDATA%\Programs\Elevate` (no admin rights), adds a
Start Menu entry and can launch Elevate when it finishes. It needs the .NET 10 runtime
(`winget install Microsoft.DotNet.Runtime.10`); the Windows App SDK runtime is bundled. Windows 11
(build 22000) or newer.

Releases are not code-signed yet. Windows SmartScreen shows "Windows protected your PC" the first
time the installer runs: choose **More info**, then **Run anyway**. Check the download against the
SHA-256 in the release notes first:

```powershell
(Get-FileHash .\Elevate-<version>-x64.msi).Hash
```

Upgrade by running a newer MSI; uninstall from Settings > Apps. A winget package
(`Reothor.Elevate`) is prepared but not submitted, because winget moderation requires signed
installers; see [Release](#release).

## Use

Elevate lives in the notification area. Left-click the icon for the flyout, right-click for Open,
Settings and Quit. The tray glyph shows the number of active roles, a warning dot when one expires
within five minutes and a hollow dot while a request awaits approval. A toast fires five minutes
before a role expires with an Extend button, and again when it has expired.

Sign in from the flyout's **Add account…** with one of:

- **Your app registration** through the Windows account picker (WAM), with the system browser as the
  fallback. Supports Entra directory roles, Azure resource roles and PIM for Groups. Enter the
  application (client) ID in Settings first; the registration must list both redirect URIs shown
  there under the *Mobile and desktop applications* platform:
  `ms-appx-web://microsoft.aad.brokerplugin/{client id}` for the broker and `http://localhost` for
  the browser fallback. See [docs/entra-app-registration.md](../docs/entra-app-registration.md).
- **A custom client ID**: any public-client registration, through the system browser on `http://localhost`.
- **The Azure CLI app** or **the Azure PowerShell app**: no registration or consent needed; Azure
  resource roles only.

Everything the macOS app does is here: select several roles across pivots and activate them
together, save a selection as a **profile** (a chip row under the pivots; *Manage…* renames,
reorders and deletes; a chip opens the run window with per-entry durations, the remembered reason,
ticket and start time), **Ctrl-click** Activate, Extend or a chip to go straight through with the
last reason and duration when the policy allows it, and a **global shortcut** (Settings) that runs
one profile the same way. Requests waiting for *your* approval appear in a pinned **Approvals**
group above *Active now* with Approve and Deny; a toast announces each new request once, and the
tray icon carries an orange dot while any are pending.

Settings also holds *Start Elevate when I sign in* (a per-user Run entry), *Check for updates*
(the flyout offers a newer release with a Windows installer once a day, with Open and Dismiss) and *Copy
diagnostics* (a plain-text report of accounts, tenants, profiles, the shortcut and recent errors,
never a token or client ID).

Files live in `%LOCALAPPDATA%\Elevate`: `state.json` (accounts, tenants, manual roles, remembered
reasons; the same schema as the macOS app), `settings.json`, the DPAPI-protected MSAL token cache
`msal.cache`, and `elevate.log` for failures of the shell itself.

## Build and test

```powershell
cd windows
dotnet test Elevate.sln
```

`Elevate.Core` and its tests build on macOS, Linux and Windows with the .NET 10 SDK. The app
(`src/Elevate.App`, WinUI 3, unpackaged, self-contained Windows App SDK 1.8) and
`Elevate.App.Model` (the testable half: app model, settings, MSAL providers) need Windows 11 and
the Windows 11 SDK 10.0.22621; `dotnet build Elevate.sln` builds them without Visual Studio.

Run the app from the build output with developer switches for screenshots and smoke tests:

```powershell
dotnet build src/Elevate.App/Elevate.App.csproj
src\Elevate.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Elevate.exe --flyout
src\Elevate.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Elevate.exe --show settings
```

`--show` takes `settings`, `add-account`, `configure`, `activation`, `bulk`, `add-tenant`, `discover`,
`save-profile`, `manage-profiles`, `run-profile` or `decision`. To exercise the flyout without
signing in, seed `%LOCALAPPDATA%\Elevate\state.json` with an own-app identity, tenants in
`manualRoles` mode, manual roles and profiles (the golden fixture
`tests/Elevate.Core.Tests/Fixtures/state-macos.json` shows the shape; durations are the
128-bit attosecond pair Swift writes) and clear `clientId` in `settings.json`; back both files up first.

### Installer and winget manifest

```powershell
dotnet tool install --global wix --version 5.0.2
./installer/build.ps1 -Version 1.0.0            # x64 and arm64 MSIs in installer/out
./winget/New-Manifest.ps1 -Version 1.0.0 -X64Sha256 <sha> -Arm64Sha256 <sha>
winget validate --manifest winget/manifests/r/Reothor/Elevate/1.0.0
```

`installer/build.ps1` publishes the app framework-dependent with the Windows App SDK self-contained,
builds the per-user MSI with WiX v5 (`installer/Elevate.wxs`) and signs it with Azure Trusted Signing
when `-Sign` is given and the signing variables are set. `winget/New-Manifest.ps1` fills the templates
in `winget/templates` with the release URLs and hashes.

## Release

Releases are shared with the macOS app: tag `v<version>` on `main` and the
[release workflow](../.github/workflows/release.yml) builds both apps, publishes one GitHub
release with the DMG, both MSIs and their SHA-256 files, and attaches the generated winget
manifest as a workflow artifact. The Windows MSIs are unsigned unless the Azure Artifact Signing
secrets are configured, and the manifest is not submitted to `microsoft/winget-pkgs` until they
are. The full procedure is in [docs/releasing.md](../docs/releasing.md).
