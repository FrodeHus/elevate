import Foundation

public struct TokenResponse: Decodable, Sendable {
    public let accessToken: String
    public let refreshToken: String?
    public let expiresIn: Int
    public let idToken: String?
    public let scope: String?
    enum CodingKeys: String, CodingKey { case accessToken = "access_token", refreshToken = "refresh_token", expiresIn = "expires_in", idToken = "id_token", scope }

    public init(accessToken: String, refreshToken: String?, expiresIn: Int, idToken: String?, scope: String?) {
        self.accessToken = accessToken
        self.refreshToken = refreshToken
        self.expiresIn = expiresIn
        self.idToken = idToken
        self.scope = scope
    }
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
        if code == "interaction_required" || (code == "invalid_grant" && desc.contains(/AADSTS(50076|50079|53000|53001|50158|70008|700082|700084|50173|50133)/)) {
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
