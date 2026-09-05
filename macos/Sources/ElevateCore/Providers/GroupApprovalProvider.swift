import Foundation

/// PIM for Groups activation requests awaiting the signed-in user's approval.
public struct GroupApprovalProvider: ApprovalProvider {
    public let kind: RoleScopeKind = .group
    public let scopes = GroupScopes.all
    let transport: GraphTransport

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        transport = GraphTransport(http: http, tokens: tokens)
    }

    struct Request: Decodable {
        let id: String
        let action: String?
        let principalId: String?
        let groupId: String?
        let accessId: String?
        let justification: String?
        let createdDateTime: Date?
        let scheduleInfo: GraphApprovals.ScheduleInfo?
        let group: GraphApprovals.Named?
        let principal: GraphApprovals.Principal?
    }

    static let approvalsPath = "\(GroupProvider.base)/assignmentApprovals"

    public func pendingApprovals(identity: Identity, tenant: TenantContext) async throws -> [ApprovalRequest] {
        let url = try transport.graphURL("\(GroupProvider.base)/assignmentScheduleRequests/filterByCurrentUser(on='approver')?$filter=status eq 'PendingApproval'&$expand=group,principal")
        let items = try await transport.listAll(Request.self, identity: identity, tenantId: tenant.tenantId, url: url, scopes: scopes)
        return items.map { r in
            ApprovalRequest(id: r.id, tenantKey: tenant.id, kind: kind, action: GraphApprovals.action(r.action),
                            targetName: r.group?.displayName ?? r.groupId ?? r.id,
                            scopeCaption: r.accessId.map { GroupProvider.access($0) == .owner ? "owner" : "member" },
                            requesterName: GraphApprovals.requester(r.principal, principalId: r.principalId),
                            justification: r.justification,
                            requestedDuration: r.scheduleInfo?.expiration?.duration.flatMap(ISO8601Duration.parse),
                            createdAt: r.createdDateTime, decisionRef: r.id)
        }
    }

    public func decide(_ request: ApprovalRequest, approve: Bool, justification: String, identity: Identity) async throws {
        try await GraphApprovals.decide(request, approve: approve, justification: justification, identity: identity,
                                        transport: transport, scopes: scopes, approvalsPath: Self.approvalsPath)
    }
}
