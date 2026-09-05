import Testing
import Foundation
@testable import ElevateCore

@Suite struct AzureResourceProviderTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t1")
    let tenant = TenantContext(identityId: "id1", tenantId: "t1", displayName: "Contoso", source: .home)
    let contributorId = "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c"
    let readerId = "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7"

    func makeProvider() -> (AzureResourceProvider, StubHTTPClient, FakeTokenProvider) {
        let http = StubHTTPClient()
        let tokens = FakeTokenProvider()
        return (AzureResourceProvider(http: http, tokens: tokens), http, tokens)
    }

    @Test func listsEligibleRolesAcrossPagesWithScopeCaption() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        let roles = try await p.eligibleRoles(identity: identity, tenant: tenant)
        #expect(roles.map(\.displayName) == ["Contributor", "Reader"])
        #expect(roles[0].detail == "Pay-As-You-Go · subscription")
        #expect(roles[1].detail == "rg-ops · resource group")
        #expect(roles[0].key.scope == .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: contributorId))
        #expect(roles.allSatisfy { $0.source == .discovered && $0.key.tenantId == "t1" })
        let viaGroup = roles.first { $0.viaGroup != nil }
        #expect(viaGroup?.viaGroup == "Platform Team")
        let first = await http.requests.first!
        #expect(first.url.absoluteString.hasPrefix("https://management.azure.com/providers/Microsoft.Authorization/roleEligibilityScheduleInstances"))
        #expect(first.url.absoluteString.contains("api-version=2020-10-01"))
        #expect(first.url.absoluteString.contains("asTarget()"))
        #expect(first.headers["Authorization"] == "Bearer token-t1")
        #expect(await http.requests.count == 2)
    }

    @Test func listsActivatedAndPendingAssignments() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleAssignmentScheduleInstances", body: Fixtures.data("arm-active"))
        await http.on("GET", "roleAssignmentScheduleRequests", body: Fixtures.data("arm-pending"))
        await http.on("GET", "roleAssignmentSchedules?", body: Data(#"{"value":[]}"#.utf8))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 2)
        let contributor = active.first { $0.roleKey.scope == .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: contributorId) }!
        #expect(contributor.status == .active)
        #expect(contributor.assignmentId == "inst-1")
        #expect(contributor.endDateTime == GraphJSON.parseDate("2026-09-04T12:00:00Z"))
        let reader = active.first { $0.roleKey.scope == .azureResource(scope: "/subscriptions/sub-1/resourceGroups/rg-ops", roleDefinitionId: readerId) }!
        #expect(reader.status == .pendingApproval)
        #expect(reader.assignmentId == "req-77")
    }

    @Test func futureSchedulesAppearAsScheduled() async throws {
        let (p, http, _) = makeProvider()
        let empty = Data(#"{"value":[]}"#.utf8)
        await http.on("GET", "roleAssignmentScheduleInstances", body: empty)
        await http.on("GET", "roleAssignmentScheduleRequests", body: empty)
        await http.on("GET", "roleAssignmentSchedules?", body: Fixtures.data("arm-schedules"))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 2)                         // the 2020 schedule is not upcoming
        let contributor = try #require(active.first { $0.roleKey.scope == .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: contributorId) })
        #expect(contributor.status == .scheduled)
        #expect(contributor.assignmentId == "sched-1")
        #expect(contributor.startDateTime == GraphJSON.parseDate("2099-01-01T09:00:00Z"))
        #expect(contributor.endDateTime == GraphJSON.parseDate("2099-01-01T11:00:00Z"))
        let reader = try #require(active.first { $0.roleKey.scope == .azureResource(scope: "/subscriptions/sub-1/resourceGroups/rg-ops", roleDefinitionId: readerId) })
        #expect(reader.status == .scheduled)
        #expect(reader.assignmentId == "sched-pending-collision")
        let get = await http.requests(matching: "roleAssignmentSchedules?").first!
        #expect(get.url.absoluteString.contains("asTarget()"))
    }

    @Test func futureScheduleDoesNotOverrideAnActiveAssignment() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleAssignmentScheduleInstances", body: Fixtures.data("arm-active"))
        await http.on("GET", "roleAssignmentScheduleRequests", body: Fixtures.data("arm-pending"))
        await http.on("GET", "roleAssignmentSchedules?", body: Fixtures.data("arm-schedules"))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 2)
        let contributor = active.first { $0.roleKey.scope == .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: contributorId) }!
        #expect(contributor.status == .active)
        #expect(contributor.assignmentId == "inst-1")
        // A future schedule sharing the pending request's key must not override it either.
        let reader = active.first { $0.roleKey.scope == .azureResource(scope: "/subscriptions/sub-1/resourceGroups/rg-ops", roleDefinitionId: readerId) }!
        #expect(reader.status == .pendingApproval)
        #expect(reader.assignmentId == "req-77")
    }

    @Test func forbiddenIsNotTreatedAsConsent() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances", status: 403, body: Data(#"{"error":{"code":"AuthorizationFailed","message":"x"}}"#.utf8))
        await #expect(throws: PIMError.policyViolation("Not permitted at this scope")) {
            _ = try await p.eligibleRoles(identity: identity, tenant: tenant)
        }
    }

    var contributor: EligibleRole {
        EligibleRole(key: RoleKey(identityId: "id1", tenantId: "t1", scope: .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: contributorId)),
                     displayName: "Contributor", detail: "Pay-As-You-Go · subscription", source: .discovered, policy: .manualDefault)
    }

    @Test func readsEndUserPolicyForTheMatchingRoleAtScope() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleManagementPolicyAssignments", body: Fixtures.data("arm-policy"))
        let policy = try await p.policy(for: contributor, identity: identity)
        #expect(policy.maximumDuration == .seconds(4 * 3600))
        #expect(policy.defaultDuration == .seconds(4 * 3600))
        #expect(policy.requiresJustification && policy.requiresMFA && policy.requiresTicket && policy.requiresApproval)
        #expect(policy.authenticationContext == "c2")
        let req = await http.requests.first!
        #expect(req.url.absoluteString.hasPrefix("https://management.azure.com/subscriptions/sub-1/providers/Microsoft.Authorization/roleManagementPolicyAssignments"))
    }

    /// A JWT-shaped ARM token whose `oid` names the caller, as the real providers return.
    private struct JWTTokenProvider: TokenProviding {
        let oid: String
        private var token: String {
            let payload = try! JSONSerialization.data(withJSONObject: ["oid": oid, "tid": "t1"])
            let b64 = payload.base64EncodedString().replacingOccurrences(of: "+", with: "-")
                .replacingOccurrences(of: "/", with: "_").trimmingCharacters(in: CharacterSet(charactersIn: "="))
            return "eyJhbGciOiJub25lIn0.\(b64).sig"
        }
        func signIn(method: SignInMethod) async throws -> Identity { throw PIMError.notEligible }
        func signOut(_ identity: Identity) async throws {}
        func identities() async throws -> [Identity] { [] }
        func accessToken(identity: Identity, tenantId: String, scopes: [String]) async throws -> String { token }
        func acquireInteractively(identity: Identity, tenantId: String, scopes: [String], claims: String?) async throws -> String { token }
    }

    @Test func groupInheritedEligibilityActivatesAsTheCaller() async throws {
        // The eligibility instance names the group ("user-obj-1" stands in); ARM wants the requestor's own oid.
        let http = StubHTTPClient()
        let p = AzureResourceProvider(http: http, tokens: JWTTokenProvider(oid: "caller-oid"))
        await http.on("GET", "roleEligibilityScheduleInstances?", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        await http.on("PUT", "roleAssignmentScheduleRequests", status: 201, body: Fixtures.data("arm-activate-response"))
        _ = try await p.activate(ActivationRequest(roleKey: contributor.key, duration: .seconds(3600), justification: "x", ticket: nil), identity: identity)
        let put = await http.requests(matching: "roleAssignmentScheduleRequests").first!
        let props = (try JSONSerialization.jsonObject(with: put.body!) as! [String: Any])["properties"] as! [String: Any]
        #expect(props["principalId"] as? String == "caller-oid")
        #expect(props["linkedRoleEligibilityScheduleId"] as? String == "b1477448-2cc6-4ceb-93b4-54a202a89413")
    }

    @Test func activateLooksUpEligibilityAndPutsSelfActivate() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances", body: Fixtures.data("arm-eligible-page2"))
        await http.on("GET", "roleEligibilityScheduleInstances?", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        await http.on("PUT", "roleAssignmentScheduleRequests", status: 201, body: Fixtures.data("arm-activate-response"))
        let a = try await p.activate(ActivationRequest(roleKey: contributor.key, duration: .seconds(7200), justification: "INC-1", ticket: TicketInfo(number: "42", system: "Jira")), identity: identity)
        #expect(a.status == .active)
        #expect(a.assignmentId == "fea7a502-9a96-4806-a26f-eee560e52045")
        #expect(a.endDateTime == GraphJSON.parseDate("2026-09-04T11:00:00Z"))
        let put = await http.requests(matching: "roleAssignmentScheduleRequests").first!
        #expect(put.method == "PUT")
        #expect(put.url.absoluteString.hasPrefix("https://management.azure.com/subscriptions/sub-1/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/"))
        let name = put.url.lastPathComponent
        #expect(UUID(uuidString: name) != nil)
        let body = try JSONSerialization.jsonObject(with: put.body!) as! [String: Any]
        let props = body["properties"] as! [String: Any]
        #expect(props["requestType"] as? String == "SelfActivate")
        #expect(props["principalId"] as? String == "user-obj-1")
        #expect(props["roleDefinitionId"] as? String == contributorId)
        #expect(props["linkedRoleEligibilityScheduleId"] as? String == "b1477448-2cc6-4ceb-93b4-54a202a89413")
        #expect(props["justification"] as? String == "INC-1")
        #expect((props["ticketInfo"] as? [String: Any])?["ticketNumber"] as? String == "42")
        let exp = (props["scheduleInfo"] as! [String: Any])["expiration"] as! [String: Any]
        #expect(exp["type"] as? String == "AfterDuration")
        #expect(exp["duration"] as? String == "PT2H")
    }

    @Test func activateWithFutureStartSendsItAndReportsScheduled() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances", body: Fixtures.data("arm-eligible-page2"))
        await http.on("GET", "roleEligibilityScheduleInstances?", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        await http.on("PUT", "roleAssignmentScheduleRequests", status: 201, body: Fixtures.data("arm-activate-response"))
        let start = GraphJSON.parseDate("2099-01-01T09:00:00Z")!
        let a = try await p.activate(ActivationRequest(roleKey: contributor.key, duration: .seconds(7200), justification: "later", startDateTime: start), identity: identity)

        let put = await http.requests(matching: "roleAssignmentScheduleRequests").first!
        let props = (try JSONSerialization.jsonObject(with: put.body!) as! [String: Any])["properties"] as! [String: Any]
        let sched = props["scheduleInfo"] as! [String: Any]
        #expect(GraphJSON.parseDate(sched["startDateTime"] as! String) == start)
        // The response echoes a start in the past; the request's future start wins.
        #expect(a.status == .scheduled)
        #expect(a.startDateTime == start)
        #expect(a.endDateTime == GraphJSON.parseDate("2099-01-01T11:00:00Z"))
    }

    @Test func futureStartDoesNotMaskPendingApproval() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances", body: Fixtures.data("arm-eligible-page2"))
        await http.on("GET", "roleEligibilityScheduleInstances?", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        var json = try JSONSerialization.jsonObject(with: Fixtures.data("arm-activate-response")) as! [String: Any]
        var props = json["properties"] as! [String: Any]
        props["status"] = "PendingApproval"
        json["properties"] = props
        await http.on("PUT", "roleAssignmentScheduleRequests", status: 201, body: try JSONSerialization.data(withJSONObject: json))
        let start = GraphJSON.parseDate("2099-01-01T09:00:00Z")!
        let a = try await p.activate(ActivationRequest(roleKey: contributor.key, duration: .seconds(3600), justification: "later", startDateTime: start), identity: identity)
        #expect(a.status == .pendingApproval)
    }

    @Test func futureStartDoesNotMaskFailure() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances", body: Fixtures.data("arm-eligible-page2"))
        await http.on("GET", "roleEligibilityScheduleInstances?", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        var json = try JSONSerialization.jsonObject(with: Fixtures.data("arm-activate-response")) as! [String: Any]
        var props = json["properties"] as! [String: Any]
        props["status"] = "Denied"
        json["properties"] = props
        await http.on("PUT", "roleAssignmentScheduleRequests", status: 201, body: try JSONSerialization.data(withJSONObject: json))
        let start = GraphJSON.parseDate("2099-01-01T09:00:00Z")!
        let a = try await p.activate(ActivationRequest(roleKey: contributor.key, duration: .seconds(3600), justification: "later", startDateTime: start), identity: identity)
        #expect(a.status == .failed("Denied"))
    }

    @Test func manualRoleNameIsResolvedBeforeActivation() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleDefinitions?", body: Fixtures.data("arm-roledefinitions"))
        await http.on("GET", "roleEligibilityScheduleInstances?", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        await http.on("PUT", "roleAssignmentScheduleRequests", status: 201, body: Fixtures.data("arm-activate-response"))
        let manualKey = RoleKey(identityId: "id1", tenantId: "t1", scope: .azureResource(scope: "/subscriptions/SUB-1", roleDefinitionId: "Contributor"))
        let a = try await p.activate(ActivationRequest(roleKey: manualKey, duration: .seconds(3600), justification: "x"), identity: identity)
        #expect(a.status == .active)
        let defs = await http.requests(matching: "roleDefinitions?").first!
        #expect(defs.url.absoluteString.contains("api-version=2022-04-01"))
        #expect(defs.url.absoluteString.lowercased().contains("rolename%20eq%20'contributor'") || defs.url.absoluteString.lowercased().contains("rolename eq 'contributor'"))
        let put = await http.requests(matching: "roleAssignmentScheduleRequests").first!
        let props = (try JSONSerialization.jsonObject(with: put.body!) as! [String: Any])["properties"] as! [String: Any]
        #expect(props["roleDefinitionId"] as? String == contributorId)
        // The assignment comes back keyed by the resolved id, not by the role name we asked with.
        #expect(a.roleKey.scope == .azureResource(scope: "/subscriptions/SUB-1", roleDefinitionId: contributorId))
        #expect(a.roleKey.identityId == "id1" && a.roleKey.tenantId == "t1")
    }

    @Test func roleNameWithApostropheIsEscapedInTheODataFilter() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleDefinitions?", body: Fixtures.data("arm-roledefinitions"))
        await http.on("GET", "roleEligibilityScheduleInstances?", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        _ = try? await p.resolveRoleDefinitionId("O'Brien Operator", scope: "/subscriptions/sub-1", identity: identity, tenantId: "t1")
        let defs = await http.requests(matching: "roleDefinitions?").first!
        let filter = URLComponents(url: defs.url, resolvingAgainstBaseURL: false)!.queryItems!.first { $0.name == "$filter" }!.value
        #expect(filter == "roleName eq 'O''Brien Operator'")
    }

    @Test func activateWithoutMatchingEligibilityIsNotEligible() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances?", body: Fixtures.data("arm-eligible-page2"))
        let key = RoleKey(identityId: "id1", tenantId: "t1", scope: .azureResource(scope: "/subscriptions/sub-9", roleDefinitionId: contributorId))
        await #expect(throws: PIMError.notEligible) {
            _ = try await p.activate(ActivationRequest(roleKey: key, duration: .seconds(3600), justification: "x"), identity: identity)
        }
    }

    @Test func deactivatePutsSelfDeactivate() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "roleEligibilityScheduleInstances?", body: Fixtures.data("arm-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("arm-eligible-page2"))
        await http.on("PUT", "roleAssignmentScheduleRequests", status: 201, body: Fixtures.data("arm-activate-response"))
        let a = ActiveAssignment(roleKey: contributor.key, assignmentId: "inst-1", startDateTime: .now, endDateTime: nil, status: .active)
        try await p.deactivate(a, identity: identity)
        let put = await http.requests(matching: "roleAssignmentScheduleRequests").first!
        let props = (try JSONSerialization.jsonObject(with: put.body!) as! [String: Any])["properties"] as! [String: Any]
        #expect(props["requestType"] as? String == "SelfDeactivate")
        #expect(props["linkedRoleEligibilityScheduleId"] as? String == "b1477448-2cc6-4ceb-93b4-54a202a89413")
        #expect(props["scheduleInfo"] == nil)
    }

    @Test func cancelPostsToTheRequestAtItsScope() async throws {
        let (p, http, _) = makeProvider()
        await http.on("POST", "/cancel", status: 200, body: Data())
        let key = RoleKey(identityId: "id1", tenantId: "t1", scope: .azureResource(scope: "/subscriptions/sub-1/resourceGroups/rg-ops", roleDefinitionId: readerId))
        let a = ActiveAssignment(roleKey: key, assignmentId: "req-77", startDateTime: .now, endDateTime: nil, status: .pendingApproval)
        try await p.cancelPendingRequest(a, identity: identity)
        let post = await http.requests.first!
        #expect(post.method == "POST")
        #expect(post.url.absoluteString.hasPrefix("https://management.azure.com/subscriptions/sub-1/resourceGroups/rg-ops/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/req-77/cancel"))
    }
}
