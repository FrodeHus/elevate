import Foundation

/// PIM for Azure resource roles through Azure Resource Manager.
public struct AzureResourceProvider: PIMProvider {
    public let kind: RoleScopeKind = .azureResource
    public let scopes = ArmScopes.all
    let transport: GraphTransport

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        transport = GraphTransport(http: http, tokens: tokens, mapper: GraphTransport.mapArmError)
    }

    // MARK: Wire models

    struct Named: Decodable { let displayName: String?; let type: String?; let id: String? }
    struct Expanded: Decodable { let scope: Named?; let roleDefinition: Named? }
    struct Expiration: Decodable { let type: String?; let duration: String?; let endDateTime: Date? }
    struct ScheduleInfo: Decodable { let startDateTime: Date?; let expiration: Expiration? }
    struct Properties: Decodable {
        let scope: String
        let roleDefinitionId: String
        let principalId: String?
        let status: String?
        let assignmentType: String?
        let roleEligibilityScheduleId: String?
        let linkedRoleEligibilityScheduleId: String?
        let startDateTime: Date?
        let endDateTime: Date?
        let createdOn: Date?
        let scheduleInfo: ScheduleInfo?
        let expandedProperties: Expanded?
    }
    struct Instance: Decodable { let name: String; let id: String; let properties: Properties }
    struct Page<T: Decodable>: Decodable { let value: [T]; let nextLink: String? }

    static let pendingStatuses: Set<String> = ["PendingApproval", "PendingAdminDecision", "PendingApprovalProvisioning"]

    func armURL(_ path: String, apiVersion: String = "2020-10-01", query: [String: String] = [:]) throws -> URL {
        guard var components = URLComponents(url: GraphTransport.armBase.appendingPathComponent(path), resolvingAgainstBaseURL: false) else {
            throw PIMError.unexpected(status: 0, body: "Bad ARM path \(path)")
        }
        var items = [URLQueryItem(name: "api-version", value: apiVersion)]
        for (k, v) in query.sorted(by: { $0.key < $1.key }) { items.append(URLQueryItem(name: k, value: v)) }
        components.queryItems = items
        guard let url = components.url else { throw PIMError.unexpected(status: 0, body: "Bad ARM URL \(path)") }
        return url
    }

    /// GET every page of an ARM list, following `nextLink`.
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

    static func caption(_ e: Expanded?) -> String? {
        guard let scope = e?.scope, let name = scope.displayName else { return nil }
        let type: String?
        switch scope.type?.lowercased() {
        case "subscription": type = "subscription"
        case "resourcegroup": type = "resource group"
        case "managementgroup": type = "management group"
        case let other?: type = other
        case nil: type = nil
        }
        return type.map { "\(name) · \($0)" } ?? name
    }

    // MARK: Reads

    public func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] {
        let url = try armURL("providers/Microsoft.Authorization/roleEligibilityScheduleInstances", query: ["$filter": "asTarget()"])
        let items = try await listAll(Instance.self, identity: identity, tenantId: tenant.tenantId, url: url)
        var seen = Set<RoleScope>()
        var roles: [EligibleRole] = []
        for i in items {
            let scope = RoleScope.azureResource(scope: i.properties.scope, roleDefinitionId: i.properties.roleDefinitionId)
            guard seen.insert(scope).inserted else { continue }
            roles.append(EligibleRole(key: RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: scope),
                                      displayName: i.properties.expandedProperties?.roleDefinition?.displayName ?? i.properties.roleDefinitionId,
                                      detail: Self.caption(i.properties.expandedProperties),
                                      source: .discovered, policy: .manualDefault))
        }
        return roles.sorted { ($0.displayName, $0.detail ?? "") < ($1.displayName, $1.detail ?? "") }
    }

    public func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] {
        let instances = try await listAll(Instance.self, identity: identity, tenantId: tenant.tenantId,
                                          url: try armURL("providers/Microsoft.Authorization/roleAssignmentScheduleInstances", query: ["$filter": "asTarget()"]))
        let requests = try await listAll(Instance.self, identity: identity, tenantId: tenant.tenantId,
                                         url: try armURL("providers/Microsoft.Authorization/roleAssignmentScheduleRequests", query: ["$filter": "asTarget()"]))
        var result: [RoleKey: ActiveAssignment] = [:]
        for i in instances where i.properties.assignmentType == "Activated" {
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: .azureResource(scope: i.properties.scope, roleDefinitionId: i.properties.roleDefinitionId))
            result[key] = ActiveAssignment(roleKey: key, assignmentId: i.name, startDateTime: i.properties.startDateTime ?? .now,
                                           endDateTime: i.properties.endDateTime, status: .active)
        }
        for r in requests where Self.pendingStatuses.contains(r.properties.status ?? "") {
            let key = RoleKey(identityId: identity.id, tenantId: tenant.tenantId, scope: .azureResource(scope: r.properties.scope, roleDefinitionId: r.properties.roleDefinitionId))
            guard result[key] == nil else { continue }
            result[key] = ActiveAssignment(roleKey: key, assignmentId: r.name,
                                           startDateTime: r.properties.scheduleInfo?.startDateTime ?? r.properties.createdOn ?? .now,
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
        struct ApprovalSetting: Decodable { let isApprovalRequired: Bool? }
    }
    struct PolicyProperties: Decodable { let roleDefinitionId: String?; let effectiveRules: [PolicyRule]? }
    struct PolicyAssignment: Decodable { let name: String; let properties: PolicyProperties }

    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy {
        guard case .azureResource(let scope, let roleDefinitionId) = role.key.scope else { throw PIMError.notEligible }
        let url = try armURL(scope.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/providers/Microsoft.Authorization/roleManagementPolicyAssignments")
        let assignments = try await listAll(PolicyAssignment.self, identity: identity, tenantId: role.key.tenantId, url: url)
        guard let match = assignments.first(where: { $0.properties.roleDefinitionId?.caseInsensitiveCompare(roleDefinitionId) == .orderedSame }),
              let rules = match.properties.effectiveRules else { return .manualDefault }
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
            default: break
            }
        }
        return policy
    }

    // MARK: Activation

    struct RoleDefinition: Decodable { let id: String; let properties: Props; struct Props: Decodable { let roleName: String? } }

    /// OData string literals escape a single quote by doubling it.
    static func odataEscaped(_ value: String) -> String { value.replacingOccurrences(of: "'", with: "''") }

    /// Manual roles carry a role *name*; ARM wants the definition id at that scope.
    func resolveRoleDefinitionId(_ nameOrId: String, scope: String, identity: Identity, tenantId: String) async throws -> String {
        if nameOrId.contains("/") { return nameOrId }
        let url = try armURL(scope.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/providers/Microsoft.Authorization/roleDefinitions",
                             apiVersion: "2022-04-01", query: ["$filter": "roleName eq '\(Self.odataEscaped(nameOrId))'"])
        let defs = try await listAll(RoleDefinition.self, identity: identity, tenantId: tenantId, url: url)
        guard let id = defs.first?.id else { throw PIMError.notEligible }
        return id
    }

    /// Finds the caller's eligibility for a scope + role; ARM needs its principal id and schedule name to activate.
    func eligibility(scope: String, roleDefinitionId: String, identity: Identity, tenantId: String) async throws -> (principalId: String, scheduleName: String) {
        let url = try armURL("providers/Microsoft.Authorization/roleEligibilityScheduleInstances", query: ["$filter": "asTarget()"])
        let items = try await listAll(Instance.self, identity: identity, tenantId: tenantId, url: url)
        guard let match = items.first(where: {
            $0.properties.scope.caseInsensitiveCompare(scope) == .orderedSame &&
            $0.properties.roleDefinitionId.caseInsensitiveCompare(roleDefinitionId) == .orderedSame
        }), let principal = match.properties.principalId, let schedule = match.properties.roleEligibilityScheduleId else {
            throw PIMError.notEligible
        }
        return (principal, schedule.components(separatedBy: "/").last ?? schedule)
    }

    /// The principal to put in a Self* request: always the caller. An eligibility inherited
    /// through a group carries the *group's* principal id, and ARM refuses a request naming it
    /// ("The requestor … does not have permissions for this request"). The caller's object id
    /// in this tenant comes from the ARM token; an opaque token falls back to the eligibility's id.
    func requestPrincipalId(eligibilityPrincipalId: String, identity: Identity, tenantId: String) async -> String {
        guard let token = try? await transport.tokens.accessToken(identity: identity, tenantId: tenantId, scopes: scopes),
              let oid = AccessTokenClaims.objectId(token) else { return eligibilityPrincipalId }
        return oid
    }

    func requestURL(scope: String) throws -> URL {
        try armURL(scope.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/" + UUID().uuidString.lowercased())
    }

    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        guard case .azureResource(let scope, let nameOrId) = request.roleKey.scope else { throw PIMError.notEligible }
        let tenantId = request.roleKey.tenantId
        let roleDefinitionId = try await resolveRoleDefinitionId(nameOrId, scope: scope, identity: identity, tenantId: tenantId)
        let elig = try await eligibility(scope: scope, roleDefinitionId: roleDefinitionId, identity: identity, tenantId: tenantId)
        let principalId = await requestPrincipalId(eligibilityPrincipalId: elig.principalId, identity: identity, tenantId: tenantId)
        var props: [String: Any] = [
            "principalId": principalId,
            "roleDefinitionId": roleDefinitionId,
            "requestType": "SelfActivate",
            "linkedRoleEligibilityScheduleId": elig.scheduleName,
            "justification": request.justification,
            "scheduleInfo": [
                "startDateTime": GraphJSON.encoderDateString(.now),
                "expiration": ["type": "AfterDuration", "duration": ISO8601Duration.format(request.duration)],
            ],
        ]
        if let t = request.ticket { props["ticketInfo"] = ["ticketNumber": t.number, "ticketSystem": t.system] }
        let body = try JSONSerialization.data(withJSONObject: ["properties": props])
        let r = try await transport.put(identity: identity, tenantId: tenantId, url: try requestURL(scope: scope), scopes: scopes, body: body)
        let created = try GraphJSON.decoder.decode(Instance.self, from: r.body)
        let start = created.properties.scheduleInfo?.startDateTime ?? .now
        let end = created.properties.scheduleInfo?.expiration?.endDateTime
            ?? created.properties.scheduleInfo?.expiration?.duration.flatMap(ISO8601Duration.parse).map { start.addingTimeInterval(TimeInterval($0.components.seconds)) }
            ?? start.addingTimeInterval(TimeInterval(request.duration.components.seconds))
        let status: ActiveAssignment.Status = switch created.properties.status ?? "Provisioned" {
        case "PendingApproval", "PendingAdminDecision", "PendingApprovalProvisioning": .pendingApproval
        case "PendingProvisioning", "PendingScheduleCreation", "ScheduleCreated", "Accepted", "PendingEvaluation", "ProvisioningStarted", "PendingExternalProvisioning": .pendingProvisioning
        case "Denied", "Failed", "Canceled", "Revoked", "TimedOut", "Invalid", "AdminDenied", "FailedAsResourceIsLocked": .failed(created.properties.status ?? "Failed")
        default: .active
        }
        // A manual role is keyed by role name; key the assignment by the id ARM resolved it to.
        let resolvedKey = RoleKey(identityId: request.roleKey.identityId, tenantId: tenantId,
                                  scope: .azureResource(scope: scope, roleDefinitionId: roleDefinitionId))
        return ActiveAssignment(roleKey: resolvedKey, assignmentId: created.name, startDateTime: start,
                                endDateTime: status == .active ? end : nil, status: status)
    }

    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard case .azureResource(let scope, let nameOrId) = assignment.roleKey.scope else { throw PIMError.notEligible }
        let tenantId = assignment.roleKey.tenantId
        let roleDefinitionId = try await resolveRoleDefinitionId(nameOrId, scope: scope, identity: identity, tenantId: tenantId)
        let elig = try await eligibility(scope: scope, roleDefinitionId: roleDefinitionId, identity: identity, tenantId: tenantId)
        let principalId = await requestPrincipalId(eligibilityPrincipalId: elig.principalId, identity: identity, tenantId: tenantId)
        let props: [String: Any] = [
            "principalId": principalId,
            "roleDefinitionId": roleDefinitionId,
            "requestType": "SelfDeactivate",
            "linkedRoleEligibilityScheduleId": elig.scheduleName,
        ]
        _ = try await transport.put(identity: identity, tenantId: tenantId, url: try requestURL(scope: scope), scopes: scopes,
                                    body: try JSONSerialization.data(withJSONObject: ["properties": props]))
    }

    public func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws {
        guard case .azureResource(let scope, _) = assignment.roleKey.scope, let name = assignment.assignmentId else { throw PIMError.notEligible }
        let url = try armURL(scope.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/\(name)/cancel")
        _ = try await transport.post(identity: identity, tenantId: assignment.roleKey.tenantId, url: url, scopes: scopes, body: Data())
    }
}
