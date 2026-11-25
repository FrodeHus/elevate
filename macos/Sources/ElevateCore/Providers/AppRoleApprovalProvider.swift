import Foundation

/// PIM for app roles (preview) activation requests awaiting the signed-in user's approval.
public struct AppRoleApprovalProvider: ApprovalProvider {
    public let kind: RoleScopeKind = .appRole
    public let scopes = AppRoleScopes.all
    let transport: GraphTransport

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        transport = GraphTransport(http: http, tokens: tokens)
    }

    struct Request: Decodable {
        let id: String
        let approvalId: String?
        let action: String?
        let principalId: String?
        let servicePrincipalId: String?
        let appRoleId: String?
        let justification: String?
        let createdDateTime: Date?
        let scheduleInfo: GraphApprovals.ScheduleInfo?
        let appRole: AppRoleProvider.AppRoleRef?
        let servicePrincipal: GraphApprovals.Named?
        let principal: GraphApprovals.Principal?
    }
    struct Approval: Decodable { let stages: [GraphApprovals.Step]? }

    static let approvalsPath = "\(AppRoleProvider.base)/assignmentApprovals"

    public func pendingApprovals(identity: Identity, tenant: TenantContext) async throws -> [ApprovalRequest] {
        let url = try transport.graphBetaURL("\(AppRoleProvider.base)/assignmentScheduleRequests/filterByCurrentUser(on='approver')?$filter=status eq 'PendingApproval'&$expand=servicePrincipal($select=id,displayName),principal")
        let items = try await transport.listAll(Request.self, identity: identity, tenantId: tenant.tenantId, url: url, scopes: scopes)
        return items.map { r in
            ApprovalRequest(id: r.id, tenantKey: tenant.id, kind: kind, action: GraphApprovals.action(r.action),
                            targetName: r.appRole?.displayName ?? r.appRole?.value ?? r.appRoleId ?? r.id,
                            scopeCaption: r.servicePrincipal?.displayName ?? r.servicePrincipalId,
                            requesterName: GraphApprovals.requester(r.principal, principalId: r.principalId),
                            justification: r.justification,
                            requestedDuration: r.scheduleInfo?.expiration?.duration.flatMap(ISO8601Duration.parse),
                            createdAt: r.createdDateTime, decisionRef: r.approvalId ?? r.id)
        }
    }

    /// The app role approval names its steps `stages` and serves them only as an expansion of the
    /// approval, unlike Groups and Entra (`/steps`).
    // shortcut: the PATCH is modelled on the Groups one and has not been run against a real pending
    // request (that needs a second account as approver). Verify it when one is available.
    public func decide(_ request: ApprovalRequest, approve: Bool, justification: String, identity: Identity) async throws {
        let approvalId = request.decisionRef ?? request.id
        let tenantId = request.tenantKey.tenantId
        let r = try await transport.get(identity: identity, tenantId: tenantId,
                                        url: try transport.graphBetaURL("\(Self.approvalsPath)/\(approvalId)?$expand=stages"), scopes: scopes)
        let stage = try GraphApprovals.step(try GraphJSON.decoder.decode(Approval.self, from: r.body).stages ?? [])
        _ = try await transport.patch(identity: identity, tenantId: tenantId,
                                      url: try transport.graphBetaURL("\(Self.approvalsPath)/\(approvalId)/stages/\(stage.id)"),
                                      scopes: scopes, body: try GraphApprovals.decisionBody(approve: approve, justification: justification))
    }
}
