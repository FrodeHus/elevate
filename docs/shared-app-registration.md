# The shared Elevate app registration

The Elevate project publishes one multi-tenant app registration that any organization may
consent to, so you can try Elevate — or run it in a small tenant — without registering an app
of your own first.

**It is completely optional, and it is not the recommended production choice.** It is a quick
start and a fast way to test Elevate. It comes with no SLA: no support commitment, and it may
change or be withdrawn at any time. If you need control over the registration itself, register
your own in your own tenant with
[Setting up the Entra app registration](entra-app-registration.md) and type the id into
Settings, or push it with the managed [`ClientId`](enterprise/keys.md#clientid) key. Everything
Elevate can do works exactly the same either way; only who owns the registration differs.

Related: [Getting started](getting-started.md), [Troubleshooting](troubleshooting.md),
[Managed configuration keys](enterprise/keys.md).

## 1. What it is

| Fact | Value |
|---|---|
| Application (client) id | `c9011cc5-7422-4630-a432-73ff4df5834e` |
| Display name | Elevate |
| Publisher domain | `reothor.no` |
| Sign-in audience | Multi-tenant, organizational accounts only (no personal Microsoft accounts) |
| Verified publisher | None |
| Client secret or certificate | None — it is a public client |
| Application (app-only) permissions | None |
| Delegated permissions | Exactly the ones in [the permission table](entra-app-registration.md#4-permission-table), plus `openid`, `profile` and `offline_access` — requested by MSAL at sign-in, not part of the consent link below |
| Owner | The Elevate maintainer's own tenant |

Its redirect URIs are:

| Redirect URI | Used by |
|---|---|
| `msauth.no.reothor.elevate://auth` | The signed macOS app, through MSAL |
| `http://localhost` | The browser (loopback) flow on macOS, Windows and the CLI |
| `ms-appx-web://microsoft.aad.brokerplugin/c9011cc5-7422-4630-a432-73ff4df5834e` | The Windows account picker (the WAM broker) |
| `https://elevate.reothor.no/consent.html` | The admin consent link only — the page that tells an administrator what just happened |

## 2. The security model

Elevate is a public client. There is no credential to leak and no background service:

- **Delegated permissions only.** Every call Elevate makes is made as the signed-in user, with
  that user's own access. The registration has no application (app-only) permission, so it can
  do nothing on its own.
- **No secret, no certificate.** Nothing about the registration can be stolen and replayed. A
  token is only ever issued after a real interactive sign-in by a real user in your tenant.
- **Tokens stay on the device.** Access and refresh tokens go to the platform's protected
  store — the macOS keychain, a DPAPI-protected cache on Windows, the keyring or platform store
  for the CLI. Nothing is sent to the maintainer, and Elevate has no backend of any kind.
- **Your tenant stays in control of sign-in.** Conditional Access, MFA requirements, sign-in
  risk policies and PIM's own policies all apply as usual, and every activation is written to
  your tenant's audit log with the user who made it.

### What the maintainer can and cannot do

The maintainer owns the registration, not your data. With it, the maintainer **cannot**:

- sign in as anyone in your tenant, or obtain a token without that user's own interactive
  session;
- read, list or change anything in your directory;
- act in the background, on a schedule, or after a user closes the app.

What the maintainer **can** do is change the registration itself — its permissions, redirect
URIs and whether it exists at all. That is what the risks below are about.

## 3. Known risks of a shared multi-tenant registration

Read these before consenting. They are inherent to using someone else's registration; none of
them is specific to Elevate.

1. **The owner account is the crown jewel.** If the account that owns the registration were
   compromised, an attacker could add a redirect URI they control and phish users in every
   tenant that has already consented — and because consent is already granted, those users
   would see no consent prompt. There is no secret to steal, but tenants that consent inherit
   the security of the maintainer's account. Your own registration inherits your tenant's
   security instead.
2. **Scopes and redirect URIs can change.** The maintainer can add permissions or redirects at
   any time. Adding a **new scope** requires fresh admin consent in each tenant, so you would
   see and have to approve it. Changing a **redirect URI** does not — nothing prompts you.
3. **Deletion or disablement breaks sign-in everywhere.** If the registration is deleted or
   disabled, or Microsoft disables it, sign-in stops working for every tenant using it, with no
   notice and no migration path other than switching to another client id.
4. **No verified publisher.** An individual maintainer cannot enrol as a verified publisher, so
   the registration will never be marked verified. Tenant policies that require a verified
   publisher block it outright, and administrators see Microsoft's risk warning at consent time
   (see section 5).
5. **You control the service principal, not the registration.** In your tenant you can require
   user assignment, target it with Conditional Access, and revoke its consent — see section 6.
   You cannot change its permissions, its redirect URIs or its lifetime; only the maintainer
   can.
6. **No SLA.** There is no support commitment, no uptime promise and no notice period. Treat it
   as a convenience, not as infrastructure.

## 4. When to register your own instead

Use the shared app when you want to see whether Elevate is useful, or when a small tenant would
rather not maintain a registration.

Register your own — following [entra-app-registration.md](entra-app-registration.md) — when any
of these matters to you:

- you want to decide which scopes the app has, and when they change;
- you want to control its redirect URIs;
- you need the registration to exist for as long as you need it, on your terms;
- you target applications by application id in Conditional Access, and want that id to be
  yours;
- you apply app governance, app consent policies or a verified-publisher requirement;
- you are rolling Elevate out to a fleet, where a client id you own is pushed with the managed
  [`ClientId`](enterprise/keys.md#clientid) key.

Switching later is a one-field change: put your own client id into Settings (or push it), which
signs out the accounts that used the old one. Nothing else has to be redone.

## 5. Onboarding: granting admin consent

Admin consent is granted once per tenant, by a Privileged Role Administrator, an Application
Administrator or a Global Administrator — every scope in the link is delegated, so any of these
roles can grant it outright. Until it is granted, an account in that tenant signs in but shows
**manual roles**, because Elevate cannot read eligibility.

### From the app

Elevate offers the shared app and its consent link in four places:

- The **setup panel** on first launch: **Quick start with the shared Elevate app…** configures
  the shared app as the sign-in method. (The other buttons are **Open Settings…** for your own or
  your company's registration, and **Continue with the Azure CLI app**.)
- The **No accounts** state that follows, once the shared app is configured: **Grant admin
  consent…**.
- **Settings › Entra app registration**, which shows **Shared Elevate app — no SLA** and a
  **Grant admin consent…** button while the shared id is in effect.
- The **tenant menu**, for any account signed in with the Entra app registration method:
  **Open admin consent link…**, pre-filled for that tenant. Use it to re-consent after a release
  adds a scope.

### The link

If you would rather send an administrator a link, this is the same one, on one line:

```
https://login.microsoftonline.com/organizations/v2.0/adminconsent?client_id=c9011cc5-7422-4630-a432-73ff4df5834e&scope=https://graph.microsoft.com/User.Read https://graph.microsoft.com/RoleEligibilitySchedule.Read.Directory https://graph.microsoft.com/RoleAssignmentSchedule.ReadWrite.Directory https://graph.microsoft.com/RoleManagementPolicy.Read.Directory https://graph.microsoft.com/PrivilegedEligibilitySchedule.Read.AzureADGroup https://graph.microsoft.com/PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup https://graph.microsoft.com/RoleManagementPolicy.Read.AzureADGroup https://graph.microsoft.com/EntitlementMgmt-SubjectAccess.ReadWrite&redirect_uri=https://elevate.reothor.no/consent.html
```

This is the Graph scope list only — it deliberately omits
`https://management.azure.com/user_impersonation` for Azure resource roles, matching what
Elevate itself requests here; `user_impersonation` is user-consentable and is granted
automatically at first sign-in, so it does not need admin consent. The link above is identical to
the one the in-app **Grant admin consent…** button opens.

An administrator may replace `organizations` with their own tenant id to be certain the consent
lands in the right tenant. What each scope is for is in
[the permission table](entra-app-registration.md#4-permission-table).

From the CLI, `elevate config set client-id shared` selects the shared app (and states the no-SLA
caveat once), and `elevate consent` prints this link — `--tenant <id or domain>` for one tenant,
`--open` to open it in the browser.

> `az ad app permission admin-consent --id <client id>` does **not** work here. That command
> consents for an app registered in your own tenant; the shared app is registered in another
> one. Use the link.

### What the administrator sees

1. **Microsoft's "This app may be risky" warning** (AADSTS900981). It appears because the
   registration has no verified publisher. It cannot be removed, and it is not a judgement about
   Elevate — Microsoft shows it for every unverified multi-tenant app. Evaluate the permission
   list on the consent screen yourself; that list is the authoritative one, not this document.
2. **The consent prompt**, listing the delegated permissions above, with the "Consent on behalf
   of your organization" checkbox already implied by the admin consent endpoint.
3. **The result page** at <https://elevate.reothor.no/consent.html>, which says whether consent
   succeeded and what to do next. Microsoft has already recorded the consent by the time this
   page loads; the page itself receives nothing but the result parameters in the URL.

After consent, choose **Retry discovery** from the tenant menu in Elevate, or refresh.

## 6. Tenant-side controls

Consent is not the end of your control. Everything below lives on the *enterprise application*
(the service principal) the consent created in your tenant, under **Entra admin center →
Enterprise applications → Elevate**:

| Control | Where | What it does |
|---|---|---|
| **User assignment required** | Properties | Only users and groups you assign may sign in with Elevate at all. |
| **Conditional Access** | Your CA policies, targeting the app | Require MFA, compliant devices, trusted locations, or block it, exactly as for any other app. Note the app id is the shared one, so a policy targeting it targets Elevate for everyone in your tenant. |
| **Review and revoke consent** | Permissions | Shows exactly what was granted and by whom, and lets you revoke it. Revoking stops Elevate from reading or activating anything for your tenant. |
| **Sign-in logs** | Monitoring → Sign-in logs, filtered by the application | Every sign-in and token request, per user. |

For a managed fleet there are two more, both in
[Managed configuration keys](enterprise/keys.md):

- [`ClientId`](enterprise/keys.md#clientid) — push the client id you want everyone to use. Push
  your own registration's id if you have one; pushing the shared id is possible but locks your
  fleet to an app you do not control.
- [`AllowedTenants`](enterprise/keys.md#allowedtenants) — restrict which tenants people may add
  or discover, regardless of which registration is in effect.

## 7. Permissions

The shared registration has exactly the permissions in
[the permission table in the registration guide](entra-app-registration.md#4-permission-table)
— all delegated, none app-only — plus the standard `openid`, `profile` and `offline_access`.
That table is the single source of truth; it is not duplicated here so the two cannot drift
apart.

## 8. Troubleshooting

- **AADSTS900981 / "This app may be risky"**: expected. See section 5.
- **AADSTS65001 ("The user or administrator has not consented")**: consent has not been granted
  in that tenant yet. Use the link above.
- **Consent is blocked by a policy**: your tenant requires a verified publisher, or restricts
  which apps administrators may consent to. The shared app cannot satisfy a verified-publisher
  requirement — register your own instead.
- **A tenant shows "manual roles" after consent**: give it a moment, then choose **Retry
  discovery** from the tenant menu. More in [Troubleshooting](troubleshooting.md).
- **Which id is in effect?** Settings shows **Shared Elevate app — no SLA** when the shared one
  is, and **Copy diagnostics** reports `Client id: shared Elevate app`, `own registration` or
  `not set` — never the id itself.
