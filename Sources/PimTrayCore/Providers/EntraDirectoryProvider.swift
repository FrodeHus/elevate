import Foundation

public struct EntraDirectoryProvider: PIMProvider {
    public let kind: RoleScopeKind = .entraDirectory
    public let scopes = GraphScopes.all
    let transport: GraphTransport

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        transport = GraphTransport(http: http, tokens: tokens)
    }

    // MARK: Wire models

    struct RoleDefinitionRef: Decodable { let id: String; let displayName: String? }
    struct Schedule: Decodable {
        let id: String
        let roleDefinitionId: String
        let directoryScopeId: String?
        let assignmentType: String?
        let startDateTime: Date?
        let endDateTime: Date?
        let roleDefinition: RoleDefinitionRef?
    }
    struct Expiration: Decodable { let type: String?; let duration: String?; let endDateTime: Date? }
    struct ScheduleInfo: Decodable { let startDateTime: Date?; let expiration: Expiration? }
    struct ScheduleRequest: Decodable {
        let id: String
        let status: String
        let roleDefinitionId: String
        let directoryScopeId: String?
        let createdDateTime: Date?
        let scheduleInfo: ScheduleInfo?
    }
    struct Collection<T: Decodable>: Decodable { let value: [T] }

    func url(_ path: String) -> URL {
        if let u = URL(string: GraphTransport.graphBase.absoluteString + path) {
            return u
        }
        let encoded = path.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? path
        return URL(string: GraphTransport.graphBase.absoluteString + encoded)!
    }

    // MARK: Reads

    public func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] {
        let r = try await transport.get(identity: identity, tenantId: tenant.tenantId,
                                        url: url("/roleManagement/directory/roleEligibilitySchedules/filterByCurrentUser(on='principal')?$expand=roleDefinition"),
                                        scopes: scopes)
        let items = try GraphJSON.decoder.decode(Collection<Schedule>.self, from: r.body).value
        var seen = Set<RoleScope>()
        var roles: [EligibleRole] = []
        for s in items {
            let scope = RoleScope.entraDirectory(roleDefinitionId: s.roleDefinitionId, directoryScopeId: s.directoryScopeId ?? "/")
            guard seen.insert(scope).inserted else { continue }
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: scope)
            roles.append(EligibleRole(key: key, displayName: s.roleDefinition?.displayName ?? s.roleDefinitionId,
                                      source: .discovered, policy: .manualDefault))
        }
        return roles.sorted { $0.displayName < $1.displayName }
    }

    public func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] {
        let instances = try await transport.get(identity: identity, tenantId: tenant.tenantId,
                                                url: url("/roleManagement/directory/roleAssignmentScheduleInstances/filterByCurrentUser(on='principal')?$expand=roleDefinition"),
                                                scopes: scopes)
        let requests = try await transport.get(identity: identity, tenantId: tenant.tenantId,
                                               url: url("/roleManagement/directory/roleAssignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter=status eq 'PendingApproval'"),
                                               scopes: scopes)
        let activated = try GraphJSON.decoder.decode(Collection<Schedule>.self, from: instances.body).value
            .filter { $0.assignmentType == "Activated" }
        let pending = try GraphJSON.decoder.decode(Collection<ScheduleRequest>.self, from: requests.body).value
            .filter { $0.status == "PendingApproval" }

        var result: [RoleKey: ActiveAssignment] = [:]
        for s in activated {
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId,
                              scope: .entraDirectory(roleDefinitionId: s.roleDefinitionId, directoryScopeId: s.directoryScopeId ?? "/"))
            result[key] = ActiveAssignment(roleKey: key, assignmentId: s.id, startDateTime: s.startDateTime ?? .now,
                                           endDateTime: s.endDateTime, status: .active)
        }
        for p in pending {
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId,
                              scope: .entraDirectory(roleDefinitionId: p.roleDefinitionId, directoryScopeId: p.directoryScopeId ?? "/"))
            guard result[key] == nil else { continue }
            result[key] = ActiveAssignment(roleKey: key, assignmentId: p.id,
                                           startDateTime: p.scheduleInfo?.startDateTime ?? p.createdDateTime ?? .now,
                                           endDateTime: nil, status: .pendingApproval)
        }
        return Array(result.values)
    }

    // Policy, activate and deactivate are implemented in Task 6.
    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy {
        throw PIMError.unexpected(status: 501, body: "policy: implemented in Task 6")
    }
    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        throw PIMError.unexpected(status: 501, body: "activate: implemented in Task 6")
    }
    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        throw PIMError.unexpected(status: 501, body: "deactivate: implemented in Task 6")
    }
}
