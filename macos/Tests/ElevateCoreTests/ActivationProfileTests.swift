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

    @Test func pinnedDecodesTolerantlyAndIsWrittenOnlyWhenTrue() throws {
        let legacy = #"{"id":"A1B2C3D4-E5F6-4A5B-8C7D-9E0FABCDEF12","name":"Ops","entries":[]}"#
        let decoded = try JSONDecoder().decode(ActivationProfile.self, from: Data(legacy.utf8))
        #expect(decoded.pinned == false)
        let unpinnedJSON = String(decoding: try JSONEncoder().encode(decoded), as: UTF8.self)
        #expect(!unpinnedJSON.contains("pinned"))
        var pinned = decoded; pinned.pinned = true
        let pinnedJSON = String(decoding: try JSONEncoder().encode(pinned), as: UTF8.self)
        #expect(pinnedJSON.contains(#""pinned":true"#))
        #expect(try JSONDecoder().decode(ActivationProfile.self, from: Data(pinnedJSON.utf8)).pinned)
    }

    @Test func pinningIsCappedAtTheLimit() {
        var s = AppState()
        let profiles = (0...ProfilePins.limit).map { ActivationProfile(name: "P\($0)", entries: []) }
        for p in profiles { s.upsertProfile(p) }
        for p in profiles.prefix(ProfilePins.limit) { #expect(s.setPinned(id: p.id, true)) }
        #expect(s.pinnedProfiles.count == ProfilePins.limit)
        #expect(!s.setPinned(id: profiles[ProfilePins.limit].id, true))
        #expect(s.profile(id: profiles[ProfilePins.limit].id)?.pinned == false)
        // Re-pinning an already pinned profile is not a new pin.
        #expect(s.setPinned(id: profiles[0].id, true))
        #expect(s.setPinned(id: profiles[0].id, false))
        #expect(s.pinnedProfiles.map(\.name) == ["P1", "P2", "P3"])
        #expect(s.setPinned(id: profiles[ProfilePins.limit].id, true))
        #expect(!s.setPinned(id: UUID(), true))
    }

    @Test func summaryCaption() {
        #expect(ProfileSummary.caption(entries: [.init(roleKey: key("a"), lastDuration: nil)]) == "1 role")
        #expect(ProfileSummary.caption(entries: [.init(roleKey: key("a"), lastDuration: nil), .init(roleKey: key("b", kind: .azureResource), lastDuration: nil)]) == "2 roles")
        #expect(ProfileSummary.caption(entries: [.init(roleKey: key("g", kind: .group), lastDuration: nil)]) == "1 group")
        #expect(ProfileSummary.caption(entries: [.init(roleKey: key("a"), lastDuration: nil), .init(roleKey: key("g", kind: .group), lastDuration: nil), .init(roleKey: key("h", kind: .group), lastDuration: nil)]) == "1 role · 2 groups")
        #expect(ProfileSummary.caption(entries: []) == "empty")
    }
}
