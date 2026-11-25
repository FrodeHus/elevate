import Foundation

/// PIM for app roles (preview) through Microsoft Graph beta: eligibility for, and activation of,
/// an enterprise application's app role. The API mirrors PIM for Groups under `/appRole`.
public struct AppRoleProvider: PIMProvider {
    public let kind: RoleScopeKind = .appRole
    public let scopes = AppRoleScopes.all
    let transport: GraphTransport

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        transport = GraphTransport(http: http, tokens: tokens)
    }

    // MARK: Wire models

    struct AppRoleRef: Decodable { let displayName: String?; let value: String? }
    struct ServicePrincipalRef: Decodable { let displayName: String? }
    struct Instance: Decodable {
        let id: String
        let principalId: String?
        let servicePrincipalId: String
        let appRoleId: String
        let memberType: String?
        let assignmentScheduleId: String?
        let assignmentType: String?
        let startDateTime: Date?
        let endDateTime: Date?
        let appRole: AppRoleRef?
        let servicePrincipal: ServicePrincipalRef?
    }
    struct ScheduleRequest: Decodable {
        let id: String
        let status: String
        let targetScheduleId: String?
        let action: String?
        let servicePrincipalId: String
        let appRoleId: String
        let createdDateTime: Date?
        let scheduleInfo: ScheduleInfo?
    }
    /// Only on the beta endpoint while the feature is in preview.
    static let base = "/identityGovernance/privilegedAccess/appRole"
    static let eligibilityPath = "\(base)/eligibilityScheduleInstances/filterByCurrentUser(on='principal')?$expand=servicePrincipal($select=id,displayName)"

    func url(_ path: String) throws -> URL { try transport.graphBetaURL(path) }

    /// A tenant without the preview (no licence, or not rolled out) refuses these reads. That is the
    /// normal case, not a fault, so it reads as "no app roles" instead of an error on every refresh.
    /// Only a refusal counts: a 5xx or an unreadable answer is a failed read and must surface, or a
    /// passing fault would blank the user's app roles.
    func listOrEmpty<T: Decodable & Sendable>(_ type: T.Type, identity: Identity, tenantId: String, path: String) async throws -> [T] {
        do {
            return try await transport.listAll(type, identity: identity, tenantId: tenantId, url: try url(path), scopes: scopes)
        } catch PIMError.consentRequired {
            return []
        } catch PIMError.forbidden {
            return []
        } catch PIMError.unexpected(let status, _) where status == 400 || status == 404 {
            return []
        }
    }

    // MARK: Reads

    public func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] {
        let items = try await listOrEmpty(Instance.self, identity: identity, tenantId: tenant.tenantId, path: Self.eligibilityPath)
        var seen = Set<RoleScope>()
        var roles: [EligibleRole] = []
        for i in items {
            let scope = RoleScope.appRole(servicePrincipalId: i.servicePrincipalId, appRoleId: i.appRoleId)
            guard seen.insert(scope).inserted else { continue }
            roles.append(EligibleRole(key: RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: scope),
                                      displayName: i.appRole?.displayName ?? i.appRole?.value ?? i.appRoleId,
                                      detail: i.servicePrincipal?.displayName ?? i.servicePrincipalId,
                                      source: .discovered, policy: .manualDefault,
                                      viaGroup: GroupProvider.isGroupMember(i.memberType) ? "group" : nil))
        }
        return roles.sorted { ($0.detail ?? "", $0.displayName) < ($1.detail ?? "", $1.displayName) }
    }

    public func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] {
        let instances = try await listOrEmpty(Instance.self, identity: identity, tenantId: tenant.tenantId,
                                              path: "\(Self.base)/assignmentScheduleInstances/filterByCurrentUser(on='principal')")
        let requests = try await listOrEmpty(ScheduleRequest.self, identity: identity, tenantId: tenant.tenantId,
                                             path: "\(Self.base)/assignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter=status eq 'PendingApproval' or status eq 'ScheduleCreated' or status eq 'Provisioned'")
        func key(_ servicePrincipalId: String, _ appRoleId: String) -> RoleKey {
            RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: .appRole(servicePrincipalId: servicePrincipalId, appRoleId: appRoleId))
        }
        var result: [RoleKey: ActiveAssignment] = [:]
        // A standing ("assigned") app role is not ours to deactivate, so only activations are listed.
        for i in instances where i.assignmentType?.caseInsensitiveCompare("activated") == .orderedSame {
            let key = key(i.servicePrincipalId, i.appRoleId)
            let originatingRequest = requests.first {
                i.assignmentScheduleId != nil && $0.targetScheduleId == i.assignmentScheduleId && $0.action == "selfActivate"
            }
            result[key] = ActiveAssignment(roleKey: key, assignmentId: i.id, startDateTime: i.startDateTime ?? .now,
                                           endDateTime: i.endDateTime, status: .active, scheduleId: i.assignmentScheduleId, activationRequestId: originatingRequest?.id)
        }
        for r in requests where r.status == "PendingApproval" {
            let key = key(r.servicePrincipalId, r.appRoleId)
            guard result[key] == nil else { continue }
            result[key] = ActiveAssignment(roleKey: key, assignmentId: r.id,
                                           startDateTime: r.scheduleInfo?.startDateTime ?? r.createdDateTime ?? .now,
                                           endDateTime: nil, status: .pendingApproval, scheduleId: r.targetScheduleId, activationRequestId: r.id)
        }
        for u in requests where !ScheduledStart.isSettledOrPending(u.status) {
            guard let start = u.scheduleInfo?.startDateTime, ScheduledStart.isFuture(start) else { continue }
            let key = key(u.servicePrincipalId, u.appRoleId)
            guard result[key] == nil else { continue }
            let end = ScheduledStart.end(explicit: u.scheduleInfo?.expiration?.endDateTime,
                                         duration: u.scheduleInfo?.expiration?.duration, start: start, fallback: nil)
            result[key] = ActiveAssignment(roleKey: key, assignmentId: u.id, startDateTime: start,
                                           endDateTime: end, status: .scheduled, scheduleId: u.targetScheduleId, activationRequestId: u.id)
        }
        return Array(result.values)
    }

    // MARK: Policy

    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy {
        guard case .appRole(let servicePrincipalId, let appRoleId) = role.key.scope else { throw PIMError.notEligible }
        let filter = "scopeId eq '\(GraphTransport.odataEscaped(servicePrincipalId))' and scopeType eq 'AppRole' and roleDefinitionId eq '\(GraphTransport.odataEscaped(appRoleId))'"
        let encoded = filter.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? filter
        let r = try await transport.get(identity: identity, tenantId: role.key.tenantId,
                                        url: try url("/policies/roleManagementPolicyAssignments?$filter=\(encoded)&$expand=policy($expand=rules)"),
                                        scopes: scopes)
        let assignments = try GraphJSON.decoder.decode(GraphTransport.Page<GroupProvider.PolicyAssignment>.self, from: r.body).value
        guard let rules = assignments.first?.policy?.rules else { return .manualDefault }
        return PolicyRules.apply(rules)
    }

    // MARK: Activate / deactivate

    /// Always the caller: an eligibility inherited through a group names that group, which Graph refuses.
    /// The eligibility's own principal is used only when the token hides the caller's oid.
    func requestPrincipalId(servicePrincipalId: String, appRoleId: String, identity: Identity, tenantId: String) async throws -> String {
        if let token = try? await transport.tokens.accessToken(identity: identity, tenantId: tenantId, scopes: scopes),
           let oid = AccessTokenClaims.objectId(token) { return oid }
        let items = try await transport.listAll(Instance.self, identity: identity, tenantId: tenantId, url: try url(Self.eligibilityPath), scopes: scopes)
        guard let fallback = items.first(where: { $0.servicePrincipalId == servicePrincipalId && $0.appRoleId == appRoleId })?.principalId else {
            throw PIMError.notEligible
        }
        return fallback
    }

    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        guard case .appRole(let servicePrincipalId, let appRoleId) = request.roleKey.scope else { throw PIMError.notEligible }
        let tenantId = request.roleKey.tenantId
        let principal = try await requestPrincipalId(servicePrincipalId: servicePrincipalId, appRoleId: appRoleId, identity: identity, tenantId: tenantId)
        var body: [String: Any] = [
            "action": "selfActivate",
            "principalId": principal,
            "servicePrincipalId": servicePrincipalId,
            "appRoleId": appRoleId,
            "justification": request.justification,
            "scheduleInfo": [
                "startDateTime": GraphJSON.encoderDateString(request.startDateTime ?? .now),
                "expiration": ["type": "afterDuration", "duration": ISO8601Duration.format(request.duration)],
            ],
        ]
        if let t = request.ticket { body["ticketInfo"] = ["ticketNumber": t.number, "ticketSystem": t.system] }
        let r = try await transport.post(identity: identity, tenantId: tenantId,
                                         url: try url("\(Self.base)/assignmentScheduleRequests"),
                                         scopes: scopes, body: try JSONSerialization.data(withJSONObject: body))
        let created = try GraphJSON.decoder.decode(ScheduleRequest.self, from: r.body)
        let start = ScheduledStart.effective(response: created.scheduleInfo?.startDateTime, requested: request.startDateTime)
        let end = ScheduledStart.end(explicit: created.scheduleInfo?.expiration?.endDateTime,
                                     duration: created.scheduleInfo?.expiration?.duration, start: start, fallback: request.duration)
        // A future start only masks an outcome that would otherwise read as active; pending/failed still win.
        let reported = GroupProvider.status(created.status)
        let status: ActiveAssignment.Status = (reported == .active && ScheduledStart.isFuture(start)) ? .scheduled : reported
        return ActiveAssignment(roleKey: request.roleKey, assignmentId: created.id, startDateTime: start,
                                endDateTime: status == .active || status == .scheduled ? end : nil, status: status, scheduleId: created.targetScheduleId, activationRequestId: created.id)
    }

    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard case .appRole(let servicePrincipalId, let appRoleId) = assignment.roleKey.scope else { throw PIMError.notEligible }
        let tenantId = assignment.roleKey.tenantId
        let principal = try await requestPrincipalId(servicePrincipalId: servicePrincipalId, appRoleId: appRoleId, identity: identity, tenantId: tenantId)
        var body: [String: Any] = [
            "action": "selfDeactivate",
            "principalId": principal,
            "servicePrincipalId": servicePrincipalId,
            "appRoleId": appRoleId,
        ]
        if let scheduleId = assignment.scheduleId { body["targetScheduleId"] = scheduleId }
        let response = try await transport.post(identity: identity, tenantId: tenantId,
                                                url: try url("\(Self.base)/assignmentScheduleRequests"),
                                                scopes: scopes, body: try JSONSerialization.data(withJSONObject: body))
        let outcome = try GraphJSON.decoder.decode(ScheduleRequest.self, from: response.body).status
        guard outcome == "Provisioned" || outcome == "Revoked" else {
            throw PIMError.unexpected(status: 0, body: "Deactivation has not completed: \(outcome)")
        }
    }

    /// An app role reaches the app as a `roles` claim in a token minted for that app, which
    /// Elevate never holds, so there is nothing here to observe.
    public func effectiveAccess(_ assignment: ActiveAssignment, identity: Identity) async throws -> EffectiveAccess {
        .unknown("An app role shows up in the application's own token, so Elevate cannot see when it takes effect.")
    }

    /// Withdraws a request still awaiting approval.
    public func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard let requestId = assignment.assignmentId else { throw PIMError.notEligible }
        _ = try await transport.post(identity: identity, tenantId: assignment.roleKey.tenantId,
                                     url: try url("\(Self.base)/assignmentScheduleRequests/\(requestId)/cancel"),
                                     scopes: scopes, body: Data())
    }
}
