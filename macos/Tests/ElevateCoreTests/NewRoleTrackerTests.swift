import Testing
import Foundation
@testable import ElevateCore

@Suite struct NewRoleTrackerTests {
    func key(_ n: String) -> RoleKey { RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: n, directoryScopeId: "/")) }

    @Test func firstObserveBaselinesWithoutReportingAdditions() {
        var t = NewRoleTracker()
        let added = t.observe(discovered: [key("a"), key("b")])
        #expect(added.isEmpty)
        #expect(t.seen == [key("a"), key("b")])
        #expect(t.new.isEmpty)
    }

    @Test func additionsAreReportedOnceAndMarkedNew() {
        var t = NewRoleTracker()
        _ = t.observe(discovered: [key("a")])
        let added = t.observe(discovered: [key("a"), key("c"), key("b")])
        #expect(Set(added) == [key("b"), key("c")])
        #expect(t.isNew(key("b")) && t.isNew(key("c")) && !t.isNew(key("a")))
        #expect(t.observe(discovered: [key("a"), key("b"), key("c")]).isEmpty)
        #expect(t.new == [key("b"), key("c")])
    }

    @Test func removalsAreIgnoredAndAReturningRoleIsNewAgain() {
        var t = NewRoleTracker()
        _ = t.observe(discovered: [key("a"), key("b")])
        #expect(t.observe(discovered: [key("a")]).isEmpty)
        #expect(t.seen == [key("a")])
        #expect(t.observe(discovered: [key("a"), key("b")]) == [key("b")])
    }

    @Test func markerClearsOnTheSecondPanelOpen() {
        var t = NewRoleTracker()
        _ = t.observe(discovered: [key("a")])
        _ = t.observe(discovered: [key("a"), key("b")])
        t.panelOpened()
        #expect(t.isNew(key("b")))
        t.panelOpened()
        #expect(!t.isNew(key("b")))
        #expect(t.shownOpens == 0)
    }

    @Test func panelOpensWithNothingNewDoNotCount() {
        var t = NewRoleTracker()
        _ = t.observe(discovered: [key("a")])
        t.panelOpened(); t.panelOpened(); t.panelOpened()
        _ = t.observe(discovered: [key("a"), key("b")])
        t.panelOpened()
        #expect(t.isNew(key("b")))
    }

    @Test func opensCountedAgainstAVanishedMarkerDoNotShortenTheNextOne() {
        // b is new, the panel opens once, then b disappears: the marker empties and the count must go with it.
        var t = NewRoleTracker()
        _ = t.observe(discovered: [key("a")])
        _ = t.observe(discovered: [key("a"), key("b")])
        t.panelOpened()
        _ = t.observe(discovered: [key("a")])
        #expect(t.new.isEmpty)
        #expect(t.shownOpens == 0)

        // c gets its full two opens.
        _ = t.observe(discovered: [key("a"), key("c")])
        t.panelOpened()
        #expect(t.isNew(key("c")))
        t.panelOpened()
        #expect(!t.isNew(key("c")))
    }

    @Test func emptyDiscoveryNeverBaselines() {
        var t = NewRoleTracker()
        #expect(t.observe(discovered: []).isEmpty)
        #expect(t.seen.isEmpty)
        #expect(t.observe(discovered: [key("a")]).isEmpty)   // still the first real sight
    }

    @Test func roundTripsThroughCodable() throws {
        var t = NewRoleTracker()
        _ = t.observe(discovered: [key("a")])
        _ = t.observe(discovered: [key("a"), key("b")])
        t.panelOpened()
        let data = try JSONEncoder().encode(t)
        #expect(try JSONDecoder().decode(NewRoleTracker.self, from: data) == t)
    }
}
