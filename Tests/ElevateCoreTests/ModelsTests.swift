import Testing
import Foundation
@testable import ElevateCore

@Suite struct ModelsTests {
    @Test func roleKeyDistinguishesTenants() {
        let scope = RoleScope.entraDirectory(roleDefinitionId: "abc", directoryScopeId: "/")
        let a = RoleKey(identityId: "id1", tenantId: "t1", scope: scope)
        let b = RoleKey(identityId: "id1", tenantId: "t2", scope: scope)
        #expect(a != b)
        #expect(a.tenantKey == TenantKey(identityId: "id1", tenantId: "t1"))
    }

    @Test func roleScopeRoundTripsThroughJSON() throws {
        let scopes: [RoleScope] = [
            .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"),
            .azureResource(scope: "/subscriptions/s", roleDefinitionId: "d"),
            .group(groupId: "g", accessId: .owner),
        ]
        let data = try JSONEncoder().encode(scopes)
        let back = try JSONDecoder().decode([RoleScope].self, from: data)
        #expect(back == scopes)
        #expect(scopes.map(\.kind) == [.entraDirectory, .azureResource, .group])
    }

    @Test func manualPolicyDefaults() {
        let p = RolePolicy.manualDefault
        #expect(p.defaultDuration == .seconds(3600))
        #expect(p.maximumDuration == .seconds(8 * 3600))
        #expect(p.requiresJustification)
        #expect(!p.requiresTicket)
        #expect(!p.requiresApproval)
    }

    @Test func tenantWithoutAzureFieldDecodesWithNoReason() throws {
        let json = Data(#"{"identityId":"id1","tenantId":"t1","displayName":"Contoso","source":"home","discoveryMode":"automatic"}"#.utf8)
        let tenant = try JSONDecoder().decode(TenantContext.self, from: json)
        #expect(tenant.azureUnavailableReason == nil)
        #expect(tenant.lastDiscoveryError == nil)
        var off = tenant
        off.azureUnavailableReason = "No Azure access in this tenant"
        let back = try JSONDecoder().decode(TenantContext.self, from: JSONEncoder().encode(off))
        #expect(back == off)
    }

    @Test func activeAssignmentStatusRoundTrips() throws {
        let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
        let a = ActiveAssignment(roleKey: key, assignmentId: "x", startDateTime: Date(timeIntervalSince1970: 0), endDateTime: nil, status: .failed("boom"))
        let data = try JSONEncoder().encode(a)
        #expect(try JSONDecoder().decode(ActiveAssignment.self, from: data) == a)
    }
}
