import Foundation

/// PIM for Groups through Microsoft Graph: eligibility for, and activation of, group membership or ownership.
public struct GroupProvider: PIMProvider {
    public let kind: RoleScopeKind = .group
    public let scopes = GroupScopes.all
    let transport: GraphTransport

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        transport = GraphTransport(http: http, tokens: tokens)
    }

    // MARK: Wire models

    struct GroupRef: Decodable { let id: String?; let displayName: String? }
    struct Instance: Decodable {
        let id: String
        let principalId: String?
        let groupId: String
        let accessId: String
        let memberType: String?
        let assignmentType: String?
        let startDateTime: Date?
        let endDateTime: Date?
        let group: GroupRef?
    }
    struct Expiration: Decodable { let type: String?; let duration: String?; let endDateTime: Date? }
    struct ScheduleInfo: Decodable { let startDateTime: Date?; let expiration: Expiration? }
    struct ScheduleRequest: Decodable {
        let id: String
        let status: String
        let groupId: String
        let accessId: String
        let createdDateTime: Date?
        let scheduleInfo: ScheduleInfo?
    }
    struct Page<T: Decodable>: Decodable {
        let value: [T]
        let nextLink: String?
        enum CodingKeys: String, CodingKey { case value; case nextLink = "@odata.nextLink" }
    }

    static let base = "/identityGovernance/privilegedAccess/group"

    func url(_ path: String) throws -> URL {
        if let u = URL(string: GraphTransport.graphBase.absoluteString + path) { return u }
        let encoded = path.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? path
        guard let u = URL(string: GraphTransport.graphBase.absoluteString + encoded) else {
            throw PIMError.unexpected(status: 0, body: "Bad URL")
        }
        return u
    }

    /// GET every page, following `@odata.nextLink`.
    func listAll<T: Decodable>(_ type: T.Type, identity: Identity, tenantId: String, url: URL) async throws -> [T] {
        var next: URL? = url
        var out: [T] = []
        while let current = next {
            let r = try await transport.get(identity: identity, tenantId: tenantId, url: current, scopes: scopes)
            let page = try GraphJSON.decoder.decode(Page<T>.self, from: r.body)
            out += page.value
            next = page.nextLink.flatMap(URL.init(string:))
        }
        return out
    }

    static func access(_ raw: String) -> GroupAccess { raw.caseInsensitiveCompare("owner") == .orderedSame ? .owner : .member }
    static func isGroupMember(_ memberType: String?) -> Bool { memberType?.caseInsensitiveCompare("group") == .orderedSame }

    // MARK: Reads

    public func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] {
        let items = try await listAll(Instance.self, identity: identity, tenantId: tenant.tenantId,
                                      url: try url("\(Self.base)/eligibilityScheduleInstances/filterByCurrentUser(on='principal')?$expand=group($select=id,displayName)"))
        var seen = Set<RoleScope>()
        var roles: [EligibleRole] = []
        for i in items {
            let access = Self.access(i.accessId)
            let scope = RoleScope.group(groupId: i.groupId, accessId: access)
            guard seen.insert(scope).inserted else { continue }
            roles.append(EligibleRole(key: RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: scope),
                                      displayName: i.group?.displayName ?? i.groupId,
                                      detail: access == .owner ? "owner" : "member",
                                      source: .discovered, policy: .manualDefault,
                                      viaGroup: Self.isGroupMember(i.memberType) ? "group" : nil))
        }
        return roles.sorted { ($0.displayName, $0.detail ?? "") < ($1.displayName, $1.detail ?? "") }
    }

    public func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] {
        let instances = try await listAll(Instance.self, identity: identity, tenantId: tenant.tenantId,
                                          url: try url("\(Self.base)/assignmentScheduleInstances/filterByCurrentUser(on='principal')?$expand=group($select=id,displayName)"))
        let requests = try await listAll(ScheduleRequest.self, identity: identity, tenantId: tenant.tenantId,
                                         url: try url("\(Self.base)/assignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter=status eq 'PendingApproval'"))
        var result: [RoleKey: ActiveAssignment] = [:]
        for i in instances where i.assignmentType?.caseInsensitiveCompare("activated") == .orderedSame {
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: .group(groupId: i.groupId, accessId: Self.access(i.accessId)))
            result[key] = ActiveAssignment(roleKey: key, assignmentId: i.id, startDateTime: i.startDateTime ?? .now,
                                           endDateTime: i.endDateTime, status: .active)
        }
        for r in requests where r.status == "PendingApproval" {
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: .group(groupId: r.groupId, accessId: Self.access(r.accessId)))
            guard result[key] == nil else { continue }
            result[key] = ActiveAssignment(roleKey: key, assignmentId: r.id,
                                           startDateTime: r.scheduleInfo?.startDateTime ?? r.createdDateTime ?? .now,
                                           endDateTime: nil, status: .pendingApproval)
        }
        return Array(result.values)
    }

    // MARK: Policy

    struct PolicyRule: Decodable {
        let id: String
        let maximumDuration: String?
        let enabledRules: [String]?
        let setting: ApprovalSetting?
        let isEnabled: Bool?
        let claimValue: String?
        struct ApprovalSetting: Decodable { let isApprovalRequired: Bool? }
    }
    struct Policy: Decodable { let id: String; let rules: [PolicyRule]? }
    struct PolicyAssignment: Decodable { let id: String; let roleDefinitionId: String?; let policy: Policy? }

    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy {
        guard case .group(let groupId, let access) = role.key.scope else { throw PIMError.notEligible }
        let filter = "scopeId eq '\(groupId)' and scopeType eq 'Group' and roleDefinitionId eq '\(access == .owner ? "owner" : "member")'"
        let encoded = filter.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? filter
        let r = try await transport.get(identity: identity, tenantId: role.key.tenantId,
                                        url: try url("/policies/roleManagementPolicyAssignments?$filter=\(encoded)&$expand=policy($expand=rules)"),
                                        scopes: scopes)
        let assignments = try GraphJSON.decoder.decode(Page<PolicyAssignment>.self, from: r.body).value
        guard let rules = assignments.first?.policy?.rules else { return .manualDefault }
        var policy = RolePolicy.manualDefault
        for rule in rules {
            switch rule.id {
            case "Expiration_EndUser_Assignment":
                if let d = rule.maximumDuration.flatMap(ISO8601Duration.parse) { policy.maximumDuration = d; policy.defaultDuration = d }
            case "Enablement_EndUser_Assignment":
                let enabled = Set(rule.enabledRules ?? [])
                policy.requiresJustification = enabled.contains("Justification")
                policy.requiresTicket = enabled.contains("Ticketing")
                policy.requiresMFA = enabled.contains("MultiFactorAuthentication")
            case "Approval_EndUser_Assignment":
                policy.requiresApproval = rule.setting?.isApprovalRequired ?? false
            case "AuthenticationContext_EndUser_Assignment":
                if rule.isEnabled == true, let claim = rule.claimValue, !claim.isEmpty { policy.authenticationContext = claim }
            default: break
            }
        }
        return policy
    }

    // MARK: Activate / deactivate

    /// The eligibility's own principal id (a group when inherited), used only when the token hides the caller's oid.
    func eligibilityPrincipalId(groupId: String, access: GroupAccess, identity: Identity, tenantId: String) async throws -> String? {
        let items = try await listAll(Instance.self, identity: identity, tenantId: tenantId,
                                      url: try url("\(Self.base)/eligibilityScheduleInstances/filterByCurrentUser(on='principal')?$expand=group($select=id,displayName)"))
        return items.first { $0.groupId == groupId && Self.access($0.accessId) == access }?.principalId
    }

    /// Always the caller: an eligibility inherited through another group names that group, which Graph refuses.
    func requestPrincipalId(groupId: String, access: GroupAccess, identity: Identity, tenantId: String) async throws -> String {
        if let token = try? await transport.tokens.accessToken(identity: identity, tenantId: tenantId, scopes: scopes),
           let oid = AccessTokenClaims.objectId(token) { return oid }
        guard let fallback = try await eligibilityPrincipalId(groupId: groupId, access: access, identity: identity, tenantId: tenantId) else {
            throw PIMError.notEligible
        }
        return fallback
    }

    static func status(_ raw: String) -> ActiveAssignment.Status {
        switch raw {
        case "PendingApproval", "PendingAdminDecision": .pendingApproval
        case "PendingProvisioning", "PendingScheduleCreation", "ScheduleCreated": .pendingProvisioning
        case "Denied", "Failed", "Canceled", "Revoked": .failed(raw)
        default: .active
        }
    }

    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        guard case .group(let groupId, let access) = request.roleKey.scope else { throw PIMError.notEligible }
        let tenantId = request.roleKey.tenantId
        let principal = try await requestPrincipalId(groupId: groupId, access: access, identity: identity, tenantId: tenantId)
        var body: [String: Any] = [
            "action": "selfActivate",
            "principalId": principal,
            "groupId": groupId,
            "accessId": access == .owner ? "owner" : "member",
            "justification": request.justification,
            "scheduleInfo": [
                "startDateTime": GraphJSON.encoderDateString(.now),
                "expiration": ["type": "afterDuration", "duration": ISO8601Duration.format(request.duration)],
            ],
        ]
        if let t = request.ticket { body["ticketInfo"] = ["ticketNumber": t.number, "ticketSystem": t.system] }
        let r = try await transport.post(identity: identity, tenantId: tenantId,
                                         url: try url("\(Self.base)/assignmentScheduleRequests"),
                                         scopes: scopes, body: try JSONSerialization.data(withJSONObject: body))
        let created = try GraphJSON.decoder.decode(ScheduleRequest.self, from: r.body)
        let start = created.scheduleInfo?.startDateTime ?? .now
        let end = created.scheduleInfo?.expiration?.endDateTime
            ?? created.scheduleInfo?.expiration?.duration.flatMap(ISO8601Duration.parse).map { start.addingTimeInterval(TimeInterval($0.components.seconds)) }
            ?? start.addingTimeInterval(TimeInterval(request.duration.components.seconds))
        let status = Self.status(created.status)
        return ActiveAssignment(roleKey: request.roleKey, assignmentId: created.id, startDateTime: start,
                                endDateTime: status == .active ? end : nil, status: status)
    }

    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard case .group(let groupId, let access) = assignment.roleKey.scope else { throw PIMError.notEligible }
        let tenantId = assignment.roleKey.tenantId
        let principal = try await requestPrincipalId(groupId: groupId, access: access, identity: identity, tenantId: tenantId)
        let body: [String: Any] = [
            "action": "selfDeactivate",
            "principalId": principal,
            "groupId": groupId,
            "accessId": access == .owner ? "owner" : "member",
        ]
        _ = try await transport.post(identity: identity, tenantId: tenantId,
                                     url: try url("\(Self.base)/assignmentScheduleRequests"),
                                     scopes: scopes, body: try JSONSerialization.data(withJSONObject: body))
    }

    /// Withdraws a request still awaiting approval. Graph answers 204 with no body.
    public func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard let requestId = assignment.assignmentId else { throw PIMError.notEligible }
        _ = try await transport.post(identity: identity, tenantId: assignment.roleKey.tenantId,
                                     url: try url("\(Self.base)/assignmentScheduleRequests/\(requestId)/cancel"),
                                     scopes: scopes, body: Data())
    }
}
