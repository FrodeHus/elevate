import Foundation

/// Phase 2. Azure Resource Manager PIM.
public struct AzureResourceProvider: PIMProvider {
    public let kind: RoleScopeKind = .azureResource
    public let scopes = ArmScopes.all
    public init() {}
    public func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] { [] }
    public func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] { [] }
    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy { .manualDefault }
    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        throw PIMError.unexpected(status: 501, body: "Azure resource roles arrive in phase 2")
    }
    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        throw PIMError.unexpected(status: 501, body: "Azure resource roles arrive in phase 2")
    }
}

/// Phase 3. PIM for Groups.
public struct GroupProvider: PIMProvider {
    public let kind: RoleScopeKind = .group
    public let scopes = GraphScopes.all
    public init() {}
    public func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] { [] }
    public func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] { [] }
    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy { .manualDefault }
    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        throw PIMError.unexpected(status: 501, body: "PIM for Groups arrives in phase 3")
    }
    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        throw PIMError.unexpected(status: 501, body: "PIM for Groups arrives in phase 3")
    }
}
