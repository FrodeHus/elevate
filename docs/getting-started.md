# Getting started with Elevate

Elevate lets you activate the Microsoft Entra roles, Azure resource roles and PIM for Groups
memberships you are eligible for, across every account and tenant you use. It runs in the macOS
menu bar, in the Windows system tray, and as the `elevate` command in a terminal on Linux, macOS
and Windows. This guide takes you from a fresh install to a panel full of roles.

> The pictures in this guide are real renders of the macOS app with sample data from a fictional
> organization. Elevate for Windows has the same controls in the same places, drawn in the
> Windows style; where it differs, the guide says so.

Other guides: [Activating roles](activating-roles.md), [Profiles and shortcuts](profiles-and-shortcuts.md),
[Approving requests](approvals.md), [Requesting access packages](access-packages.md),
[Troubleshooting](troubleshooting.md), [The shared Elevate app](shared-app-registration.md).

## 1. Install

Pick your platform:

- **macOS 26 (Tahoe).** Install with Homebrew or download the DMG; both are described in
  [macos/README.md](../macos/README.md#install). The Homebrew cask installs the installer package,
  which also puts the `elevate` command on your PATH on Apple Silicon. Releases are signed and
  notarized, so the app opens without Gatekeeper prompts. After the first launch, its icon (a
  double chevron) sits in the menu bar. Click it to open the panel.
- **Windows 11.** Download the per-user MSI from the
  [latest release](https://github.com/FrodeHus/elevate/releases/latest) and run it; the steps,
  including the .NET runtime it needs, are in [windows/README.md](../windows/README.md#install).
  The MSI installs the app and the `elevate` command for the current user, no admin rights needed.
  Releases are not code-signed yet, so SmartScreen asks once: choose **More info**, then **Run
  anyway**, after checking the file against the SHA-256 in the release notes. Elevate then lives
  in the notification area: left-click the icon for the flyout, right-click for Open, Settings and
  Quit.
- **The CLI on Linux, macOS or Windows.** Homebrew, a single binary from the release, or the
  copy bundled with the macOS package and the Windows MSI; see
  [cli/README.md](../cli/README.md#install). The rest of this guide describes the apps; the CLI
  equivalent of each step is in [cli/README.md](../cli/README.md#sign-in).

## 2. Choose how to sign in

Elevate signs in with an Entra app registration. On first launch the panel asks you to complete
setup, and links to this guide and to the app registration guide on GitHub. On macOS the setup
panel offers three buttons: **Open Settings…**, **Quick start with the shared Elevate app…** and
**Continue with the Azure CLI app**. On Windows it offers **Open Settings…** and **Continue with
the Azure CLI app**; the shared app is used there by pasting its ID into Settings.

![Setup panel offering Open Settings, Quick start with the shared Elevate app and Continue with the Azure CLI app](images/tutorials/panel-setup.png)

You have three routes:

- **Your organization's Elevate registration (recommended for real use).** Someone in your
  organization creates one app registration once, following
  [entra-app-registration.md](entra-app-registration.md), and gives you its application
  (client) ID. The guide covers both the portal and the Azure CLI route, with a
  [script](entra-app/create-app-registration.sh) and a
  [permissions manifest](entra-app/required-resource-access.json) for `az ad app create`.
  Click **Open Settings…**, paste the ID into the app registration field and press Return. This
  route supports everything: Entra roles, Azure roles, groups and access packages. If your organization rolls Elevate out with Intune, Jamf or Group Policy, the ID may
  already be filled in and marked "Managed by your organization"; skip to step 3.
- **The shared Elevate app (quickest).** The Elevate project publishes a multi-tenant
  registration you can use without creating one. On macOS click **Quick start with the shared
  Elevate app…** and confirm; on Windows paste `c9011cc5-7422-4630-a432-73ff4df5834e` into
  Settings. Then have an administrator use **Grant admin consent…** (or the consent link in
  [shared-app-registration.md](shared-app-registration.md)) once per tenant. It covers
  everything, but it is **optional and has no SLA**: it may change or be withdrawn, and anyone who
  needs control over the registration should register their own. Read
  [shared-app-registration.md](shared-app-registration.md) before you consent.
- **The Azure CLI app.** Needs no registration and no consent, but covers Azure resource roles
  only. Click **Continue with the Azure CLI app** if that is all you need, or if you cannot get a
  registration yet. You can always add a registration later.

Whichever registration you use, an administrator grants its delegated permissions once per
tenant, either with `az ad app permission admin-consent --id <client id>` or through the consent
link Elevate offers from a tenant's menu until consent has happened. The CLI command does not
apply to the shared Elevate app, which is registered in another tenant; use the link. Details
are in [entra-app-registration.md](entra-app-registration.md).

## 3. Add an account

Click **Add account…** at the bottom of the panel. Pick the sign-in method for this account:

![The Add account dialog with the four sign-in methods and a note about what each supports](images/tutorials/add-account.png)

- **Entra app registration** (on Windows: **Your app registration**) uses the client ID from
  Settings — your own, your company's, or the shared Elevate app. Each tenant needs an
  administrator to consent once; the panel offers a consent link when that has not happened yet.
  On macOS the caption under the option describes how this build signs in: through Microsoft's
  sign-in window on a signed build, through your browser on an unsigned one. On Windows the
  Windows account picker opens, with the browser as fallback.
- **Azure CLI app** and **Azure PowerShell app** are Microsoft's own apps. No consent, Azure
  resource roles only. Use the PowerShell one when your tenant blocks the Azure CLI.
- **Company app (client ID)** (on Windows: **Custom client ID**) is for two cases: an existing
  public-client registration that lists only `http://localhost`, such as a company-wide PIM app,
  or a second registration you want to use alongside the one in Settings. Type its ID; Elevate
  reads what it was granted after sign-in.

Click **Continue**. Microsoft sign-in opens and returns you to Elevate. The account appears in
the panel with its home tenant and, after a moment, the roles you are eligible for. The same
account cannot be added twice with different methods; sign it out first.

## 4. Reach other tenants

An account often has access to more than its home tenant, as a guest or through a
partner setup. Open the account menu (the circled dots on the account row) and choose:

- **Discover tenants…** lists every tenant Microsoft says the account can reach; tick the ones
  you want and click **Track selected**.
- **Add tenant…** takes a tenant ID or domain name directly.

![The Add tenant dialog asking for a tenant ID or domain](images/tutorials/add-tenant.png)

Each tracked tenant gets its own header under the account, with a **home** tag on the account's
own tenant.

## 5. Find your way around the panel

![The panel with profiles, an approval, active roles, and the Contoso tenant's roles](images/tutorials/panel-entra.png)

From the top:

- **Header buttons**: search (filters roles, tenants and accounts as you type), select roles
  (for activating several at once), and refresh.
- **Entra, Azure, Groups** tabs switch between directory roles, Azure resource roles and PIM for
  Groups. Elevate remembers the tab you left it on.
- **Profiles** are saved selections you run with one click. See
  [Profiles and shortcuts](profiles-and-shortcuts.md).
- **Approvals** appears only when someone is waiting for your decision. See
  [Approving requests](approvals.md).
- **Active now** lists everything currently active or pending across all accounts, with a
  countdown.
- **Accounts and tenants** follow, each with its eligible roles. Every account and tenant row has
  a menu with actions such as sign in again, discover tenants, configure known roles, request
  access packages, and remove. The tenant menu's **Open…** submenu opens the Azure Portal, the
  Entra and Intune admin centers, and the Defender and Purview portals in that tenant; the
  browser decides which account, so a sign-in prompt may appear with the tenant already chosen.
- **Add account…**, **Settings…** and **Quit** are at the bottom.

Click a tenant name to collapse or expand it. The pills next to a tenant name tell you about
limits, for example **manual roles** or **Azure roles only**; hover for the reason. A small box
icon next to a tenant means it offers access packages; see
[Requesting access packages](access-packages.md).

## 6. Settings worth knowing

Open **Settings…** from the panel (on Windows, also from the tray icon's right-click menu).

![Settings with General, Entra app registration and Global shortcut sections](images/tutorials/settings.png)

- **Launch at login** (on Windows: **Start Elevate when I sign in**) keeps Elevate in the menu
  bar or tray after a restart. macOS may ask you to approve it under System Settings, and Elevate
  says so under the toggle.
- **Check for updates** looks for a newer release. Elevate also checks once a day on its own and
  shows a banner in the panel when one is available.
- **Copy diagnostics** puts a plain-text report on the clipboard for bug reports: accounts,
  tenants, profiles and recent errors, never tokens or client IDs.
- **Entra app registration** (on Windows: **App registration**) is where the client ID lives.
  Changing it signs out the accounts that use it, so Elevate asks before applying. On macOS it
  also has **Quick start with the shared Elevate app…**, and shows **Shared Elevate app — no
  SLA** with a **Grant admin consent…** button while the shared ID is in effect; see
  [shared-app-registration.md](shared-app-registration.md). On Windows it lists the two redirect
  URIs your registration must carry.
- **Global shortcut** runs a profile from anywhere; see
  [Profiles and shortcuts](profiles-and-shortcuts.md).

Settings that your organization manages are locked with a "Managed by your organization"
caption; see [enterprise/README.md](enterprise/README.md).

## What the menu bar or tray icon tells you

The icon shows a number when roles are active. On macOS an exclamation badge means one of them
expires within a few minutes, a clock means one is awaiting approval, and a person-with-clock
means someone is waiting for your approval. On Windows the same states are a warning dot, a
hollow dot, and an orange dot. Both apps notify you five minutes before a role expires, with an
option to extend it, and again when it has expired.

## Next steps

- [Activating roles](activating-roles.md): activate, schedule, extend and deactivate.
- [Profiles and shortcuts](profiles-and-shortcuts.md): save a role set and run it with one click.
- [Troubleshooting](troubleshooting.md): consent, manual roles, sign-in banners and refused
  activations.
