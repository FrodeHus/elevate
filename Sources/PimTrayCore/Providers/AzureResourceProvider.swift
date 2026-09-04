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

    // Task 4 replaces these.
    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy {
        throw PIMError.unexpected(status: 501, body: "policy: Task 4")
    }
    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        throw PIMError.unexpected(status: 501, body: "activate: Task 4")
    }
    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        throw PIMError.unexpected(status: 501, body: "deactivate: Task 4")
    }
    public func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws {
        throw PIMError.unexpected(status: 501, body: "cancel: Task 4")
    }
}
