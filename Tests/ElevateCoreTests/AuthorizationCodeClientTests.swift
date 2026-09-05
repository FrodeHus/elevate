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
