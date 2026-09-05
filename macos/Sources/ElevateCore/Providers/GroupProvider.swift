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

    // MARK: Policy and writes (Task 4)

    public func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy { .manualDefault }
    public func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment { throw PIMError.notEligible }
    public func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws { throw PIMError.notEligible }
    public func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws { throw PIMError.notEligible }
}
