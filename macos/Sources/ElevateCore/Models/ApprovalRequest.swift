import Foundation

/// One activation request waiting for the signed-in user's decision as an approver.
public struct ApprovalRequest: Codable, Hashable, Sendable, Identifiable {
    /// What the requester asked for. Only `activate` can be decided through the APIs;
    /// extend and renew requests are listed with a pointer to the portal.
    public enum Action: String, Codable, Sendable { case activate, extend, renew, other }

    /// Request id (Graph) or request name (ARM).
    public var id: String
    public var tenantKey: TenantKey
    public var kind: RoleScopeKind
    public var action: Action
    /// Role or group display name; falls back to the id when the service did not expand it.
    public var targetName: String
    /// Azure scope display, or "member"/"owner" for a group request.
    public var scopeCaption: String?
    /// Display name or UPN of the requester; falls back to their principal id.
    public var requesterName: String
    public var justification: String?
    public var requestedDuration: Duration?
    public var createdAt: Date?
    /// Graph: the approval id (equal to the request id). ARM: `properties.approvalId`.
    public var decisionRef: String?

    public init(id: String, tenantKey: TenantKey, kind: RoleScopeKind, action: Action,
                targetName: String, scopeCaption: String? = nil, requesterName: String,
                justification: String? = nil, requestedDuration: Duration? = nil,
                createdAt: Date? = nil, decisionRef: String? = nil) {
        self.id = id
        self.tenantKey = tenantKey
        self.kind = kind
        self.action = action
        self.targetName = targetName
        self.scopeCaption = scopeCaption
        self.requesterName = requesterName
        self.justification = justification
        self.requestedDuration = requestedDuration
        self.createdAt = createdAt
        self.decisionRef = decisionRef
    }
}

/// Which pending requests have not been notified about yet.
public enum ApprovalDiff {
    /// Requests in `current` whose id is not in `previousIds`, in input order.
    public static func newRequests(previousIds: Set<String>, current: [ApprovalRequest]) -> [ApprovalRequest] {
        current.filter { !previousIds.contains($0.id) }
    }
}
