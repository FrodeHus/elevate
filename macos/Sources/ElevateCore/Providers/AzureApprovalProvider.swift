import Foundation

/// Azure resource role activation requests awaiting the signed-in user's approval, through ARM.
public struct AzureApprovalProvider: ApprovalProvider {
    public let kind: RoleScopeKind = .azureResource
    public let scopes = ArmScopes.all
    let transport: GraphTransport

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        transport = GraphTransport(http: http, tokens: tokens, mapper: GraphTransport.mapArmError)
    }

    // MARK: Wire models

    struct RequestProperties: Decodable {
        let roleDefinitionId: String?
        let principalId: String?
        let requestType: String?
        let status: String?
        let approvalId: String?
        let justification: String?
        let createdOn: Date?
        let scheduleInfo: ScheduleInfo?
        let expandedProperties: AzureResourceProvider.Expanded?
    }
    struct Request: Decodable { let name: String; let properties: RequestProperties }
    struct Page<T: Decodable>: Decodable { let value: [T]; let nextLink: String? }
    struct StageProperties: Decodable { let reviewResult: String?; let status: String? }
    struct Stage: Decodable { let name: String?; let id: String?; let properties: StageProperties? }
    struct ApprovalProperties: Decodable { let stages: [Stage]? }
    struct Approval: Decodable { let properties: ApprovalProperties? }

    static let listAPIVersion = "2020-10-01"
    static let approvalAPIVersion = "2021-01-01-preview"

    // MARK: Reads

    public func pendingApprovals(identity: Identity, tenant: TenantContext) async throws -> [ApprovalRequest] {
        var next: URL? = try AzureResourceProvider.armURL("providers/Microsoft.Authorization/roleAssignmentScheduleRequests",
                                         apiVersion: Self.listAPIVersion, query: ["$filter": "asApprover()"])
        var items: [Request] = []
        while let current = next {
            let r = try await transport.get(identity: identity, tenantId: tenant.tenantId, url: current, scopes: scopes)
            let page = try GraphJSON.decoder.decode(Page<Request>.self, from: r.body)
            items += page.value
            next = page.nextLink.flatMap(URL.init(string:))
        }
        return items.compactMap { r -> ApprovalRequest? in
            guard r.properties.status == "PendingApproval" else { return nil }
            let expanded = r.properties.expandedProperties
            return ApprovalRequest(
                id: r.name, tenantKey: tenant.id, kind: kind, action: GraphApprovals.action(r.properties.requestType),
                targetName: expanded?.roleDefinition?.displayName ?? r.properties.roleDefinitionId ?? r.name,
                scopeCaption: AzureResourceProvider.caption(expanded),
                requesterName: expanded?.principal?.displayName ?? r.properties.principalId ?? "Unknown",
                justification: r.properties.justification,
                requestedDuration: r.properties.scheduleInfo?.expiration?.duration.flatMap(ISO8601Duration.parse),
                createdAt: r.properties.createdOn, decisionRef: r.properties.approvalId)
        }
    }

    // MARK: Decision

    /// The stage to decide: the one still awaiting review, else the first.
    static func stage(_ stages: [Stage]) throws -> String {
        let pick = stages.first { $0.properties?.reviewResult?.caseInsensitiveCompare("NotReviewed") == .orderedSame } ?? stages.first
        guard let name = pick?.name ?? pick?.id?.components(separatedBy: "/").last else {
            throw PIMError.unexpected(status: 0, body: "No approval step to decide")
        }
        return name
    }

    public func decide(_ request: ApprovalRequest, approve: Bool, justification: String, identity: Identity) async throws {
        guard let approvalId = request.decisionRef else {
            throw PIMError.unexpected(status: 0, body: "No approval step to decide")
        }
        let tenantId = request.tenantKey.tenantId
        let base = "providers/Microsoft.Authorization/roleAssignmentApprovals/\(approvalId)"
        let r = try await transport.get(identity: identity, tenantId: tenantId,
                                        url: try AzureResourceProvider.armURL(base, apiVersion: Self.approvalAPIVersion), scopes: scopes)
        let approval = try GraphJSON.decoder.decode(Approval.self, from: r.body)
        let stage = try Self.stage(approval.properties?.stages ?? [])
        let url = try AzureResourceProvider.armURL("\(base)/stages/\(stage)", apiVersion: Self.approvalAPIVersion)
        let decision = ["reviewResult": approve ? "Approve" : "Deny", "justification": justification]
        do {
            _ = try await transport.patch(identity: identity, tenantId: tenantId, url: url, scopes: scopes,
                                          body: try JSONSerialization.data(withJSONObject: decision))
        } catch PIMError.unexpected(status: 400, _) {
            // Some ARM versions want the decision wrapped in `properties`; the flat form the docs show comes first.
            _ = try await transport.patch(identity: identity, tenantId: tenantId, url: url, scopes: scopes,
                                          body: try JSONSerialization.data(withJSONObject: ["properties": decision]))
        }
    }
}
