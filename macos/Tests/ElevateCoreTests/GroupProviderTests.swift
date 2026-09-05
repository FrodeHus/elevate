import Testing
import Foundation
@testable import ElevateCore

@Suite struct GroupProviderTests {
    let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t-home")
    let tenant = TenantContext(identityId: "id1", tenantId: "t1", displayName: "Contoso", source: .home)

    func makeProvider() -> (GroupProvider, StubHTTPClient, FakeTokenProvider) {
        let http = StubHTTPClient()
        let tokens = FakeTokenProvider()
        return (GroupProvider(http: http, tokens: tokens), http, tokens)
    }

    @Test func listsEligibleGroupsAcrossPagesWithAccessCaption() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "eligibilityScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-eligible"))
        await http.on("GET", "skiptoken=page2", body: Fixtures.data("group-eligible-page2"))
        let roles = try await p.eligibleRoles(identity: identity, tenant: tenant)
        #expect(roles.map(\.displayName) == ["Dev Contributors", "Ops Admins", "Security Owners"])
        #expect(roles.map(\.detail) == ["member", "member", "owner"])
        #expect(roles[1].key.scope == .group(groupId: "grp-ops", accessId: .member))
        #expect(roles[2].key.scope == .group(groupId: "grp-sec", accessId: .owner))
        #expect(roles[2].viaGroup == "group")
        #expect(roles[1].viaGroup == nil)
        #expect(roles.allSatisfy { $0.source == .discovered && $0.key.tenantId == "t1" })
        let first = await http.requests.first!
        #expect(first.headers["Authorization"] == "Bearer token-t1")
        #expect(first.url.absoluteString.contains("identityGovernance/privilegedAccess/group/eligibilityScheduleInstances/filterByCurrentUser(on='principal')"))
        #expect(first.url.absoluteString.contains("expand=group"))
    }

    @Test func activeKeepsActivatedOnlyAndMergesPending() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "assignmentScheduleInstances/filterByCurrentUser", body: Fixtures.data("group-active"))
        await http.on("GET", "assignmentScheduleRequests/filterByCurrentUser", body: Fixtures.data("group-pending"))
        let active = try await p.activeAssignments(identity: identity, tenant: tenant)
        #expect(active.count == 2)
        let ops = active.first { $0.roleKey.scope == .group(groupId: "grp-ops", accessId: .member) }!
        #expect(ops.status == .active)
        #expect(ops.assignmentId == "ginst-1")
        #expect(ops.endDateTime == GraphJSON.parseDate("2026-09-04T16:00:00Z"))
        let sec = active.first { $0.roleKey.scope == .group(groupId: "grp-sec", accessId: .owner) }!
        #expect(sec.status == .pendingApproval)
        #expect(sec.assignmentId == "greq-9")
        let pendingReq = await http.requests(matching: "assignmentScheduleRequests").first!
        #expect(pendingReq.url.absoluteString.contains("status eq 'PendingApproval'") || pendingReq.url.absoluteString.contains("status%20eq%20'PendingApproval'"))
    }

    @Test func forbiddenReadMapsToConsentRequired() async throws {
        let (p, http, _) = makeProvider()
        await http.on("GET", "eligibilityScheduleInstances", status: 403, body: Data(#"{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}"#.utf8))
        await #expect(throws: PIMError.consentRequired) {
            _ = try await p.eligibleRoles(identity: identity, tenant: tenant)
        }
    }
}
