# Pinned app registrations per account — design

Date: 2026-09-17
Status: approved in brainstorming, awaiting spec review

## Problem

An "Entra app registration" account always uses the client ID in Settings, and there is one
such ID per install. A user who has a second registration that mirrors the shared Elevate app
(same scopes, same redirect URIs, different client ID) can only add it as "Company app
(client ID)". That path uses the loopback flow instead of MSAL, treats a 403 as a plain
refusal rather than a consent problem, and does not build consent links for the app.

Separately, changing the Settings client ID today removes every `.ownApp` account together with
its tenants, profile entries and role memory (`AppModel.applyClientId` → `forgetIdentity`).

## Goals

- An account can be added with its own Elevate-equivalent registration ("pinned"), with the
  same behaviour as the Settings registration: MSAL on signed builds, loopback on unsigned
  builds, consent-required handling, consent links.
- Pinned accounts are independent of the Settings client ID.
- A pinned account's client ID can be changed while keeping its configuration; the change is
  committed only after a successful sign-in as the same user.
- Changing the Settings client ID keeps the affected accounts' configuration and asks them to
  sign in again instead of removing them.

## Non-goals

- Windows app and CLI UI (parity issues; see Scope).
- A managed-configuration key for pinning. Managed organisations use a single client ID.

## Scope

- macOS app: full feature.
- Swift Core: `SignInMethod` change.
- C# Core: reads and writes the new storage form so a macOS `state.json` keeps loading on
  Windows; golden file updated.
- GitHub parity issues: Windows app (WAM provider per client ID, Add account option, Change
  client ID) and CLI (`elevate accounts add --client-id`, `elevate accounts set-client-id`).

## Model

### Swift `SignInMethod`

Add a case; plain `.ownApp` keeps meaning "follow the Settings client ID".

```swift
case pinnedApp(clientId: String)
```

- Storage key: `ownApp:<id>`, the ID lower-cased on both encode and decode. `ownApp:` with an
  empty ID fails to decode, like `custom:`.
- `displayName`: "Entra app registration" (same as `.ownApp`). Headers and diagnostics that
  need to tell them apart append a shortened client ID.
- `clientId`: the pinned ID.
- `kind`: `.ownApp`, so `AllowedSignInMethods` treats both forms alike.
- New `isOwnApp`: true for `.ownApp` and `.pinnedApp`.
- `usesMSAL` becomes `isOwnApp`.
- `isPreauthorisedForEntraActivation`: true.
- `builtIn` is unchanged.

Equality checks against `.ownApp` must each be reviewed:

| Site | Meaning after the change |
|---|---|
| `GraphTransport.swift` consent mapping | `isOwnApp` (pinned accounts get consent-required errors) |
| `AppModel.applyClientId` | plain `.ownApp` only (pinned accounts are unaffected) |
| `AppModel.ownAppIdentityCount` | plain `.ownApp` only (count shown before a Settings change) |
| `AppModel+Accounts` availability and notices | handle `.pinnedApp` explicitly |
| `Identity` default and decode fallback | stays `.ownApp` |

### C# `SignInMethod`

`SignInMethodKind` is unchanged. Add a pinned form of `OwnApp`:

- `SignInMethod.PinnedApp(string clientId)` creates `Kind = OwnApp` with the client ID set
  (the existing `CustomClientId` property is renamed or generalised to `ClientIdOverride`;
  `Custom` keeps using it).
- `IsPinned`: `Kind == OwnApp && ClientIdOverride != null`.
- Parsing and formatting of `ownApp:<id>` match Swift, including lower-casing.
- `OwnApp` equality stays the unpinned form.
- The Windows app and CLI must not break on a pinned account: they treat it as "sign-in
  method not supported in this version" (the account is shown with a notice and skipped)
  until the parity issues land.

## Token providers (macOS app)

- New `MSALProviderRegistry`: one `MSALTokenProvider` per client ID, created on first use,
  all sharing `AppSettings.redirectUri`, the auth anchor and the interactive-sign-in gate. The
  current `msal` provider becomes the registry entry for the Settings ID.
- `CompositeTokenProvider` routes:
  - `.ownApp` → the registry entry for the Settings ID (or `ownAppLoopbackProvider` on unsigned builds)
  - `.pinnedApp(id)` → `registry.provider(id)`; on unsigned builds
    `loopback.provider(clientId: id, reportedMethod: .pinnedApp(id))`
- New `AppModel.effectiveClientId(for:)`: the pinned ID or the Settings ID. It replaces the
  body of `loopbackClientId(for:)`, so the shared-keychain-item check on duplicate accounts
  covers pinned accounts.
- `isAvailable(.pinnedApp(id))`: `id` is a valid GUID and there is a sign-in path (auth anchor
  on a signed build, or `ownAppViaLoopback`). It does not require the Settings ID.
- Refresh tokens stay keyed `<clientId>|<identityId>`; two accounts with the same pinned ID
  share a provider and keep separate tokens.

## Re-key flow

### `AppModel.rekey(_ identityIds:)`

For each account:
1. Remove its cached tokens under the old client ID (MSAL `removeCachedAccounts`, or loopback
   `signOut`) without opening a browser.
2. `dropRuntime(for:)`: remove roles, active assignments, activation and deactivation progress,
   recently deactivated entries, tenant errors, approvals and policies for the account. This is
   `forgetIdentity` without `state.removeIdentity`; `forgetIdentity` calls it.
3. Add the account to `signInNeeded` and its tenants to `tenantsAwaitingSignIn`.

Kept: the `state.identities` entry, the account's tenants with their pinned/hidden flags,
profile entries and role memory.

### Settings client ID change

`applyClientId` calls `rekey` for plain `.ownApp` accounts instead of `forgetIdentity`. Pinned
accounts are untouched. The confirmation text becomes "N accounts will need to sign in again".
The new ID takes effect immediately (several accounts cannot sign in atomically).

### Change client ID (account menu)

A sheet with two choices: "Use a different registration" (client ID field) and "Follow the
Settings registration". The latter needs a valid Settings ID. The action is shown for `.ownApp`
and `.pinnedApp` accounts, and disabled when the account is in `busy` or `inFlight`, or when
`ClientId` is managed.

Commit-on-success:
1. Validate the new ID; the same method as now is a no-op.
2. Get the provider for the new method and sign in interactively with the account's UPN as the
   login hint.
3. Cancelled or failed: nothing changes; the error is shown in the sheet.
4. A different user (`identity.id` differs): sign that session out, show "Signed in as X,
   expected Y", change nothing.
5. Success: set the account's method to the new one and save state; remove the old client ID's
   tokens for the account (unless the old and new effective ID are the same); `dropRuntime`;
   refresh the account. It is not put in `signInNeeded`.

### Sign-in after a re-key

`signIn(identity)` (the existing Sign in button) gains the same user check: if the returned
`identity.id` differs from the account's, sign the new session out, keep the account in
`signInNeeded` and show "Signed in as X, expected Y".

## Add account dialog

The "Entra app registration" row gets two sub-options:

```
◉ Entra app registration
    Full Entra, Azure and Groups support; needs admin consent in each tenant
    ◉ Use the registration in Settings  (Shared Elevate app)
    ○ Use a different registration
        [ Application (client) ID ]
        Needs the same setup as the Elevate app: redirect
        msauth.no.reothor.elevate://auth under Mobile and desktop
        applications, the Graph PIM scopes, admin consent
○ Azure CLI app
○ Azure PowerShell app
○ Company app (client ID)
    A registration that lists only http://localhost, such as an
    existing company PIM app; signs in through the browser
```

- The Settings sub-option shows "Shared Elevate app", "Managed by your organization" or a
  shortened client ID. Without a Settings ID it is disabled ("Configure a client ID in
  Settings") and the different-registration sub-option is preselected.
- On unsigned builds the setup text also mentions `http://localhost`.
- The different-registration sub-option is hidden when `ClientId` is managed. It follows
  `AllowedSignInMethods` → `ownApp`.
- An ID equal to the Settings ID shows: "Matches the registration in Settings; this account
  keeps this ID even if Settings changes." It is still stored pinned.
- The last pinned ID is remembered under its own defaults key (`pinnedClientIdKey`), separate
  from the Company app ID.
- The capability summary matches the Settings sub-option.
- The "already added with …" notice names the registration, e.g.
  "Entra app registration (1a2b…)".

## Elsewhere in the app

- Account header and Copy diagnostics show the pinned client ID.
- Admin-consent links and consent-required messages use `effectiveClientId(for:)`.
- The shared-app consent dialog appears only when the effective ID is the shared app's.
- First-launch setup (`SetupView`) is unchanged.

## Testing

Swift Core:
- `SignInMethod` storage round-trip for `ownApp:<id>`, lower-casing, empty-ID failure.
- `kind`, `isOwnApp`, `usesMSAL`, allow-list mapping.
- An old state file with only `ownApp` still decodes to `.ownApp`.

App (`ElevateAppTests`, with a fake token provider):
- `rekey` keeps the account, tenants, profile entries and role memory, and sets `signInNeeded`.
- `applyClientId` leaves pinned accounts alone and re-keys plain ones.
- Change client ID: commits on success; no change on cancel, failure or a different user.
- Sign in after re-key rejects a different user.
- Pinning is hidden when `ClientId` is managed.
- `isAvailable(.pinnedApp)` without a Settings ID.

C# Core:
- `SignInMethod` parse and format for `ownApp:<id>`.
- `state-macos.json` golden file gains a pinned account; the interop test covers it.
- The Windows app and CLI skip a pinned account with a notice instead of failing.

Manual:
- A copy of the shared registration in a test tenant, on a signed build (MSAL) and an unsigned
  build (loopback): add, refresh, activate, change client ID, change the Settings ID.
