import Testing
import Foundation
@testable import ElevateCore

@Suite struct ActivationProfileTests {
    func key(_ n: String, kind: RoleScopeKind = .entraDirectory, tenantId: String = "t") -> RoleKey {
        switch kind {
        case .entraDirectory: RoleKey(identityId: "i", tenantId: tenantId, scope: .entraDirectory(roleDefinitionId: n, directoryScopeId: "/"))
        case .azureResource: RoleKey(identityId: "i", tenantId: tenantId, scope: .azureResource(scope: "/subscriptions/s", roleDefinitionId: n))
        case .group: RoleKey(identityId: "i", tenantId: tenantId, scope: .group(groupId: n, accessId: .member))
        }
    }

    @Test func stateWithoutProfilesDecodes() throws {
        let json = #"{"identities":[],"tenants":[],"manualRoles":[],"memory":[]}"#
        let s = try JSONDecoder().decode(AppState.self, from: Data(json.utf8))
        #expect(s.profiles.isEmpty)
        let minimal = try JSONDecoder().decode(AppState.self, from: Data("{}".utf8))
        #expect(minimal.identities.isEmpty && minimal.profiles.isEmpty)
    }

    @Test func profilesRoundTripAndHelpers() throws {
        var s = AppState()
        let p = ActivationProfile(name: "Ops", entries: [.init(roleKey: key("a"), lastDuration: .seconds(3600))], lastJustification: "INC")
        s.upsertProfile(p)
        s.upsertProfile(ActivationProfile(name: "Second", entries: []))
        let decoded = try JSONDecoder().decode(AppState.self, from: JSONEncoder().encode(s))
        #expect(decoded.profiles.count == 2)
        #expect(decoded.profile(id: p.id)?.entries.first?.lastDuration == .seconds(3600))
        var renamed = p; renamed.name = "Ops 2"
        s.upsertProfile(renamed)
        #expect(s.profiles.count == 2 && s.profile(id: p.id)?.name == "Ops 2")
        s.moveProfile(fromOffsets: IndexSet(integer: 1), toOffset: 0)
        #expect(s.profiles.first?.name == "Second")
        s.removeProfile(id: p.id)
        #expect(s.profiles.count == 1)
    }

    @Test func removingTenantDropsProfileEntries() throws {
        var s = AppState()
        let keepKey = key("keep", tenantId: "t1")
        let dropKey = key("drop", tenantId: "t2")
        let p = ActivationProfile(name: "Mixed", entries: [.init(roleKey: keepKey), .init(roleKey: dropKey)])
        s.upsertProfile(p)
        s.removeTenant(TenantKey(identityId: "i", tenantId: "t2"))
        let entries = s.profile(id: p.id)?.entries ?? []
        #expect(entries.count == 1)
        #expect(entries.first?.roleKey == keepKey)
    }

    @Test func removingIdentityDropsProfileEntries() throws {
        var s = AppState()
        let keepKey = RoleKey(identityId: "other", tenantId: "t1", scope: .entraDirectory(roleDefinitionId: "keep", directoryScopeId: "/"))
        let dropKey = key("drop", tenantId: "t1")
        let p = ActivationProfile(name: "Mixed", entries: [.init(roleKey: keepKey), .init(roleKey: dropKey)])
        s.upsertProfile(p)
        s.removeIdentity("i")
        let entries = s.profile(id: p.id)?.entries ?? []
        #expect(entries.count == 1)
        #expect(entries.first?.roleKey == keepKey)
    }

    @Test func summaryCaption() {
        #expect(ProfileSummary.caption(entries: [.init(roleKey: key("a"), lastDuration: nil)]) == "1 role")
        #expect(ProfileSummary.caption(entries: [.init(roleKey: key("a"), lastDuration: nil), .init(roleKey: key("b", kind: .azureResource), lastDuration: nil)]) == "2 roles")
        #expect(ProfileSummary.caption(entries: [.init(roleKey: key("g", kind: .group), lastDuration: nil)]) == "1 group")
        #expect(ProfileSummary.caption(entries: [.init(roleKey: key("a"), lastDuration: nil), .init(roleKey: key("g", kind: .group), lastDuration: nil), .init(roleKey: key("h", kind: .group), lastDuration: nil)]) == "1 role · 2 groups")
        #expect(ProfileSummary.caption(entries: []) == "empty")
    }
}
