import Testing
import Foundation
@testable import ElevateCore

@Suite struct ManagedProfileSetTests {
    /// The document from the design's §7.1.
    static let example = """
    {
      "version": 1,
      "profiles": [
        {
          "id": "prod-incident",
          "name": "Prod incident",
          "reason": "Incident response",
          "pinned": true,
          "roles": [
            { "kind": "azureResource", "tenant": "contoso.com",
              "scope": "/subscriptions/00000000-0000-0000-0000-000000000001",
              "role": "Contributor", "duration": "PT2H" },
            { "kind": "entraDirectory", "tenant": "contoso.com",
              "role": "Security Reader" },
            { "kind": "group", "tenant": "contoso.com",
              "group": "SRE on-call", "access": "member", "duration": "PT4H" }
          ]
        }
      ]
    }
    """

    @Test func parsesTheSpecExample() throws {
        let set = try ManagedProfileSet.parse(Self.example)
        #expect(set.profiles.count == 1)
        let p = try #require(set.profiles.first)
        #expect(p.id == "prod-incident")
        #expect(p.name == "Prod incident")
        #expect(p.reason == "Incident response")
        #expect(p.pinned)
        #expect(p.profileId == ManagedProfileSet.profileId(slug: "prod-incident"))
        #expect(p.roles.count == 3)

        #expect(p.roles[0].kind == .azureResource)
        #expect(p.roles[0].tenant == "contoso.com")
        #expect(p.roles[0].scope == "/subscriptions/00000000-0000-0000-0000-000000000001")
        #expect(p.roles[0].role == "Contributor")
        #expect(p.roles[0].duration == .seconds(2 * 3600))

        #expect(p.roles[1].kind == .entraDirectory)
        #expect(p.roles[1].role == "Security Reader")
        #expect(p.roles[1].directoryScope == "/")
        #expect(p.roles[1].duration == nil)

        #expect(p.roles[2].kind == .group)
        #expect(p.roles[2].group == "SRE on-call")
        #expect(p.roles[2].access == .member)
        #expect(p.roles[2].duration == .seconds(4 * 3600))
    }

    @Test func defaultsAndUnknownFieldsAreIgnored() throws {
        let json = """
        {"profiles":[{"id":"p","name":"P","future":42,"pinned":1,
          "roles":[{"kind":"group","tenant":"contoso.com","group":"g","extra":"x"}]}]}
        """
        let set = try ManagedProfileSet.parse(json)
        let p = try #require(set.profiles.first)
        #expect(!p.pinned)   // only a JSON true pins
        #expect(p.reason == nil)
        #expect(p.roles[0].access == .member)
    }

    @Test func rejectsAFutureVersion() {
        let json = #"{"version":2,"profiles":[]}"#
        #expect(throws: ManagedProfileError.invalid("version 2 is not supported")) {
            try ManagedProfileSet.parse(json)
        }
    }

    @Test func rejectsANonIntegerVersion() {
        let json = #"{"version":1.9,"profiles":[]}"#
        #expect(throws: ManagedProfileError.invalid("version 1.9 is not supported")) {
            try ManagedProfileSet.parse(json)
        }
    }

    @Test func rejectsATrailingNewlineInTheSlug() {
        let json = "{\"profiles\":[{\"id\":\"prod-incident\\n\",\"name\":\"P\",\"roles\":[]}]}"
        #expect(throws: ManagedProfileError.invalid("profile 'prod-incident\n': id must match [a-z0-9-]{1,64}")) {
            try ManagedProfileSet.parse(json)
        }
    }

    @Test func requiresAName() {
        let json = #"{"version":1,"profiles":[{"id":"prod-incident","roles":[]}]}"#
        #expect(throws: ManagedProfileError.invalid("profile 'prod-incident': name is required")) {
            try ManagedProfileSet.parse(json)
        }
    }

    @Test func rejectsANonSlugId() {
        let json = #"{"profiles":[{"id":"Prod Incident","name":"P","roles":[]}]}"#
        #expect(throws: ManagedProfileError.invalid("profile 'Prod Incident': id must match [a-z0-9-]{1,64}")) {
            try ManagedProfileSet.parse(json)
        }
    }

    @Test func rejectsAMissingId() {
        let json = #"{"profiles":[{"name":"P","roles":[]}]}"#
        #expect(throws: ManagedProfileError.invalid("profile 1: id is required")) {
            try ManagedProfileSet.parse(json)
        }
    }

    @Test func rejectsDuplicateIds() {
        let json = """
        {"profiles":[{"id":"p","name":"One","roles":[]},{"id":"p","name":"Two","roles":[]}]}
        """
        #expect(throws: ManagedProfileError.invalid("profile 'p': duplicate id")) {
            try ManagedProfileSet.parse(json)
        }
    }

    @Test func rejectsAnUnknownKind() {
        let json = #"{"profiles":[{"id":"x","name":"X","roles":[{"kind":"foo","tenant":"t"}]}]}"#
        #expect(throws: ManagedProfileError.invalid("profile 'x' role 1: unknown kind 'foo'")) {
            try ManagedProfileSet.parse(json)
        }
    }

    @Test func azureRoleRequiresAScope() {
        let json = #"{"profiles":[{"id":"x","name":"X","roles":[{"kind":"azureResource","tenant":"t","role":"Contributor"}]}]}"#
        #expect(throws: ManagedProfileError.invalid("profile 'x' role 1: scope is required for azureResource")) {
            try ManagedProfileSet.parse(json)
        }
    }

    @Test func groupRoleRequiresAGroup() {
        let json = #"{"profiles":[{"id":"x","name":"X","roles":[{"kind":"group","tenant":"t"}]}]}"#
        #expect(throws: ManagedProfileError.invalid("profile 'x' role 1: group is required")) {
            try ManagedProfileSet.parse(json)
        }
    }

    @Test func roleRequiresATenantARoleNameAndAValidDuration() {
        #expect(throws: ManagedProfileError.invalid("profile 'x' role 1: tenant is required")) {
            try ManagedProfileSet.parse(#"{"profiles":[{"id":"x","name":"X","roles":[{"kind":"group","group":"g"}]}]}"#)
        }
        #expect(throws: ManagedProfileError.invalid("profile 'x' role 2: role is required")) {
            try ManagedProfileSet.parse("""
            {"profiles":[{"id":"x","name":"X","roles":[
              {"kind":"group","tenant":"t","group":"g"},
              {"kind":"entraDirectory","tenant":"t"}]}]}
            """)
        }
        #expect(throws: ManagedProfileError.invalid("profile 'x' role 1: 'PT' is not a valid duration")) {
            try ManagedProfileSet.parse(#"{"profiles":[{"id":"x","name":"X","roles":[{"kind":"group","tenant":"t","group":"g","duration":"PT"}]}]}"#)
        }
        #expect(throws: ManagedProfileError.invalid("profile 'x' role 1: unknown access 'admin'")) {
            try ManagedProfileSet.parse(#"{"profiles":[{"id":"x","name":"X","roles":[{"kind":"group","tenant":"t","group":"g","access":"admin"}]}]}"#)
        }
    }

    @Test func rejectsMalformedJSON() {
        #expect(throws: ManagedProfileError.invalid("not a JSON object")) {
            try ManagedProfileSet.parse("[]")
        }
    }

    @Test func profileIdIsAStableUuidV5() {
        // uuid.uuid5(uuid.NAMESPACE_DNS, "managed-profile:prod-incident")
        #expect(ManagedProfileSet.profileId(slug: "prod-incident")
            == UUID(uuidString: "cd5ed157-66f4-5485-ad04-ae05ef70f344"))
        // Version 5, RFC 4122 variant.
        let bytes = ManagedProfileSet.profileId(slug: "other").uuid
        #expect(bytes.6 & 0xF0 == 0x50)
        #expect(bytes.8 & 0xC0 == 0x80)
        #expect(ManagedProfileSet.profileId(slug: "other") != ManagedProfileSet.profileId(slug: "prod-incident"))
    }

    @Test func mergedReplacesByIdAndAppendsNewOnes() throws {
        let base = try ManagedProfileSet.parse("""
        {"profiles":[{"id":"a","name":"A","roles":[]},{"id":"b","name":"B","roles":[]}]}
        """)
        let other = try ManagedProfileSet.parse("""
        {"profiles":[{"id":"b","name":"B2","roles":[]},{"id":"c","name":"C","roles":[]}]}
        """)
        let merged = base.merged(with: other)
        #expect(merged.profiles.map(\.id) == ["a", "b", "c"])
        #expect(merged.profiles.map(\.name) == ["A", "B2", "C"])
        #expect(ManagedProfileSet.empty.merged(with: other).profiles.map(\.id) == ["b", "c"])
        #expect(base.merged(with: .empty) == base)
    }
}
