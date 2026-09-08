import Foundation

/// Self-service entitlement management for the signed-in user: the access packages they may
/// request, their own requests and assignments. Graph v1.0 only.
public struct AccessPackageProvider: Sendable {
    public let scopes = EntitlementScopes.all
    let transport: GraphTransport

    public init(http: any HTTPClient, tokens: any TokenProviding) {
        transport = GraphTransport(http: http, tokens: tokens)
    }

    static let base = "/identityGovernance/entitlementManagement"

    // MARK: Wire shapes

    struct Named: Decodable { let id: String?; let displayName: String? }
    struct PackageDTO: Decodable { let id: String; let displayName: String?; let description: String?; let isHidden: Bool? }
    struct AssignmentRef: Decodable { let id: String?; let accessPackageId: String?; let assignmentPolicyId: String? }
    struct RequestDTO: Decodable {
        let id: String
        let requestType: String?
        let state: String?
        let status: String?
        let justification: String?
        let createdDateTime: Date?
        let completedDateTime: Date?
        let accessPackage: Named?
        let assignment: AssignmentRef?
    }
    struct Expiration: Decodable { let type: String?; let endDateTime: Date? }
    struct Schedule: Decodable { let startDateTime: Date?; let expiration: Expiration? }
    struct AssignmentDTO: Decodable {
        let id: String
        let state: String?
        let status: String?
        let schedule: Schedule?
        let accessPackage: Named?
        let assignmentPolicy: Named?
    }

    static func request(from r: RequestDTO) -> AccessPackageRequest {
        AccessPackageRequest(id: r.id,
                             packageId: r.accessPackage?.id ?? r.assignment?.accessPackageId ?? "",
                             packageName: r.accessPackage?.displayName ?? r.id,
                             requestType: r.requestType ?? "userAdd",
                             state: AccessPackageRequestState.parse(r.state),
                             status: r.status, justification: r.justification,
                             createdAt: r.createdDateTime, completedAt: r.completedDateTime,
                             policyId: r.assignment?.assignmentPolicyId)
    }

    static func assignment(from a: AssignmentDTO) -> AccessPackageAssignment {
        AccessPackageAssignment(id: a.id,
                                packageId: a.accessPackage?.id ?? "",
                                packageName: a.accessPackage?.displayName ?? a.id,
                                state: AccessPackageAssignmentState.parse(a.state),
                                policyName: a.assignmentPolicy?.displayName,
                                expiresAt: a.schedule?.expiration?.endDateTime)
    }

    // MARK: Reads

    public func requestablePackages(identity: Identity, tenantId: String) async throws -> [AccessPackage] {
        let url = try transport.graphURL("\(Self.base)/accessPackages/filterByCurrentUser(on='allowedRequestor')")
        let items = try await transport.listAll(PackageDTO.self, identity: identity, tenantId: tenantId, url: url, scopes: scopes)
        return items.map { AccessPackage(id: $0.id, displayName: $0.displayName ?? $0.id, description: $0.description, isHidden: $0.isHidden ?? false) }
    }

    public func myRequests(identity: Identity, tenantId: String) async throws -> [AccessPackageRequest] {
        let url = try transport.graphURL("\(Self.base)/assignmentRequests/filterByCurrentUser(on='target')?$expand=accessPackage,assignment")
        let items = try await transport.listAll(RequestDTO.self, identity: identity, tenantId: tenantId, url: url, scopes: scopes)
        return items.map(Self.request(from:))
    }

    public func myAssignments(identity: Identity, tenantId: String) async throws -> [AccessPackageAssignment] {
        let url = try transport.graphURL("\(Self.base)/assignments/filterByCurrentUser(on='target')?$expand=accessPackage,assignmentPolicy")
        let items = try await transport.listAll(AssignmentDTO.self, identity: identity, tenantId: tenantId, url: url, scopes: scopes)
        return items.map(Self.assignment(from:))
    }
}
