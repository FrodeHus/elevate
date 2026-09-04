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

    func url(_ path: String) throws -> URL {
        if let u = URL(string: GraphTransport.graphBase.absoluteString + path) {
            return u
        }
        let encoded = path.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? path
        guard let u = URL(string: GraphTransport.graphBase.absoluteString + encoded) else {
            throw PIMError.unexpected(status: 0, body: "Bad URL")
        }
        return u
    }

    // MARK: Reads

    public func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] {
        let r = try await transport.get(identity: identity, tenantId: tenant.tenantId,
                                        url: try url("/roleManagement/directory/roleEligibilitySchedules/filterByCurrentUser(on='principal')?$expand=roleDefinition"),
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
                                                url: try url("/roleManagement/directory/roleAssignmentScheduleInstances/filterByCurrentUser(on='principal')?$expand=roleDefinition"),
                                                scopes: scopes)
        let requests = try await transport.get(identity: identity, tenantId: tenant.tenantId,
                                               url: try url("/roleManagement/directory/roleAssignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter=status eq 'PendingApproval'"),
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

    // MARK: Policy

    struct PolicyRule: Decodable {
        let id: String
        let isExpirationRequired: Bool?
        let maximumDuration: String?
        let enabledRules: [String]?
        let setting: ApprovalSetting?
        struct ApprovalSetting: Decodable { let isApprovalRequired: Bool? }
    }
    struct Policy: Decodable { let id: String; let rules: [PolicyRule]? }
    struct PolicyAssignment: Decodable { let id: String; let roleDefinitionId: String?; let policy: Policy? }

    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy {
        guard case .entraDirectory(let roleDefinitionId, _) = role.key.scope else { throw PIMError.notEligible }
        let filter = "scopeId eq '/' and scopeType eq 'DirectoryRole' and roleDefinitionId eq '\(roleDefinitionId)'"
        let encoded = filter.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? filter
        let r = try await transport.get(identity: identity, tenantId: role.key.tenantId,
                                        url: try url("/policies/roleManagementPolicyAssignments?$filter=\(encoded)&$expand=policy($expand=rules)"),
                                        scopes: scopes)
        let assignments = try GraphJSON.decoder.decode(Collection<PolicyAssignment>.self, from: r.body).value
        guard let rules = assignments.first?.policy?.rules else { return .manualDefault }
        var policy = RolePolicy.manualDefault
        for rule in rules {
            switch rule.id {
            case "Expiration_EndUser_Assignment":
                if let d = rule.maximumDuration.flatMap(ISO8601Duration.parse) {
                    policy.maximumDuration = d
                    policy.defaultDuration = d
                }
            case "Enablement_EndUser_Assignment":
                let enabled = Set(rule.enabledRules ?? [])
                policy.requiresJustification = enabled.contains("Justification")
                policy.requiresTicket = enabled.contains("Ticketing")
                policy.requiresMFA = enabled.contains("MultiFactorAuthentication")
            case "Approval_EndUser_Assignment":
                policy.requiresApproval = rule.setting?.isApprovalRequired ?? false
            default:
                break
            }
        }
        return policy
    }

    // MARK: Activate / deactivate

    struct Me: Decodable { let id: String }

    func principalId(identity: Identity, tenantId: String) async throws -> String {
        let r = try await transport.get(identity: identity, tenantId: tenantId, url: try url("/me?$select=id"), scopes: scopes)
        return try GraphJSON.decoder.decode(Me.self, from: r.body).id
    }

    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        guard case .entraDirectory(let roleDefinitionId, let directoryScopeId) = request.roleKey.scope else { throw PIMError.notEligible }
        let principal = try await principalId(identity: identity, tenantId: request.roleKey.tenantId)
        var body: [String: Any] = [
            "action": "selfActivate",
            "principalId": principal,
            "roleDefinitionId": roleDefinitionId,
            "directoryScopeId": directoryScopeId,
            "justification": request.justification,
            "scheduleInfo": [
                "startDateTime": GraphJSON.encoderDateString(.now),
                "expiration": ["type": "afterDuration", "duration": ISO8601Duration.format(request.duration)],
            ],
        ]
        if let t = request.ticket {
            body["ticketInfo"] = ["ticketNumber": t.number, "ticketSystem": t.system]
        }
        let r = try await transport.post(identity: identity, tenantId: request.roleKey.tenantId,
                                         url: try url("/roleManagement/directory/roleAssignmentScheduleRequests"),
                                         scopes: scopes, body: try JSONSerialization.data(withJSONObject: body))
        let created = try GraphJSON.decoder.decode(ScheduleRequest.self, from: r.body)
        let start = created.scheduleInfo?.startDateTime ?? .now
        let end = created.scheduleInfo?.expiration?.endDateTime
            ?? created.scheduleInfo?.expiration?.duration.flatMap(ISO8601Duration.parse).map { start.addingTimeInterval(TimeInterval($0.components.seconds)) }
            ?? start.addingTimeInterval(TimeInterval(request.duration.components.seconds))
        let status: ActiveAssignment.Status = switch created.status {
        case "PendingApproval", "PendingAdminDecision": .pendingApproval
        case "PendingProvisioning", "PendingScheduleCreation", "ScheduleCreated": .pendingProvisioning
        case "Denied", "Failed", "Canceled", "Revoked": .failed(created.status)
        default: .active
        }
        return ActiveAssignment(roleKey: request.roleKey, assignmentId: created.id, startDateTime: start,
                                endDateTime: status == .active ? end : nil, status: status)
    }

    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard case .entraDirectory(let roleDefinitionId, let directoryScopeId) = assignment.roleKey.scope else { throw PIMError.notEligible }
        let principal = try await principalId(identity: identity, tenantId: assignment.roleKey.tenantId)
        let body: [String: Any] = [
            "action": "selfDeactivate",
            "principalId": principal,
            "roleDefinitionId": roleDefinitionId,
            "directoryScopeId": directoryScopeId,
        ]
        _ = try await transport.post(identity: identity, tenantId: assignment.roleKey.tenantId,
                                     url: try url("/roleManagement/directory/roleAssignmentScheduleRequests"),
                                     scopes: scopes, body: try JSONSerialization.data(withJSONObject: body))
    }

    /// Withdraws a request still awaiting approval. Graph answers 204 with no body.
    public func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard let requestId = assignment.assignmentId else { throw PIMError.notEligible }
        _ = try await transport.post(identity: identity, tenantId: assignment.roleKey.tenantId,
                                     url: try url("/roleManagement/directory/roleAssignmentScheduleRequests/\(requestId)/cancel"),
                                     scopes: scopes, body: Data())
    }
}
