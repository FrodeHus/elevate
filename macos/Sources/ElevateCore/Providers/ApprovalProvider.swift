import Foundation

/// Reads the requests awaiting the signed-in user's approval, and sends the decision.
public protocol ApprovalProvider: Sendable {
    var kind: RoleScopeKind { get }
    /// Token scopes this provider needs; all against one resource.
    var scopes: [String] { get }
    func pendingApprovals(identity: Identity, tenant: TenantContext) async throws -> [ApprovalRequest]
    func decide(_ request: ApprovalRequest, approve: Bool, justification: String, identity: Identity) async throws
}

/// Wire shapes and rules the two Graph approval providers share.
enum GraphApprovals {
    /// The expanded `principal` of a request. `@odata.type` is ignored.
    struct Principal: Decodable { let displayName: String?; let userPrincipalName: String? }
    struct Named: Decodable { let id: String?; let displayName: String? }
    struct Expiration: Decodable { let type: String?; let duration: String? }
    struct ScheduleInfo: Decodable { let startDateTime: Date?; let expiration: Expiration? }
    struct Collection<T: Decodable>: Decodable { let value: [T] }
    struct Step: Decodable { let id: String; let status: String?; let assignedToMe: Bool? }

    /// `selfActivate` → activate, `selfExtend` → extend, `selfRenew` → renew, anything else → other.
    static func action(_ raw: String?) -> ApprovalRequest.Action {
        switch raw?.lowercased() {
        case "selfactivate": .activate
        case "selfextend": .extend
        case "selfrenew": .renew
        default: .other
        }
    }

    /// Display name, then UPN, then the principal id the request carries.
    static func requester(_ principal: Principal?, principalId: String?) -> String {
        principal?.displayName ?? principal?.userPrincipalName ?? principalId ?? "Unknown"
    }

    /// The step to decide: the one in progress, else the one assigned to us, else the first.
    static func step(_ steps: [Step]) throws -> Step {
        if let inProgress = steps.first(where: { $0.status?.caseInsensitiveCompare("InProgress") == .orderedSame }) { return inProgress }
        if let mine = steps.first(where: { $0.assignedToMe == true }) { return mine }
        guard let first = steps.first else { throw PIMError.unexpected(status: 0, body: "No approval step to decide") }
        return first
    }

    static func decisionBody(approve: Bool, justification: String) throws -> Data {
        try JSONSerialization.data(withJSONObject: ["reviewResult": approve ? "Approve" : "Deny", "justification": justification])
    }

    /// GETs the approval's steps on the beta base, then PATCHes the one to decide.
    static func decide(_ request: ApprovalRequest, approve: Bool, justification: String, identity: Identity,
                       transport: GraphTransport, scopes: [String], approvalsPath: String) async throws {
        let approvalId = request.decisionRef ?? request.id
        let stepsURL = try transport.graphBetaURL("\(approvalsPath)/\(approvalId)/steps")
        let r = try await transport.get(identity: identity, tenantId: request.tenantKey.tenantId, url: stepsURL, scopes: scopes)
        let steps = try GraphJSON.decoder.decode(Collection<Step>.self, from: r.body).value
        let step = try step(steps)
        _ = try await transport.patch(identity: identity, tenantId: request.tenantKey.tenantId,
                                      url: try transport.graphBetaURL("\(approvalsPath)/\(approvalId)/steps/\(step.id)"),
                                      scopes: scopes, body: try decisionBody(approve: approve, justification: justification))
    }
}
