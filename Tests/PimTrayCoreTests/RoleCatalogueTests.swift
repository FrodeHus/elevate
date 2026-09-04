import Testing
import Foundation
@testable import PimTrayCore

@Suite struct RoleCatalogueTests {
    @Test func loadsBuiltInRoles() throws {
        let roles = try RoleCatalogue.entraBuiltInRoles()
        #expect(roles.count >= 130)
        let ga = roles.first { $0.displayName == "Global Administrator" }
        #expect(ga?.templateId == "62e90394-69f5-4237-9190-012177145e10")
        #expect(ga?.isPrivileged == true)
        #expect(roles == roles.sorted { $0.displayName < $1.displayName })
    }

    @Test func manualRolesBecomeEligibleRolesWithDefaultPolicy() {
        let tk = TenantKey(identityId: "i", tenantId: "t")
        let manual = [ManualRole(tenantKey: tk, scope: .entraDirectory(roleDefinitionId: "62e90394-69f5-4237-9190-012177145e10", directoryScopeId: "/"), displayName: "Global Administrator"),
                      ManualRole(tenantKey: TenantKey(identityId: "i", tenantId: "other"), scope: .group(groupId: "g", accessId: .member), displayName: "Ops")]
        let roles = ManualRoleSource.eligibleRoles(from: manual, tenantKey: tk)
        #expect(roles.count == 1)
        #expect(roles[0].source == .manual)
        #expect(roles[0].policy == .manualDefault)
        #expect(roles[0].key == RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "62e90394-69f5-4237-9190-012177145e10", directoryScopeId: "/")))
    }

    @Test func mergePrefersDiscovered() {
        let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
        let discovered = EligibleRole(key: key, displayName: "Disc", source: .discovered, policy: .manualDefault)
        let manual = EligibleRole(key: key, displayName: "Man", source: .manual, policy: .manualDefault)
        let other = EligibleRole(key: RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "x", directoryScopeId: "/")), displayName: "X", source: .manual, policy: .manualDefault)
        let merged = ManualRoleSource.merge(discovered: [discovered], manual: [manual, other])
        #expect(merged.map(\.displayName) == ["Disc", "X"])
    }

    @Test func manualAzureRoleCarriesScopeAsDetail() {
        let tk = TenantKey(identityId: "i", tenantId: "t")
        let manual = [ManualRole(tenantKey: tk, scope: .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: "Contributor"), displayName: "Contributor")]
        let roles = ManualRoleSource.eligibleRoles(from: manual, tenantKey: tk)
        #expect(roles[0].detail == "/subscriptions/sub-1")
        #expect(roles[0].displayName == "Contributor")
    }

    @Test func mergeDropsManualAzureRoleMatchingDiscoveredByScopeAndName() {
        let tk = TenantKey(identityId: "i", tenantId: "t")
        let discovered = EligibleRole(key: RoleKey(identityId: "i", tenantId: "t", scope: .azureResource(scope: "/subscriptions/SUB-1", roleDefinitionId: "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/b24988ac")),
                                      displayName: "Contributor", detail: "Pay-As-You-Go · subscription", source: .discovered, policy: .manualDefault)
        let manualSame = EligibleRole(key: RoleKey(identityId: "i", tenantId: "t", scope: .azureResource(scope: "/subscriptions/sub-1", roleDefinitionId: "contributor")),
                                      displayName: "contributor", detail: "/subscriptions/sub-1", source: .manual, policy: .manualDefault)
        let manualOther = EligibleRole(key: RoleKey(identityId: "i", tenantId: "t", scope: .azureResource(scope: "/subscriptions/sub-2", roleDefinitionId: "Reader")),
                                       displayName: "Reader", detail: "/subscriptions/sub-2", source: .manual, policy: .manualDefault)
        let merged = ManualRoleSource.merge(discovered: [discovered], manual: [manualSame, manualOther])
        #expect(merged.map(\.displayName) == ["Contributor", "Reader"])
        _ = tk
    }

    @Test func detailRoundTripsAndDefaultsToNil() throws {
        let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
        let role = EligibleRole(key: key, displayName: "X", source: .discovered, policy: .manualDefault)
        #expect(role.detail == nil)
        let data = try JSONEncoder().encode(EligibleRole(key: key, displayName: "X", detail: "d", source: .manual, policy: .manualDefault))
        #expect(try JSONDecoder().decode(EligibleRole.self, from: data).detail == "d")
    }
}
