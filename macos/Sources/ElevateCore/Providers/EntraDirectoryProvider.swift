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
        let memberType: String?
    }
    struct ScheduleRequest: Decodable {
        let id: String
        let status: String
        let roleDefinitionId: String
        let directoryScopeId: String?
        let createdDateTime: Date?
        let scheduleInfo: ScheduleInfo?
    }
    struct Collection<T: Decodable>: Decodable { let value: [T] }

    // MARK: Reads

    public func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] {
        let r = try await transport.get(identity: identity, tenantId: tenant.tenantId,
                                        url: try transport.graphURL("/roleManagement/directory/roleEligibilitySchedules/filterByCurrentUser(on='principal')?$expand=roleDefinition"),
                                        scopes: scopes)
        let items = try GraphJSON.decoder.decode(Collection<Schedule>.self, from: r.body).value
        var seen = Set<RoleScope>()
        var roles: [EligibleRole] = []
        for s in items {
            let scope = RoleScope.entraDirectory(roleDefinitionId: s.roleDefinitionId, directoryScopeId: s.directoryScopeId ?? "/")
            guard seen.insert(scope).inserted else { continue }
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: scope)
            let viaGroup: String? = s.memberType?.caseInsensitiveCompare("Group") == .orderedSame ? "group" : nil
            roles.append(EligibleRole(key: key, displayName: s.roleDefinition?.displayName ?? s.roleDefinitionId,
                                      source: .discovered, policy: .manualDefault, viaGroup: viaGroup))
        }
        return roles.sorted { $0.displayName < $1.displayName }
    }

    public func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] {
        let instances = try await transport.get(identity: identity, tenantId: tenant.tenantId,
                                                url: try transport.graphURL("/roleManagement/directory/roleAssignmentScheduleInstances/filterByCurrentUser(on='principal')?$expand=roleDefinition"),
                                                scopes: scopes)
        // Widened past PendingApproval so a booked-ahead request, which the service has already
        // turned into a schedule, is the source for the `.scheduled` rows below.
        let requests = try await transport.get(identity: identity, tenantId: tenant.tenantId,
                                               url: try transport.graphURL("/roleManagement/directory/roleAssignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter=status eq 'PendingApproval' or status eq 'ScheduleCreated' or status eq 'Provisioned'"),
                                               scopes: scopes)
        let activated = try GraphJSON.decoder.decode(Collection<Schedule>.self, from: instances.body).value
            .filter { $0.assignmentType == "Activated" }
        let all = try GraphJSON.decoder.decode(Collection<ScheduleRequest>.self, from: requests.body).value
        let pending = all.filter { $0.status == "PendingApproval" }

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
        for u in all where !ScheduledStart.isSettledOrPending(u.status) {
            guard let start = u.scheduleInfo?.startDateTime, ScheduledStart.isFuture(start) else { continue }
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId,
                              scope: .entraDirectory(roleDefinitionId: u.roleDefinitionId, directoryScopeId: u.directoryScopeId ?? "/"))
            guard result[key] == nil else { continue }
            let end = ScheduledStart.end(explicit: u.scheduleInfo?.expiration?.endDateTime,
                                         duration: u.scheduleInfo?.expiration?.duration, start: start, fallback: nil)
            result[key] = ActiveAssignment(roleKey: key, assignmentId: u.id, startDateTime: start,
                                           endDateTime: end, status: .scheduled)
        }
        return Array(result.values)
    }

    // MARK: Policy

    struct Policy: Decodable { let id: String; let rules: [PolicyRules.Rule]? }
    struct PolicyAssignment: Decodable { let id: String; let roleDefinitionId: String?; let policy: Policy? }

    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy {
        guard case .entraDirectory(let roleDefinitionId, _) = role.key.scope else { throw PIMError.notEligible }
        let filter = "scopeId eq '/' and scopeType eq 'DirectoryRole' and roleDefinitionId eq '\(roleDefinitionId)'"
        let encoded = filter.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? filter
        let r = try await transport.get(identity: identity, tenantId: role.key.tenantId,
                                        url: try transport.graphURL("/policies/roleManagementPolicyAssignments?$filter=\(encoded)&$expand=policy($expand=rules)"),
                                        scopes: scopes)
        let assignments = try GraphJSON.decoder.decode(Collection<PolicyAssignment>.self, from: r.body).value
        guard let rules = assignments.first?.policy?.rules else { return .manualDefault }
        return PolicyRules.apply(rules)
    }

    // MARK: Activate / deactivate

    struct Me: Decodable { let id: String }

    func principalId(identity: Identity, tenantId: String) async throws -> String {
        let r = try await transport.get(identity: identity, tenantId: tenantId, url: try transport.graphURL("/me?$select=id"), scopes: scopes)
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
                "startDateTime": GraphJSON.encoderDateString(request.startDateTime ?? .now),
                "expiration": ["type": "afterDuration", "duration": ISO8601Duration.format(request.duration)],
            ],
        ]
        if let t = request.ticket {
            body["ticketInfo"] = ["ticketNumber": t.number, "ticketSystem": t.system]
        }
        let r = try await transport.post(identity: identity, tenantId: request.roleKey.tenantId,
                                         url: try transport.graphURL("/roleManagement/directory/roleAssignmentScheduleRequests"),
                                         scopes: scopes, body: try JSONSerialization.data(withJSONObject: body))
        let created = try GraphJSON.decoder.decode(ScheduleRequest.self, from: r.body)
        let start = ScheduledStart.effective(response: created.scheduleInfo?.startDateTime, requested: request.startDateTime)
        let end = ScheduledStart.end(explicit: created.scheduleInfo?.expiration?.endDateTime,
                                     duration: created.scheduleInfo?.expiration?.duration, start: start, fallback: request.duration)
        let reported: ActiveAssignment.Status = switch created.status {
        case "PendingApproval", "PendingAdminDecision": .pendingApproval
        case "PendingProvisioning", "PendingScheduleCreation", "ScheduleCreated": .pendingProvisioning
        case "Denied", "Failed", "Canceled", "Revoked": .failed(created.status)
        default: .active
        }
        // A future start only masks an outcome that would otherwise read as active; pending/failed still win.
        let status: ActiveAssignment.Status = (reported == .active && ScheduledStart.isFuture(start)) ? .scheduled : reported
        return ActiveAssignment(roleKey: request.roleKey, assignmentId: created.id, startDateTime: start,
                                endDateTime: status == .active || status == .scheduled ? end : nil, status: status)
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
                                     url: try transport.graphURL("/roleManagement/directory/roleAssignmentScheduleRequests"),
                                     scopes: scopes, body: try JSONSerialization.data(withJSONObject: body))
    }

    /// Withdraws a request still awaiting approval. Graph answers 204 with no body.
    public func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard let requestId = assignment.assignmentId else { throw PIMError.notEligible }
        _ = try await transport.post(identity: identity, tenantId: assignment.roleKey.tenantId,
                                     url: try transport.graphURL("/roleManagement/directory/roleAssignmentScheduleRequests/\(requestId)/cancel"),
                                     scopes: scopes, body: Data())
    }
}
