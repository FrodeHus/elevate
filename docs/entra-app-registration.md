# Setting up the Entra app registration

This guide is for anyone setting up Elevate's own app registration, even if you have never
opened the Microsoft Entra admin center before. You do not need to do this to use Elevate — the
app also signs in with the Azure CLI or Azure PowerShell app, which need no registration and no
consent — but your own registration is required if those first-party apps are blocked in your
tenant, or if you want a registration you control.

## 1. What the registration is for

Elevate needs one app registration: a single, multi-tenant, public-client application. "Public
client" means no client secret is ever created, stored, or used — sign-in works with just the
application (client) ID.

Elevate signs in with this registration through MSAL:

- On macOS, MSAL uses the redirect `msauth.<bundle id>://auth` — for the standard build, bundle
  id `no.reothor.elevate`, so the redirect is `msauth.no.reothor.elevate://auth`.
- On Windows, and for the custom-app sign-in method on macOS, sign-in instead uses the
  `http://localhost` loopback redirect.

You only need to create this registration once per organization; every tenant that wants to use
it then grants it admin consent (see step 2 or 3).

## 2. Option A: Azure CLI (recommended)

This is the fastest way to create the registration, and it is scripted so you do not have to
click through the portal.

**Prerequisites:**

- The Azure CLI (`az`), version 2.60 or newer.
- Signed in with `az login`, as a user who can create app registrations — the built-in
  Application Developer role or higher.

**Steps:**

1. Clone this repository and change into the script's directory:

   ```bash
   git clone https://github.com/<org>/pimtray.git
   cd pimtray/docs/entra-app
   ```

2. Run the script:

   ```bash
   ./create-app-registration.sh
   ```

   By default this creates a registration named "Elevate" with the redirect for bundle id
   `no.reothor.elevate`. Pass a different display name and/or bundle id as arguments if you are
   registering a differently-named or rebranded build — for example
   `./create-app-registration.sh "Elevate (Contoso)" no.reothor.elevate`.

3. The script prints the application (client) ID, for example:

   ```
   Application (client) ID: 11111111-2222-3333-4444-555555555555
   ```

   Copy this value — you will paste it into Elevate's Settings, and use it again for consent.

**Granting admin consent:**

The registration exists now, but no tenant can use it until an administrator in that tenant
grants admin consent for the permissions in step 4 below. For the home tenant (the tenant you
created the app in), you can do this from the CLI, as a Privileged Role Administrator or Global
Administrator:

```bash
az ad app permission admin-consent --id <client id>
```

For every other tenant that will use Elevate, an administrator in that tenant needs to open the
admin consent URL, substituting that tenant's ID and your client ID:

```
https://login.microsoftonline.com/<tenant id>/adminconsent?client_id=<client id>
```

You do not have to build this URL by hand for every tenant: once a client ID is configured in
Elevate, open the "…" menu next to any signed-in tenant and choose "Open admin consent link…" to
get the same link pre-filled for that tenant.

## 3. Option B: Entra admin center, step by step

Use this if you would rather click through the portal, or cannot run the Azure CLI.

1. Go to the [Microsoft Entra admin center](https://entra.microsoft.com) → **App registrations**
   → **New registration**.
2. Give it a name (for example "Elevate").
3. Under "Supported account types", choose **Accounts in any organizational directory (Any
   Microsoft Entra ID tenant – Multitenant)**.
4. Leave "Redirect URI" blank here — you add the redirects in the next step.
5. Select **Register**.
6. Open **Authentication** on the new registration, then **Add a platform**:
   - Choose **iOS/macOS**, enter the bundle ID `no.reothor.elevate`, and select **Configure**.
     The portal derives the redirect URI `msauth.no.reothor.elevate://auth` for you.
   - Select **Add a platform** again, choose **Mobile and desktop applications**, and add the
     custom redirect URI `http://localhost`.
   - Select **Add a platform** again, choose **Web**, and add the redirect URI
     `https://login.microsoftonline.com/common/oauth2/nativeclient`. This one is not used for
     sign-in; it only exists so the admin consent link (step 2 above) opens a valid page instead
     of an error.
7. Open **API permissions** → **Add a permission**:
   - Choose **Microsoft Graph** → **Delegated permissions**, and add the seven scopes listed in
     the permission table below.
   - Select **Add a permission** again, choose **Azure Service Management** → **Delegated
     permissions**, and add `user_impersonation`.
8. Select **Grant admin consent for \<your tenant\>** to consent for your own tenant. Every other
   tenant that will use Elevate repeats this consent step — either the same way, or by opening
   the admin consent link described in step 2 above.

A note on public client flows: you do **not** need to turn on "Allow public client flows" under
Authentication. The iOS/macOS platform and the "Mobile and desktop applications" platform already
mark their redirect URIs as public-client. Only turn this setting on if sign-in fails with error
AADSTS7000218.

## 4. Permission table

All permissions are delegated (the signed-in user's own access, not app-only). "Admin consent"
means a tenant administrator must grant it before anyone in that tenant can sign in with this
app; a user cannot consent to it themselves.

| Permission | Resource | What Elevate uses it for | Admin consent |
|---|---|---|---|
| `User.Read` | Microsoft Graph | Reads the signed-in user's basic profile after sign-in. | Yes |
| `RoleEligibilitySchedule.Read.Directory` | Microsoft Graph | Lists the Entra directory roles the user is eligible to activate. | Yes |
| `RoleAssignmentSchedule.ReadWrite.Directory` | Microsoft Graph | Activates and deactivates Entra directory roles. | Yes |
| `RoleManagementPolicy.Read.Directory` | Microsoft Graph | Reads each directory role's policy (maximum duration, whether a reason or MFA is required). | Yes |
| `PrivilegedEligibilitySchedule.Read.AzureADGroup` | Microsoft Graph | Lists PIM-for-groups eligibility, for the Groups tab. | Yes |
| `PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup` | Microsoft Graph | Activates and deactivates PIM-for-groups membership/ownership. | Yes |
| `RoleManagementPolicy.Read.AzureADGroup` | Microsoft Graph | Reads each group's PIM policy. | Yes |
| `user_impersonation` | Azure Service Management | Discovers tenants/subscriptions and reads, activates, and deactivates Azure resource roles. | User-consentable |

## 5. Verify

Confirm the registration looks right:

```bash
az ad app show --id <client id> --query "{name:displayName,audience:signInAudience,public:publicClient.redirectUris}"
```

You should see `audience` as `AzureADMultipleOrgs`, and both `msauth.no.reothor.elevate://auth`
and `http://localhost` listed as public-client redirect URIs.

Then, in Elevate, open Settings and paste the client id, and add an account to confirm sign-in
works end to end.

## 6. Troubleshooting

- **AADSTS7000218** ("client assertion or client secret required"): the redirect URI is
  registered under the Web platform instead of iOS/macOS or "Mobile and desktop applications".
  Move it to the correct platform (steps above), or, as a fallback, turn on "Allow public client
  flows".
- **AADSTS65001** ("The user or administrator has not consented") or a message about admin
  consent: consent has not been granted in that tenant. Have an administrator use the admin
  consent link from the tenant menu, or run the CLI consent command from step 2.
- **AADSTS50011** ("The redirect URI ... does not match"): the bundle id in the registration does
  not match the bundle id of the build you are running. Re-check the iOS/macOS platform's bundle
  ID against your build, or pass the build's bundle id to the script.
- **"PIM for Groups is not permitted" / the Groups tab is empty**: the three
  `*.AzureADGroup` scopes have not been consented in that tenant. Re-run consent after confirming
  they are listed under API permissions.
