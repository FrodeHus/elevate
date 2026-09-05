import Foundation

/// Reads the requests awaiting the signed-in user's approval, and sends the decision.
public protocol ApprovalProvider: Sendable {
    var kind: RoleScopeKind { get }
    /// Token scopes this provider needs; all against one resource.
    var scopes: [String] { get }
    func pendingApprovals(identity: Identity, tenant: TenantContext) async throws -> [ApprovalRequest]
    func decide(_ request: ApprovalRequest, approve: Bool, justification: String, identity: Identity) async throws
}
