import Testing
import Foundation
@testable import ElevateCore

@Suite struct ManagedProfileResolverTests {
    static let tenantId = "11111111-1111-1111-1111-111111111111"
    static let contributorId = "/subscriptions/S/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c"
    static let securityReaderId = "5d6b6bb7-de71-4623-b4af-96380a352509"

    var tenantIds: [String: String] { ["contoso.com": Self.tenantId] }

    var tenants: [TenantContext] {
        ["id-1", "id-2"].map {
            TenantContext(identityId: $0, tenantId: Self.tenantId, displayName: "Contoso", source: .home)
        }
    }

    func role(_ key: RoleKey, _ name: String) -> EligibleRole {
        EligibleRole(key: key, displayName: name, source: .discovered, policy: .manualDefault)
    }

    func key(_ identity: String, _ scope: RoleScope) -> RoleKey {
        RoleKey(identityId: identity, tenantId: Self.tenantId, scope: scope)
    }

    var azureKey: RoleKey { key("id-1", .azureResource(scope: "/subscriptions/S", roleDefinitionId: Self.contributorId)) }
    func entraKey(_ identity: String) -> RoleKey {
        key(identity, .entraDirectory(roleDefinitionId: Self.securityReaderId, directoryScopeId: "/"))
    }
    var groupKey: RoleKey { key("id-2", .group(groupId: "g-1", accessId: .member)) }

    var roles: [RoleKey: EligibleRole] {
        [azureKey: role(azureKey, "Contributor"),
         entraKey("id-1"): role(entraKey("id-1"), "Security Reader"),
         entraKey("id-2"): role(entraKey("id-2"), "Security Reader"),
         groupKey: role(groupKey, "SRE on-call")]
    }

    /// The §7.1 example, with the scope and names in a different case than the eligible roles carry.
    var set: ManagedProfileSet {
        get throws {
            try ManagedProfileSet.parse("""
            {"version":1,"profiles":[{"id":"prod-incident","name":"Prod incident",
              "reason":"Incident response","pinned":true,"roles":[
                {"kind":"azureResource","tenant":"contoso.com","scope":"/SUBSCRIPTIONS/s",
                 "role":"contributor","duration":"PT2H"},
                {"kind":"entraDirectory","tenant":"contoso.com","role":"SECURITY READER"},
                {"kind":"group","tenant":"contoso.com","group":"sre on-call","access":"member","duration":"PT4H"}]}]}
            """)
        }
    }

    @Test func resolvesRolesForEveryIdentityInTheTenant() throws {
        let result = ManagedProfileResolver.resolve(try set, tenantIds: tenantIds, tenants: tenants, roles: roles)
        #expect(result.warnings.isEmpty)
        let profile = try #require(result.profiles.first)
        #expect(result.profiles.count == 1)
        #expect(profile.id == ManagedProfileSet.profileId(slug: "prod-incident"))
        #expect(profile.name == "Prod incident")
        #expect(profile.lastJustification == "Incident response")
        #expect(profile.pinned)
        #expect(profile.source == .managed)
        #expect(profile.entries.map(\.roleKey) == [azureKey, entraKey("id-1"), entraKey("id-2"), groupKey])
        #expect(profile.entries.map(\.lastDuration) == [.seconds(2 * 3600), nil, nil, .seconds(4 * 3600)])
    }

    @Test func matchesByRoleDefinitionGuidAndObjectId() throws {
        let set = try ManagedProfileSet.parse("""
        {"profiles":[{"id":"ids","name":"By id","roles":[
          {"kind":"azureResource","tenant":"contoso.com","scope":"/subscriptions/S",
           "role":"B24988AC-6180-42A0-AB88-20F7382DD24C"},
          {"kind":"entraDirectory","tenant":"contoso.com","role":"\(Self.securityReaderId)"},
          {"kind":"group","tenant":"contoso.com","group":"G-1"}]}]}
        """)
        let result = ManagedProfileResolver.resolve(set, tenantIds: tenantIds, tenants: tenants, roles: roles)
        #expect(result.warnings.isEmpty)
        #expect(result.profiles.first?.entries.map(\.roleKey) == [azureKey, entraKey("id-1"), entraKey("id-2"), groupKey])
    }

    @Test func acceptsATenantGuidDirectly() throws {
        let set = try ManagedProfileSet.parse("""
        {"profiles":[{"id":"guid","name":"Guid","roles":[
          {"kind":"entraDirectory","tenant":"\(Self.tenantId.uppercased())","role":"Security Reader"}]}]}
        """)
        let result = ManagedProfileResolver.resolve(set, tenantIds: [:], tenants: tenants, roles: roles)
        #expect(result.warnings.isEmpty)
        #expect(result.profiles.first?.entries.map(\.roleKey) == [entraKey("id-1"), entraKey("id-2")])
    }

    @Test func addsAPlaceholderEntryPerIdentityWhenNothingMatches() throws {
        let set = try ManagedProfileSet.parse("""
        {"profiles":[{"id":"miss","name":"Miss","roles":[
          {"kind":"azureResource","tenant":"contoso.com","scope":"/subscriptions/other","role":"Owner"},
          {"kind":"entraDirectory","tenant":"contoso.com","role":"Global Reader","directoryScope":"/x"},
          {"kind":"group","tenant":"contoso.com","group":"Payments","access":"owner"}]}]}
        """)
        let result = ManagedProfileResolver.resolve(set, tenantIds: tenantIds, tenants: tenants, roles: roles)
        #expect(result.warnings.isEmpty)
        let entries = try #require(result.profiles.first).entries.map(\.roleKey)
        #expect(entries == [
            key("id-1", .azureResource(scope: "/subscriptions/other", roleDefinitionId: "Owner")),
            key("id-1", .entraDirectory(roleDefinitionId: "Global Reader", directoryScopeId: "/x")),
            key("id-1", .group(groupId: "Payments", accessId: .owner)),
            key("id-2", .azureResource(scope: "/subscriptions/other", roleDefinitionId: "Owner")),
            key("id-2", .entraDirectory(roleDefinitionId: "Global Reader", directoryScopeId: "/x")),
            key("id-2", .group(groupId: "Payments", accessId: .owner)),
        ])
    }

    @Test func dropsARoleNoAccountTracksTheTenantFor() throws {
        let set = try ManagedProfileSet.parse("""
        {"profiles":[{"id":"prod-incident","name":"Prod incident","roles":[
          {"kind":"entraDirectory","tenant":"fabrikam.com","role":"Security Reader"},
          {"kind":"entraDirectory","tenant":"contoso.com","role":"Security Reader"}]}]}
        """)
        let ids = ["contoso.com": Self.tenantId, "fabrikam.com": "22222222-2222-2222-2222-222222222222"]
        let result = ManagedProfileResolver.resolve(set, tenantIds: ids, tenants: tenants, roles: roles)
        #expect(result.warnings == ["Prod incident: no account in tenant fabrikam.com"])
        #expect(result.profiles.first?.entries.map(\.roleKey) == [entraKey("id-1"), entraKey("id-2")])
    }

    @Test func warnsWhenADomainCouldNotBeResolved() throws {
        let set = try ManagedProfileSet.parse("""
        {"profiles":[{"id":"prod-incident","name":"Prod incident","roles":[
          {"kind":"entraDirectory","tenant":"fabrikam.com","role":"Security Reader"}]}]}
        """)
        let result = ManagedProfileResolver.resolve(set, tenantIds: tenantIds, tenants: tenants, roles: roles)
        #expect(result.warnings == ["Prod incident: could not resolve tenant fabrikam.com"])
        #expect(result.profiles.first?.entries.isEmpty == true)
    }

    @Test func emptySetResolvesToNothing() {
        let result = ManagedProfileResolver.resolve(.empty, tenantIds: [:], tenants: [], roles: [:])
        #expect(result.profiles.isEmpty && result.warnings.isEmpty)
    }
}
