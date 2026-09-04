import Foundation

/// Sends authorized requests and maps non-success responses to `PIMError`.
public struct GraphTransport: Sendable {
    public static let graphBase = URL(string: "https://graph.microsoft.com/v1.0")!
    let http: any HTTPClient
    let tokens: any TokenProviding

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        self.http = http
        self.tokens = tokens
    }

    public func get(identity: Identity, tenantId: String, url: URL, scopes: [String]) async throws -> HTTPResponse {
        try await send(HTTPRequest(method: "GET", url: url), identity: identity, tenantId: tenantId, scopes: scopes)
    }

    public func post(identity: Identity, tenantId: String, url: URL, scopes: [String], body: Data) async throws -> HTTPResponse {
        try await send(HTTPRequest(method: "POST", url: url, headers: ["Content-Type": "application/json"], body: body),
                       identity: identity, tenantId: tenantId, scopes: scopes)
    }

    private func send(_ request: HTTPRequest, identity: Identity, tenantId: String, scopes: [String]) async throws -> HTTPResponse {
        var req = request
        let token = try await tokens.accessToken(identity: identity, tenantId: tenantId, scopes: scopes)
        req.headers["Authorization"] = "Bearer \(token)"
        req.headers["Accept"] = "application/json"
        let response = try await http.send(req)
        if (200..<300).contains(response.status) { return response }
        throw Self.mapError(response)
    }

    public static func mapError(_ r: HTTPResponse) -> PIMError {
        switch r.status {
        case 401:
            if let h = r.header("WWW-Authenticate"), let claims = ClaimsChallenge.parse(wwwAuthenticate: h) {
                return .claimsChallenge(claims)
            }
            return .interactionRequired
        case 403:
            return .consentRequired
        case 429:
            let retryAfter = r.header("Retry-After").map { "\($0)s" } ?? "a few seconds"
            return .network("Throttled by Microsoft Graph; retry in \(retryAfter)")
        case 400:
            let text = r.bodyText
            if text.contains("RoleAssignmentRequestPolicyValidationFailed") || text.contains("RoleAssignmentRequestAcrsValidationFailed") {
                if text.contains("MfaRule") || text.contains("Acrs") {
                    // Graph did not hand us a claims header; ask the caller for a fresh interactive sign-in.
                    return .interactionRequired
                }
                return .policyViolation(Self.graphMessage(text) ?? "Policy validation failed")
            }
            if text.contains("RoleAssignmentDoesNotExist") || text.contains("RoleAssignmentRequestNotEligible") {
                return .notEligible
            }
            return .unexpected(status: 400, body: Self.graphMessage(text) ?? text)
        default:
            return .unexpected(status: r.status, body: Self.graphMessage(r.bodyText) ?? r.bodyText)
        }
    }

    static func graphMessage(_ body: String) -> String? {
        struct Envelope: Decodable { struct E: Decodable { let code: String?; let message: String? }; let error: E }
        return (try? JSONDecoder().decode(Envelope.self, from: Data(body.utf8)))?.error.message
    }
}
