# Elevate Sign-in Methods Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add accounts either through the user's own app registration (MSAL, existing) or through a Microsoft first-party app (Azure CLI or Azure PowerShell) using a loopback authorization-code flow with PKCE, so tenants that refuse consent still work.

**Architecture:** Core gains `SignInMethod`, `Identity.signInMethod`, a pure-Foundation `AuthorizationCodeClient` + `PKCE`, and an `OAuthSession` actor (token cache + refresh-token store protocol). The app gains `KeychainRefreshTokenStore`, `LoopbackListener` (Network framework), `LoopbackTokenProvider`, and a `CompositeTokenProvider` that routes per identity. `TokenProviding.signIn()` becomes `signIn(method:)`.

**Tech Stack:** Swift 6.2, Swift Testing, Network framework (`NWListener`), Security framework (Keychain), Microsoft identity platform v2 endpoints.

**Spec:** `docs/superpowers/specs/2026-09-05-elevate-signin-methods-design.md`

## Global Constraints

- Swift language mode 6, strict concurrency complete; macOS 26.0. `ElevateCore` imports only Foundation. No `@unchecked Sendable` in `AppModel`.
- First-party client ids: Azure CLI `04b07795-8ddb-461a-bbee-02f9e1bf7b46`, Azure PowerShell `1950a258-227b-4e31-a9cf-717495945fc2`. Loopback redirect `http://localhost:{port}` (any port); listener binds `127.0.0.1`.
- Identity id format for loopback accounts is `"\(oid).\(tid)"`, matching MSAL's home account identifier, so duplicates across methods collide on `id`.
- Sign-in scopes: `openid profile offline_access https://graph.microsoft.com/.default`. Silent scopes per resource: `https://graph.microsoft.com/.default` or `https://management.azure.com/.default`, chosen from the provider's scope list by host.
- Old `state.json` without `signInMethod` decodes as `.ownApp`.
- Tests use Swift Testing; network stubbed via `HTTPClient`. Commit after every task with the given message. Branch `signin-methods` from `main`.

## File structure

```
Sources/ElevateCore/Models/SignInMethod.swift            new enum + client ids
Sources/ElevateCore/Models/Identity.swift                + signInMethod (custom decode)
Sources/ElevateCore/Auth/TokenProviding.swift            signIn(method:)
Sources/ElevateCore/Auth/PKCE.swift                      new
Sources/ElevateCore/Auth/AuthorizationCodeClient.swift   new (authorize URL, redeem, refresh, id token, error map)
Sources/ElevateCore/Auth/OAuthSession.swift              new actor + RefreshTokenStore protocol
Tests/ElevateCoreTests/SignInMethodTests.swift, PKCETests.swift, AuthorizationCodeClientTests.swift, OAuthSessionTests.swift
Tests/ElevateCoreTests/Support/FakeTokenProvider.swift   signIn(method:)
Sources/ElevateApp/Auth/KeychainRefreshTokenStore.swift  new
Sources/ElevateApp/Auth/LoopbackListener.swift           new
Sources/ElevateApp/Auth/LoopbackTokenProvider.swift      new
Sources/ElevateApp/Auth/CompositeTokenProvider.swift     new
Sources/ElevateApp/MSAL/MSALTokenProvider.swift          signIn(method:)
Sources/ElevateApp/App/AppModel.swift                    addAccount(method:), routing, applyClientId scope
Sources/ElevateApp/App/PanelRoute.swift                  + .addAccount
Sources/ElevateApp/Views/AddAccountView.swift            new
Sources/ElevateApp/Views/SetupView.swift, PanelView.swift, IdentitySection.swift, RouteWindow.swift
README.md
```

---

### Task 1: SignInMethod, Identity.signInMethod, TokenProviding.signIn(method:)

**Files:**
- Create: `Sources/ElevateCore/Models/SignInMethod.swift`
- Modify: `Sources/ElevateCore/Models/Identity.swift`, `Sources/ElevateCore/Auth/TokenProviding.swift`, `Tests/ElevateCoreTests/Support/FakeTokenProvider.swift`, `Sources/ElevateApp/MSAL/MSALTokenProvider.swift` (signature only), `Sources/ElevateApp/App/AppModel.swift` (`UnavailableTokenProvider` signature and the one `tokens.signIn()` call → `tokens.signIn(method: .ownApp)`)
- Test: `Tests/ElevateCoreTests/SignInMethodTests.swift`

**Interfaces:**
- Produces: `public enum SignInMethod: String, Codable, Hashable, Sendable, CaseIterable { case ownApp, azureCLI, azurePowerShell; var displayName: String; var clientId: String?; var usesMSAL: Bool }`; `Identity.signInMethod: SignInMethod` (init parameter `signInMethod: SignInMethod = .ownApp`); `TokenProviding.signIn(method: SignInMethod) async throws -> Identity`.

- [ ] **Step 1: Write the failing tests**

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct SignInMethodTests {
    @Test func firstPartyClientIds() {
        #expect(SignInMethod.ownApp.clientId == nil)
        #expect(SignInMethod.azureCLI.clientId == "04b07795-8ddb-461a-bbee-02f9e1bf7b46")
        #expect(SignInMethod.azurePowerShell.clientId == "1950a258-227b-4e31-a9cf-717495945fc2")
        #expect(SignInMethod.ownApp.usesMSAL && !SignInMethod.azureCLI.usesMSAL)
        #expect(SignInMethod.allCases.count == 3)
    }

    @Test func identityDefaultsToOwnAppWhenFieldMissing() throws {
        let json = #"{"id":"oid.tid","upn":"u@x","displayName":"U","homeTenantId":"tid"}"#
        let i = try JSONDecoder().decode(Identity.self, from: Data(json.utf8))
        #expect(i.signInMethod == .ownApp)
        let round = try JSONDecoder().decode(Identity.self, from: JSONEncoder().encode(Identity(id: "a.b", upn: "u", displayName: "U", homeTenantId: "b", signInMethod: .azureCLI)))
        #expect(round.signInMethod == .azureCLI)
    }

    @Test func fakeProviderSignsInWithMethod() async throws {
        let tokens = FakeTokenProvider()
        let i = try await tokens.signIn(method: .azurePowerShell)
        #expect(i.signInMethod == .azurePowerShell)
    }
}
```

- [ ] **Step 2: Run to verify failure**: `swift test --filter SignInMethodTests 2>&1 | grep -E "error:" | head -3`

- [ ] **Step 3: Implement**

`SignInMethod.swift`:
```swift
import Foundation

/// How an account authenticates. First-party methods need no app registration or admin consent.
public enum SignInMethod: String, Codable, Hashable, Sendable, CaseIterable {
    case ownApp
    case azureCLI
    case azurePowerShell

    public var displayName: String {
        switch self {
        case .ownApp: "Own app registration"
        case .azureCLI: "Azure CLI app"
        case .azurePowerShell: "Azure PowerShell app"
        }
    }

    /// Fixed Microsoft client id, or nil when the user's own registration is used.
    public var clientId: String? {
        switch self {
        case .ownApp: nil
        case .azureCLI: "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
        case .azurePowerShell: "1950a258-227b-4e31-a9cf-717495945fc2"
        }
    }

    public var usesMSAL: Bool { self == .ownApp }
}
```

`Identity.swift`: add `public var signInMethod: SignInMethod`, the init parameter `signInMethod: SignInMethod = .ownApp`, and a custom decoder:
```swift
    enum CodingKeys: String, CodingKey { case id, upn, displayName, homeTenantId, signInMethod }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id)
        upn = try c.decode(String.self, forKey: .upn)
        displayName = try c.decode(String.self, forKey: .displayName)
        homeTenantId = try c.decode(String.self, forKey: .homeTenantId)
        signInMethod = try c.decodeIfPresent(SignInMethod.self, forKey: .signInMethod) ?? .ownApp
    }
```
(Keep synthesized `encode(to:)` by leaving the struct `Codable` with these keys.)

`TokenProviding.swift`: replace `func signIn() async throws -> Identity` with `func signIn(method: SignInMethod) async throws -> Identity` and update the doc comment. `FakeTokenProvider.signIn(method:)` returns `Identity(id: "new", upn: "new@x", displayName: "New", homeTenantId: "home", signInMethod: method)`. `MSALTokenProvider.signIn(method:)` ignores the parameter (it is always `.ownApp`) and sets `signInMethod: .ownApp` in `identity(from:)`. `UnavailableTokenProvider.signIn(method:)` keeps throwing. `AppModel.addAccount` calls `tokens.signIn(method: .ownApp)` for now.

- [ ] **Step 4: Test, build, commit**

```bash
swift test 2>&1 | tail -1
xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build 2>&1 | grep -E "error:|BUILD" | head -3
git add Sources Tests && git commit -m "Add SignInMethod and per-identity sign-in method"
```

---

### Task 2: PKCE and AuthorizationCodeClient

**Files:**
- Create: `Sources/ElevateCore/Auth/PKCE.swift`, `Sources/ElevateCore/Auth/AuthorizationCodeClient.swift`
- Test: `Tests/ElevateCoreTests/PKCETests.swift`, `Tests/ElevateCoreTests/AuthorizationCodeClientTests.swift`

**Interfaces:**
- Produces:
  - `public struct PKCE: Sendable { public let verifier: String; public let challenge: String; public static func generate() -> PKCE; static func challenge(for verifier: String) -> String }` (S256, base64url without padding).
  - `public struct TokenResponse: Decodable, Sendable { accessToken, refreshToken: String?, expiresIn: Int, idToken: String?, scope: String? }` (snake_case keys).
  - `public struct IdTokenClaims: Sendable { oid, tid, preferredUsername: String?, name: String?; public static func parse(_ idToken: String) throws -> IdTokenClaims }`.
  - `public struct AuthorizationCodeClient: Sendable { init(http:); static func authorizeURL(clientId:tenant:redirectURI:scopes:state:codeChallenge:claims:loginHint:prompt:) -> URL; func redeem(code:verifier:clientId:redirectURI:tenant:scopes:) async throws -> TokenResponse; func refresh(refreshToken:clientId:tenant:scopes:) async throws -> TokenResponse; static func mapTokenError(status:body:) -> PIMError; static func resourceScope(for scopes: [String]) -> String }`.

- [ ] **Step 1: Write the failing tests**

`PKCETests.swift`:
```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct PKCETests {
    @Test func challengeIsS256OfVerifier() {
        // RFC 7636 appendix B
        let verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
        #expect(PKCE.challenge(for: verifier) == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM")
    }

    @Test func generatedVerifierIsUrlSafeAndLongEnough() {
        let p = PKCE.generate()
        #expect(p.verifier.count >= 43 && p.verifier.count <= 128)
        #expect(p.verifier.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" })
        #expect(p.challenge == PKCE.challenge(for: p.verifier))
        #expect(PKCE.generate().verifier != p.verifier)
    }
}
```

`AuthorizationCodeClientTests.swift`:
```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct AuthorizationCodeClientTests {
    let clientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
    let redirect = "http://localhost:51234"

    func idToken(oid: String = "user-oid", tid: String = "tenant-1") -> String {
        let payload = #"{"oid":"\#(oid)","tid":"\#(tid)","preferred_username":"u@contoso.com","name":"User One"}"#
        let b64 = Data(payload.utf8).base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").trimmingCharacters(in: CharacterSet(charactersIn: "="))
        return "eyJhbGciOiJub25lIn0.\(b64)."
    }

    @Test func authorizeURLCarriesAllParameters() throws {
        let url = AuthorizationCodeClient.authorizeURL(clientId: clientId, tenant: "organizations", redirectURI: redirect,
                                                       scopes: ["openid", "offline_access", "https://graph.microsoft.com/.default"],
                                                       state: "st", codeChallenge: "ch", claims: #"{"access_token":{}}"#, loginHint: "u@contoso.com", prompt: "select_account")
        let c = URLComponents(url: url, resolvingAgainstBaseURL: false)!
        #expect(c.host == "login.microsoftonline.com" && c.path == "/organizations/oauth2/v2.0/authorize")
        let q = Dictionary(uniqueKeysWithValues: c.queryItems!.map { ($0.name, $0.value ?? "") })
        #expect(q["client_id"] == clientId)
        #expect(q["response_type"] == "code" && q["response_mode"] == "query")
        #expect(q["redirect_uri"] == redirect)
        #expect(q["scope"] == "openid offline_access https://graph.microsoft.com/.default")
        #expect(q["state"] == "st" && q["code_challenge"] == "ch" && q["code_challenge_method"] == "S256")
        #expect(q["claims"] == #"{"access_token":{}}"# && q["login_hint"] == "u@contoso.com" && q["prompt"] == "select_account")
    }

    @Test func redeemPostsFormAndDecodes() async throws {
        let http = StubHTTPClient()
        await http.on("POST", "/organizations/oauth2/v2.0/token", body: Data(#"{"token_type":"Bearer","scope":"x","expires_in":3599,"access_token":"AT","refresh_token":"RT","id_token":"\#(idToken())"}"#.utf8))
        let client = AuthorizationCodeClient(http: http)
        let r = try await client.redeem(code: "CODE", verifier: "VER", clientId: clientId, redirectURI: redirect, tenant: "organizations", scopes: ["https://graph.microsoft.com/.default"])
        #expect(r.accessToken == "AT" && r.refreshToken == "RT" && r.expiresIn == 3599)
        let req = await http.requests.first!
        #expect(req.headers["Content-Type"] == "application/x-www-form-urlencoded")
        let form = String(decoding: req.body!, as: UTF8.self)
        #expect(form.contains("grant_type=authorization_code") && form.contains("code=CODE") && form.contains("code_verifier=VER") && form.contains("client_id=\(clientId)"))
        #expect(form.contains("redirect_uri=http%3A%2F%2Flocalhost%3A51234"))
        let claims = try IdTokenClaims.parse(r.idToken!)
        #expect(claims.oid == "user-oid" && claims.tid == "tenant-1" && claims.preferredUsername == "u@contoso.com" && claims.name == "User One")
    }

    @Test func refreshPostsRefreshGrantAtTenantAuthority() async throws {
        let http = StubHTTPClient()
        await http.on("POST", "/tenant-2/oauth2/v2.0/token", body: Data(#"{"token_type":"Bearer","expires_in":100,"access_token":"AT2","refresh_token":"RT2"}"#.utf8))
        let r = try await AuthorizationCodeClient(http: http).refresh(refreshToken: "RT", clientId: clientId, tenant: "tenant-2", scopes: ["https://management.azure.com/.default"])
        #expect(r.accessToken == "AT2")
        let form = String(decoding: (await http.requests.first!).body!, as: UTF8.self)
        #expect(form.contains("grant_type=refresh_token") && form.contains("refresh_token=RT") && form.contains("scope=https%3A%2F%2Fmanagement.azure.com%2F.default"))
    }

    @Test func tokenErrorsMapToPIMErrors() {
        func err(_ e: String, _ desc: String) -> Data { Data(#"{"error":"\#(e)","error_description":"\#(desc)"}"#.utf8) }
        #expect(AuthorizationCodeClient.mapTokenError(status: 400, body: err("invalid_grant", "AADSTS50076: MFA required")) == .interactionRequired)
        #expect(AuthorizationCodeClient.mapTokenError(status: 400, body: err("interaction_required", "AADSTS50079: x")) == .interactionRequired)
        #expect(AuthorizationCodeClient.mapTokenError(status: 400, body: err("invalid_grant", "AADSTS65001: consent")) == .consentRequired)
        #expect(AuthorizationCodeClient.mapTokenError(status: 400, body: err("invalid_grant", "AADSTS700082: expired")) == .interactionRequired)
        #expect(AuthorizationCodeClient.mapTokenError(status: 400, body: err("invalid_client", "AADSTS7000218: secret")) == .unexpected(status: 400, body: "AADSTS7000218: secret"))
        #expect(AuthorizationCodeClient.mapTokenError(status: 400, body: err("temporarily_unavailable", "try later")) == .network("try later"))
    }

    @Test func resourceScopeIsDerivedFromScopeHost() {
        #expect(AuthorizationCodeClient.resourceScope(for: ["https://graph.microsoft.com/User.Read", "https://graph.microsoft.com/RoleEligibilitySchedule.Read.Directory"]) == "https://graph.microsoft.com/.default")
        #expect(AuthorizationCodeClient.resourceScope(for: ["https://management.azure.com/user_impersonation"]) == "https://management.azure.com/.default")
    }
}
```

- [ ] **Step 2: Run to verify failure**: `swift test --filter "PKCETests|AuthorizationCodeClientTests" 2>&1 | grep -E "error:" | head -3`

- [ ] **Step 3: Implement**

`PKCE.swift`:
```swift
import Foundation
import CryptoKit

public struct PKCE: Sendable {
    public let verifier: String
    public let challenge: String

    public static func generate() -> PKCE {
        var bytes = [UInt8](repeating: 0, count: 64)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        let verifier = Data(bytes).base64URLEncodedString()
        return PKCE(verifier: verifier, challenge: challenge(for: verifier))
    }

    static func challenge(for verifier: String) -> String {
        Data(SHA256.hash(data: Data(verifier.utf8))).base64URLEncodedString()
    }
}

extension Data {
    func base64URLEncodedString() -> String {
        base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").trimmingCharacters(in: CharacterSet(charactersIn: "="))
    }
    init?(base64URLEncoded s: String) {
        var b = s.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while b.count % 4 != 0 { b += "=" }
        self.init(base64Encoded: b)
    }
}
```
`CryptoKit` and `Security` (for `SecRandomCopyBytes`) are system frameworks available to a Foundation-only package on macOS; if the "Foundation only" rule is read strictly, use `SystemRandomNumberGenerator` to fill the bytes instead of `SecRandomCopyBytes` (do that: `var g = SystemRandomNumberGenerator(); bytes = (0..<64).map { _ in UInt8.random(in: .min ... .max, using: &g) }`). CryptoKit stays.

`AuthorizationCodeClient.swift`:
```swift
import Foundation

public struct TokenResponse: Decodable, Sendable {
    public let accessToken: String
    public let refreshToken: String?
    public let expiresIn: Int
    public let idToken: String?
    public let scope: String?
    enum CodingKeys: String, CodingKey { case accessToken = "access_token", refreshToken = "refresh_token", expiresIn = "expires_in", idToken = "id_token", scope }
}

public struct IdTokenClaims: Sendable {
    public let oid: String
    public let tid: String
    public let preferredUsername: String?
    public let name: String?

    public static func parse(_ idToken: String) throws -> IdTokenClaims {
        let parts = idToken.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count >= 2, let data = Data(base64URLEncoded: String(parts[1])) else {
            throw PIMError.unexpected(status: 0, body: "Malformed id token")
        }
        struct Payload: Decodable { let oid: String; let tid: String; let preferred_username: String?; let name: String? }
        let p = try JSONDecoder().decode(Payload.self, from: data)
        return IdTokenClaims(oid: p.oid, tid: p.tid, preferredUsername: p.preferred_username, name: p.name)
    }
}

/// Microsoft identity platform v2 authorization-code flow for public clients (PKCE, no secret).
public struct AuthorizationCodeClient: Sendable {
    let http: any HTTPClient
    public init(http: any HTTPClient) { self.http = http }

    public static func authorizeURL(clientId: String, tenant: String, redirectURI: String, scopes: [String], state: String,
                                    codeChallenge: String, claims: String? = nil, loginHint: String? = nil, prompt: String? = nil) -> URL {
        var c = URLComponents(string: "https://login.microsoftonline.com/\(tenant)/oauth2/v2.0/authorize")!
        var items = [
            URLQueryItem(name: "client_id", value: clientId),
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "response_mode", value: "query"),
            URLQueryItem(name: "redirect_uri", value: redirectURI),
            URLQueryItem(name: "scope", value: scopes.joined(separator: " ")),
            URLQueryItem(name: "state", value: state),
            URLQueryItem(name: "code_challenge", value: codeChallenge),
            URLQueryItem(name: "code_challenge_method", value: "S256"),
        ]
        if let claims { items.append(URLQueryItem(name: "claims", value: claims)) }
        if let loginHint { items.append(URLQueryItem(name: "login_hint", value: loginHint)) }
        if let prompt { items.append(URLQueryItem(name: "prompt", value: prompt)) }
        c.queryItems = items
        return c.url!
    }

    public func redeem(code: String, verifier: String, clientId: String, redirectURI: String, tenant: String, scopes: [String]) async throws -> TokenResponse {
        try await token(tenant: tenant, form: [
            "client_id": clientId, "grant_type": "authorization_code", "code": code,
            "redirect_uri": redirectURI, "code_verifier": verifier, "scope": scopes.joined(separator: " "),
        ])
    }

    public func refresh(refreshToken: String, clientId: String, tenant: String, scopes: [String]) async throws -> TokenResponse {
        try await token(tenant: tenant, form: [
            "client_id": clientId, "grant_type": "refresh_token", "refresh_token": refreshToken,
            "scope": scopes.joined(separator: " "),
        ])
    }

    private func token(tenant: String, form: [String: String]) async throws -> TokenResponse {
        let url = URL(string: "https://login.microsoftonline.com/\(tenant)/oauth2/v2.0/token")!
        let body = form.sorted { $0.key < $1.key }.map { "\($0.key)=\(Self.formEncode($0.value))" }.joined(separator: "&")
        let r = try await http.send(HTTPRequest(method: "POST", url: url, headers: ["Content-Type": "application/x-www-form-urlencoded", "Accept": "application/json"], body: Data(body.utf8)))
        guard (200..<300).contains(r.status) else { throw Self.mapTokenError(status: r.status, body: r.body) }
        return try JSONDecoder().decode(TokenResponse.self, from: r.body)
    }

    static func formEncode(_ s: String) -> String {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        return s.addingPercentEncoding(withAllowedCharacters: allowed) ?? s
    }

    public static func mapTokenError(status: Int, body: Data) -> PIMError {
        struct E: Decodable { let error: String?; let error_description: String? }
        let e = try? JSONDecoder().decode(E.self, from: body)
        let desc = e?.error_description ?? String(decoding: body, as: UTF8.self)
        let code = e?.error ?? ""
        if desc.contains("AADSTS65001") || code == "consent_required" { return .consentRequired }
        if code == "interaction_required" || code == "invalid_grant" && desc.contains(/AADSTS(50076|50079|53000|53001|50158|70008|700082|700084|50173|50133)/) {
            return .interactionRequired
        }
        if code == "invalid_grant" { return .interactionRequired }
        if code == "invalid_client" || code == "unauthorized_client" || code == "invalid_request" { return .unexpected(status: status, body: desc) }
        return .network(desc)
    }

    /// `.default` scope for the resource named by the first scope's host.
    public static func resourceScope(for scopes: [String]) -> String {
        guard let first = scopes.first(where: { $0.hasPrefix("https://") }), let url = URL(string: first), let host = url.host else {
            return "https://graph.microsoft.com/.default"
        }
        return "https://\(host)/.default"
    }
}
```

- [ ] **Step 4: Test, commit**

```bash
swift test 2>&1 | tail -1
git add Sources/ElevateCore Tests/ElevateCoreTests && git commit -m "Add PKCE and the v2 authorization-code client"
```

---

### Task 3: OAuthSession actor and RefreshTokenStore

**Files:**
- Create: `Sources/ElevateCore/Auth/OAuthSession.swift`
- Test: `Tests/ElevateCoreTests/OAuthSessionTests.swift`

**Interfaces:**
- Produces:
  - `public protocol RefreshTokenStore: Sendable { func load(identityId: String) throws -> String?; func save(_ token: String, identityId: String) throws; func delete(identityId: String) throws; func allIdentityIds() throws -> [String] }`
  - `public final class InMemoryRefreshTokenStore: RefreshTokenStore` (thread-safe via a lock; for tests and previews).
  - `public actor OAuthSession { init(clientId:client:store:now:); func accessToken(identityId:tenantId:scopes:) async throws -> String; func store(_ response: TokenResponse, identityId: String, tenantId: String, scopes: [String]); func signOut(identityId:) throws; func knownIdentityIds() throws -> [String] }`. Silent path: cache hit (expiry minus 60 s) → return; else load refresh token (throw `.interactionRequired` if none) → `client.refresh` at the tenant authority with `resourceScope(for: scopes)` → cache access token, rotate refresh token; a refresh failure that maps to `.interactionRequired`/`.consentRequired` is rethrown as is, other errors as `.network`.

- [ ] **Step 1: Write the failing tests**

```swift
import Testing
import Foundation
@testable import ElevateCore

@Suite struct OAuthSessionTests {
    let clientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"

    func session(_ http: StubHTTPClient, store: InMemoryRefreshTokenStore = InMemoryRefreshTokenStore(), now: @escaping @Sendable () -> Date = { Date() }) -> OAuthSession {
        OAuthSession(clientId: clientId, client: AuthorizationCodeClient(http: http), store: store, now: now)
    }

    @Test func noRefreshTokenMeansInteractionRequired() async throws {
        let s = session(StubHTTPClient())
        await #expect(throws: PIMError.interactionRequired) { _ = try await s.accessToken(identityId: "oid.tid", tenantId: "tid", scopes: GraphScopes.all) }
    }

    @Test func refreshesPerTenantAndResourceThenCaches() async throws {
        let http = StubHTTPClient()
        let store = InMemoryRefreshTokenStore()
        try store.save("RT0", identityId: "oid.tid")
        await http.on("POST", "/tid/oauth2/v2.0/token", body: Data(#"{"expires_in":3600,"access_token":"G1","refresh_token":"RT1"}"#.utf8))
        await http.on("POST", "/other/oauth2/v2.0/token", body: Data(#"{"expires_in":3600,"access_token":"A1"}"#.utf8))
        let s = session(http, store: store)
        #expect(try await s.accessToken(identityId: "oid.tid", tenantId: "tid", scopes: GraphScopes.all) == "G1")
        #expect(try await s.accessToken(identityId: "oid.tid", tenantId: "tid", scopes: GraphScopes.all) == "G1")   // cached
        #expect(try await s.accessToken(identityId: "oid.tid", tenantId: "other", scopes: ArmScopes.all) == "A1")
        #expect(await http.requests.count == 2)
        #expect(try store.load(identityId: "oid.tid") == "RT1")                                                     // rotated
        let arm = String(decoding: (await http.requests.last!).body!, as: UTF8.self)
        #expect(arm.contains("scope=https%3A%2F%2Fmanagement.azure.com%2F.default") && arm.contains("refresh_token=RT1"))
    }

    @Test func expiredCacheEntryIsRefreshed() async throws {
        let http = StubHTTPClient()
        let store = InMemoryRefreshTokenStore(); try store.save("RT", identityId: "i")
        await http.on("POST", "/t/oauth2/v2.0/token", body: Data(#"{"expires_in":100,"access_token":"X"}"#.utf8))
        let clock = Clock(); let s = session(http, store: store) { clock.now }
        _ = try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all)
        clock.advance(50); _ = try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all)   // still valid (skew 60s means 40s left)
        #expect(await http.requests.count == 2)   // 100 - 60 skew = 40 < 50 elapsed → refreshed
    }

    @Test func refreshFailureRequiringInteractionClearsNothingButThrows() async throws {
        let http = StubHTTPClient()
        let store = InMemoryRefreshTokenStore(); try store.save("RT", identityId: "i")
        await http.on("POST", "/oauth2/v2.0/token", status: 400, body: Data(#"{"error":"invalid_grant","error_description":"AADSTS50076: mfa"}"#.utf8))
        let s = session(http, store: store)
        await #expect(throws: PIMError.interactionRequired) { _ = try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all) }
        #expect(try store.load(identityId: "i") == "RT")
    }

    @Test func storeAndSignOut() async throws {
        let store = InMemoryRefreshTokenStore()
        let s = session(StubHTTPClient(), store: store)
        await s.store(TokenResponse(accessToken: "AT", refreshToken: "RT", expiresIn: 3600, idToken: nil, scope: nil), identityId: "i", tenantId: "t", scopes: GraphScopes.all)
        #expect(try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all) == "AT")
        #expect(try await s.knownIdentityIds() == ["i"])
        try await s.signOut(identityId: "i")
        #expect(try store.load(identityId: "i") == nil)
        await #expect(throws: PIMError.interactionRequired) { _ = try await s.accessToken(identityId: "i", tenantId: "t", scopes: GraphScopes.all) }
    }
}

final class Clock: @unchecked Sendable {
    private let lock = NSLock(); private var t = Date(timeIntervalSince1970: 1_000_000)
    var now: Date { lock.withLock { t } }
    func advance(_ s: TimeInterval) { lock.withLock { t = t.addingTimeInterval(s) } }
}
```
`TokenResponse` needs a public memberwise initializer for the test: add `public init(accessToken:refreshToken:expiresIn:idToken:scope:)` in Task 2's file when implementing this task.

- [ ] **Step 2: Run to verify failure**: `swift test --filter OAuthSessionTests 2>&1 | grep -E "error:" | head -3`

- [ ] **Step 3: Implement**

```swift
import Foundation

public protocol RefreshTokenStore: Sendable {
    func load(identityId: String) throws -> String?
    func save(_ token: String, identityId: String) throws
    func delete(identityId: String) throws
    func allIdentityIds() throws -> [String]
}

public final class InMemoryRefreshTokenStore: RefreshTokenStore {
    private let lock = NSLock()
    private var tokens: [String: String] = [:]
    public init() {}
    public func load(identityId: String) throws -> String? { lock.withLock { tokens[identityId] } }
    public func save(_ token: String, identityId: String) throws { lock.withLock { tokens[identityId] = token } }
    public func delete(identityId: String) throws { lock.withLock { tokens[identityId] = nil } }
    public func allIdentityIds() throws -> [String] { lock.withLock { tokens.keys.sorted() } }
}

/// Access-token cache plus refresh-token handling for one public client id.
public actor OAuthSession {
    struct CacheKey: Hashable { let identityId: String; let tenantId: String; let resource: String }
    struct Entry { let token: String; let expiresAt: Date }
    static let skew: TimeInterval = 60

    let clientId: String
    let client: AuthorizationCodeClient
    let store: any RefreshTokenStore
    let now: @Sendable () -> Date
    private var cache: [CacheKey: Entry] = [:]

    public init(clientId: String, client: AuthorizationCodeClient, store: any RefreshTokenStore, now: @escaping @Sendable () -> Date = { Date() }) {
        self.clientId = clientId
        self.client = client
        self.store = store
        self.now = now
    }

    public func accessToken(identityId: String, tenantId: String, scopes: [String]) async throws -> String {
        let resource = AuthorizationCodeClient.resourceScope(for: scopes)
        let key = CacheKey(identityId: identityId, tenantId: tenantId, resource: resource)
        if let e = cache[key], e.expiresAt.timeIntervalSince(now()) > Self.skew { return e.token }
        guard let refreshToken = try store.load(identityId: identityId) else { throw PIMError.interactionRequired }
        let response: TokenResponse
        do {
            response = try await client.refresh(refreshToken: refreshToken, clientId: clientId, tenant: tenantId, scopes: [resource])
        } catch let e as PIMError {
            switch e {
            case .interactionRequired, .consentRequired, .claimsChallenge: throw e
            case .network: throw e
            default: throw PIMError.network(e.userMessage)
            }
        }
        store(response, identityId: identityId, tenantId: tenantId, scopes: scopes)
        return response.accessToken
    }

    public func store(_ response: TokenResponse, identityId: String, tenantId: String, scopes: [String]) {
        let resource = AuthorizationCodeClient.resourceScope(for: scopes)
        cache[CacheKey(identityId: identityId, tenantId: tenantId, resource: resource)] = Entry(token: response.accessToken, expiresAt: now().addingTimeInterval(TimeInterval(response.expiresIn)))
        if let rt = response.refreshToken { try? store.save(rt, identityId: identityId) }
    }

    public func signOut(identityId: String) throws {
        cache = cache.filter { $0.key.identityId != identityId }
        try store.delete(identityId: identityId)
    }

    public func knownIdentityIds() throws -> [String] { try store.allIdentityIds() }
}
```
`store(_:identityId:tenantId:scopes:)` is a synchronous actor method; calling it from `accessToken` inside the actor needs no `await`.

- [ ] **Step 4: Test, commit**

```bash
swift test 2>&1 | tail -1
git add Sources/ElevateCore Tests/ElevateCoreTests && git commit -m "Add OAuthSession with refresh-token store and per-resource cache"
```

---

### Task 4: Keychain store, loopback listener, loopback and composite providers (app)

**Files:**
- Create: `Sources/ElevateApp/Auth/KeychainRefreshTokenStore.swift`, `Sources/ElevateApp/Auth/LoopbackListener.swift`, `Sources/ElevateApp/Auth/LoopbackTokenProvider.swift`, `Sources/ElevateApp/Auth/CompositeTokenProvider.swift`

**Interfaces:**
- `final class KeychainRefreshTokenStore: RefreshTokenStore` — `init(clientId:)`; service `no.frodehus.elevate.refresh`, account `"\(clientId)|\(identityId)"`, `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`; `allIdentityIds` queries by service and filters the account prefix.
- `actor LoopbackListener` — `static func start() async throws -> LoopbackListener` (NWListener on `127.0.0.1`, port `.any`, `port: UInt16`), `func waitForCode(expectedState: String, timeout: Duration = .seconds(120)) async throws -> String` (parses the first `GET /?code=…&state=…` or `error=…` line, replies `HTTP/1.1 200 OK` with a minimal HTML page "You can close this window and return to Elevate.", then stops; `state` mismatch → `PIMError.unexpected`; `error=access_denied` → `PIMError.network("Sign-in cancelled")`; timeout → `PIMError.network("Sign-in timed out")`).
- `final class LoopbackTokenProvider: TokenProviding, Sendable` — `init(method: SignInMethod, http: any HTTPClient, store: any RefreshTokenStore)` (precondition `method.clientId != nil`); `signIn(method:)`: PKCE + state, listener, `NSWorkspace.shared.open(authorizeURL)` (tenant `organizations`, prompt `select_account`, sign-in scopes from the spec), wait for code, `redeem`, parse id token → `Identity(id: "\(oid).\(tid)", …, signInMethod: method)`, `session.store(...)` for Graph; `accessToken` → `session.accessToken`; `acquireInteractively(identity:tenantId:scopes:claims:)` → same loopback dance with `login_hint: identity.upn`, tenant = `tenantId`, `claims`, scopes `[resourceScope(for: scopes), "openid", "offline_access"]`, then `session.store`; `signOut` → `session.signOut`; `identities()` → `[]` (identities live in `AppState`; the provider is not the source of truth) — but expose `func hasRefreshToken(for identityId:) async -> Bool` for reconciliation. All interactive work goes through an `InteractiveGate` (copy the actor from `MSALTokenProvider` into its own file `Sources/ElevateApp/Auth/InteractiveGate.swift` and make both providers use it).
- `final class CompositeTokenProvider: TokenProviding, Sendable` — `init(msal: MSALTokenProvider?, loopback: [SignInMethod: LoopbackTokenProvider])`; routes every call by `identity.signInMethod` (`signIn(method:)` by the argument); `identities()` returns the MSAL provider's identities (first-party identities are reconciled by `AppModel` via `hasRefreshToken`); an `ownApp` call with `msal == nil` throws `PIMError.unexpected(status: 0, body: "Configure a client id in Settings")`.

- [ ] **Step 1: Implement the four files** following the interfaces above. Use `Network` (`NWListener`, `NWConnection`), read the request with `receive(minimumIncompleteLength: 1, maximumLength: 8192)` accumulating until `\r\n\r\n`, parse the request line's path with `URLComponents(string: "http://localhost" + path)`. Reply and `connection.cancel()` after send completes; `listener.cancel()` when done or on timeout.

- [ ] **Step 2: Build**

```bash
xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build 2>&1 | grep -E "error:|warning:.*Sources/ElevateApp|BUILD" | head
```
Expected BUILD SUCCEEDED (the new types are not yet wired).

- [ ] **Step 3: Commit**

```bash
git add Sources/ElevateApp && git commit -m "Add loopback OAuth provider, keychain refresh-token store and composite routing"
```

---

### Task 5: Wire AppModel and UI, README

**Files:**
- Modify: `Sources/ElevateApp/App/AppModel.swift`, `Sources/ElevateApp/App/PanelRoute.swift`, `Sources/ElevateApp/Views/RouteWindow.swift`, `Sources/ElevateApp/Views/PanelView.swift`, `Sources/ElevateApp/Views/SetupView.swift`, `Sources/ElevateApp/Views/IdentitySection.swift`, `README.md`
- Create: `Sources/ElevateApp/Views/AddAccountView.swift`

- [ ] **Step 1: AppModel**
  - `live()`: build `loopback = [.azureCLI: LoopbackTokenProvider(method: .azureCLI, http:, store: KeychainRefreshTokenStore(clientId:)), .azurePowerShell: …]`, `msal` when configured, and `tokens = CompositeTokenProvider(msal:loopback:)`. `applyClientId` rebuilds the composite with the new MSAL provider and the same loopback providers; it signs out, removes and clears only identities with `signInMethod == .ownApp` (and their tenants/roles/active/memory) instead of the whole state.
  - `isConfigured` keeps meaning "own-app method available". Add `var canAddAccount: Bool { true }` and `var availableMethods: [SignInMethod]` (all three; the view disables `.ownApp` when `!isConfigured`).
  - `addAccount(method:)`: duplicate check `if let existing = state.identities.first(where: { $0.id == identity.id }), existing.signInMethod != method { notice = "This account is already added with \(existing.signInMethod.displayName)"; try? await tokens.signOut(identity); return }`; otherwise as before.
  - `bootstrap` reconciliation: keep MSAL reconciliation for `.ownApp`; for first-party identities drop those for which `loopback[method].hasRefreshToken(for:)` is false.
  - `adminConsentURL(tenantId:)` gains an `identityId` parameter check: return nil when the identity's method is not `.ownApp` (callers pass the tenant's identity).
  - `activate`'s consent fallback and the Entra consent flip stay.
- [ ] **Step 2: Routes and views**
  - `PanelRoute.addAccount`; `RouteWindow` maps it to `AddAccountView()`.
  - `AddAccountView`: title "Add account", three radio rows (`Picker` with `.radioGroup` style) each with a caption: own app ("Uses the client ID from Settings; needs admin consent in each tenant"), Azure CLI ("Microsoft's Azure CLI app; works wherever Azure CLI is allowed; no consent needed"), Azure PowerShell ("Same, for tenants that block the Azure CLI app"); own-app row disabled with the hint when `!model.isConfigured` and the default selection is the first enabled method; Cancel / Continue; Continue runs `await model.addAccount(method:)` with a spinner and closes on success, shows `model.notice`-style inline error otherwise (clear the notice it set).
  - `PanelView` footer: "Add account…" opens `PanelRoute.addAccount` (always enabled).
  - `SetupView`: text "Elevate can sign in with your own Entra app registration, or with Microsoft's Azure CLI app which needs no registration."; buttons: `SettingsLink { Text("Open Settings…") }` and `Button("Continue with the Azure CLI app") { openWindow(value: PanelRoute.addAccount) }`. The panel shows `SetupView` only when `!model.isConfigured && model.identities.isEmpty`.
  - `IdentityHeader` and `TenantHeader` caption: for `identity.signInMethod != .ownApp` show `Text(identity.signInMethod.displayName).font(.caption2).foregroundStyle(.secondary)` under/next to the UPN.
- [ ] **Step 3: README** — new section "Sign-in methods": the two paths, no redirect registration for first-party (loopback on 127.0.0.1), Conditional Access caveat (tenants that block Azure CLI or public clients), and that changing the client id affects own-app accounts only.
- [ ] **Step 4: Build, test, commit**

```bash
swift test 2>&1 | tail -1
xcodegen generate && xcodebuild -project Elevate.xcodeproj -scheme ElevateApp -configuration Debug -derivedDataPath build -allowProvisioningUpdates build 2>&1 | grep -E "error:|warning:.*Sources/ElevateApp|BUILD" | head
git add Sources README.md && git commit -m "Add accounts with the Azure CLI or Azure PowerShell app via loopback sign-in"
```
