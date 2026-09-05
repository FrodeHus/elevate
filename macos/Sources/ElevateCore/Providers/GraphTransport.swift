import Foundation

/// Sends authorized requests and maps non-success responses to `PIMError`.
public struct GraphTransport: Sendable {
    public static let graphBase = URL(string: "https://graph.microsoft.com/v1.0")!
    public static let armBase = URL(string: "https://management.azure.com")!
    let http: any HTTPClient
    let tokens: any TokenProviding
    let mapper: @Sendable (HTTPResponse) -> PIMError

    public init(http: any HTTPClient, tokens: any TokenProviding,
                mapper: @escaping @Sendable (HTTPResponse) -> PIMError = GraphTransport.mapError) {
        self.http = http
        self.tokens = tokens
        self.mapper = mapper
    }

    public func get(identity: Identity, tenantId: String, url: URL, scopes: [String]) async throws -> HTTPResponse {
        try await send(HTTPRequest(method: "GET", url: url), identity: identity, tenantId: tenantId, scopes: scopes)
    }

    public func post(identity: Identity, tenantId: String, url: URL, scopes: [String], body: Data) async throws -> HTTPResponse {
        try await send(HTTPRequest(method: "POST", url: url, headers: ["Content-Type": "application/json"], body: body),
                       identity: identity, tenantId: tenantId, scopes: scopes)
    }

    public func put(identity: Identity, tenantId: String, url: URL, scopes: [String], body: Data) async throws -> HTTPResponse {
        try await send(HTTPRequest(method: "PUT", url: url, headers: ["Content-Type": "application/json"], body: body),
                       identity: identity, tenantId: tenantId, scopes: scopes)
    }

    private func send(_ request: HTTPRequest, identity: Identity, tenantId: String, scopes: [String]) async throws -> HTTPResponse {
        var req = request
        let token = try await tokens.accessToken(identity: identity, tenantId: tenantId, scopes: scopes)
        req.headers["Authorization"] = "Bearer \(token)"
        req.headers["Accept"] = "application/json"
        let response = try await http.send(req)
        if (200..<300).contains(response.status) { return response }
        let error = mapper(response)
        // Admin consent only helps the user's own app registration; for a first-party sign-in a 403 is a plain refusal.
        if case .consentRequired = error, identity.signInMethod != .ownApp {
            throw PIMError.forbidden(Self.graphMessage(response.bodyText) ?? (response.bodyText.isEmpty ? "HTTP 403" : String(response.bodyText.prefix(300))))
        }
        throw error
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
            return .network("Throttled by the service; retry in \(retryAfter)")
        case 400:
            let text = r.bodyText
            if text.contains("ActiveDurationTooShort") || text.localizedCaseInsensitiveContains("within 5 minutes") || text.localizedCaseInsensitiveContains("within five minutes") {
                return .policyViolation("PIM requires a role to stay active for 5 minutes before it can be deactivated")
            }
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

    /// ARM variant: 403 is an RBAC denial at that scope, not a missing admin consent.
    public static func mapArmError(_ r: HTTPResponse) -> PIMError {
        if r.status == 403 { return .policyViolation("Not permitted at this scope") }
        return mapError(r)
    }

    static func graphMessage(_ body: String) -> String? {
        struct Envelope: Decodable { struct E: Decodable { let code: String?; let message: String? }; let error: E }
        return (try? JSONDecoder().decode(Envelope.self, from: Data(body.utf8)))?.error.message
    }
}
