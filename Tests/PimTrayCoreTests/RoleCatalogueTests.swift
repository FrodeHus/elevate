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
}
