# Elevate — sign-in methods (own app registration or Microsoft first-party app)

Date: 2026-09-05. Extends the phase 1 and phase 2 specs. Approved.

## 1. Goal

Let each account be added with one of three sign-in methods:

| Method | Client | Flow | Consent |
|---|---|---|---|
| `ownApp` | the client id from Settings | MSAL (unchanged) | admin consent per tenant |
| `azureCLI` | `04b07795-8ddb-461a-bbee-02f9e1bf7b46` | loopback auth-code + PKCE | pre-authorised in every tenant that allows Azure CLI |
| `azurePowerShell` | `1950a258-227b-4e31-a9cf-717495945fc2` | loopback auth-code + PKCE | same, for tenants that block the CLI app |

Success criteria:
- "Add account…" offers the three methods; the own-app option is disabled with a hint until a client id is configured.
- First-party accounts discover roles (Graph and ARM), activate, deactivate and cancel exactly like own-app accounts.
- The same user added twice with different methods is rejected with a clear message.
- Changing the client id in Settings only signs out own-app accounts.
- The setup view offers both paths: configure a client id, or continue with the Azure CLI app.

## 2. Model (Core, backward compatible)

- `enum SignInMethod: String, Codable, Sendable, CaseIterable { case ownApp, azureCLI, azurePowerShell }` with `displayName`, `clientId: String?` (nil for `ownApp`), `usesMSAL: Bool`.
- `Identity.signInMethod: SignInMethod` — custom `init(from:)` defaulting missing values to `.ownApp` so existing `state.json` decodes.
- `TokenProviding.signIn()` becomes `signIn(method: SignInMethod) async throws -> Identity`.

## 3. Loopback authorization-code flow (Core: `AuthorizationCodeClient`, `PKCE`)

Pure Foundation, testable against `HTTPClient`.

- `PKCE.generate() -> (verifier, challenge)`: 64 random bytes base64url → S256.
- `authorizeURL(clientId, tenant: "organizations" | tenantId, redirectURI, scopes, state, codeChallenge, claims: String?, loginHint: String?, prompt: "select_account" for sign-in)` on `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize` with `response_type=code`, `response_mode=query`, `code_challenge_method=S256`.
- `redeem(code, verifier, clientId, redirectURI, tenant, scopes) -> TokenResponse` (`POST …/oauth2/v2.0/token`, `grant_type=authorization_code`).
- `refresh(refreshToken, clientId, tenant, scopes) -> TokenResponse` (`grant_type=refresh_token`).
- `TokenResponse { accessToken, refreshToken?, expiresIn, idToken?, scope }`; `IdTokenClaims.parse(idToken) -> (oid, tid, preferredUsername, name)` (base64url JSON payload, no signature check — only used to name and key the account).
- Error mapping: `invalid_grant` with `AADSTS50076`/`50079`/`53000`/`53001`/`50158`/claims → `.interactionRequired`; `interaction_required` → `.interactionRequired`; `consent_required`/`AADSTS65001` → `.consentRequired`; `AADSTS7000218`/`AADSTS700016` → `.unexpected`; others → `.network(error_description)`.
- Scopes: sign-in requests `openid profile offline_access https://graph.microsoft.com/.default`; per-resource silent requests use `https://graph.microsoft.com/.default` or `https://management.azure.com/.default` (derived from the provider's scope list by resource host).

## 4. Session and storage

- Core `protocol RefreshTokenStore: Sendable { func load(identityId:) throws -> String?; func save(_:identityId:) throws; func delete(identityId:) throws }`.
- Core `actor OAuthSession` per client id: in-memory access-token cache keyed by (identityId, tenantId, resource) with expiry (60 s skew), a refresh-token store, `accessToken(identity:tenantId:scopes:)` (silent; refreshes; on failure throws `.interactionRequired`), `store(response:for:)`, `signOut(identityId:)`.
- App `KeychainRefreshTokenStore` (Security framework): generic password items, service `no.frodehus.elevate.refresh`, account `<clientId>|<identityId>`, `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`.

## 5. App providers

- `LoopbackTokenProvider: TokenProviding` (one per first-party client id): interactive sign-in = start `LoopbackListener` on `127.0.0.1` with an ephemeral port, open the authorize URL in the default browser (`NSWorkspace.shared.open`), wait (2 min timeout) for `GET /?code=…&state=…`, respond with a minimal HTML "You can close this window", verify state, redeem, parse id token → `Identity(id: "\(oid).\(tid)", upn: preferredUsername, displayName: name, homeTenantId: tid, signInMethod:)`. Interactive re-auth with claims uses the same path with `claims` and `login_hint`. Serialised through the existing `InteractiveGate` pattern. `identities()` lists the store's keys via a small `KnownIdentities` record persisted with the app state (identities are already in `AppState`; the provider only needs the refresh tokens).
- `CompositeTokenProvider: TokenProviding` routes by `identity.signInMethod`: `ownApp` → `MSALTokenProvider` (nil when not configured → `.unexpected("Configure a client id")`), others → the matching `LoopbackTokenProvider`. `signIn(method:)` dispatches; `identities()` unions.
- `AppModel`: `addAccount(method:)`; the duplicate check compares `identity.id` across methods and reports "This account is already added with <method>"; `signOut` routes; `applyClientId` signs out and clears only `ownApp` identities and their tenants; `adminConsentURL` only for `ownApp`; `bootstrap` reconciliation asks each provider.

## 6. UI

- "Add account…" opens `AddAccountView` (WindowGroup route `.addAccount`): three radio rows with one-line explanations; own-app row disabled with "Configure a client id in Settings" when unconfigured; Continue button runs `addAccount(method:)` and closes on success, shows the error inline otherwise.
- `SetupView` gains a second button "Continue with the Azure CLI app" next to "Open Settings…".
- Account header shows a caption "Azure CLI app" / "Azure PowerShell app" under the UPN for first-party accounts.
- README: explain the two paths, the loopback redirect (no registration needed), and the Conditional Access caveat.

## 7. Tests (Core)

`PKCETests`, `AuthorizationCodeClientTests` (authorize URL parameters incl. claims/login_hint, redeem body, refresh body, error mapping table, id token parsing), `OAuthSessionTests` (cache hit, refresh on expiry, per-tenant/per-resource keys, interactionRequired when no refresh token or refresh fails, signOut clears), `IdentityCodingTests` (missing `signInMethod` decodes as `.ownApp`), `SignInMethodTests` (client ids).
