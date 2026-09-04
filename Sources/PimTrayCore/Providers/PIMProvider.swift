import Foundation

public protocol PIMProvider: Sendable {
    var kind: RoleScopeKind { get }
    /// Token scopes this provider needs; all against one resource.
    var scopes: [String] { get }
    func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole]
    func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment]
    func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy
    func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment
    func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws
    /// Withdraws a request that is still waiting for an approver.
    func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws
}
