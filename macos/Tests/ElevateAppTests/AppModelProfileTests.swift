import Foundation
import Testing
import ElevateCore
@testable import Elevate

@MainActor
struct AppModelProfileTests {
    private func loadedModel() async -> AppModel {
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]
        let model = await makeModel(state: state)
        model.roles[Sample.tenantKey] = [
            Sample.role(Sample.entraKey, name: "Global Reader"),
            Sample.role(Sample.azureKey, name: "Owner"),
            Sample.role(Sample.groupKey, name: "Platform Admins"),
        ]
        return model
    }

    @Test func pinningStopsAtTheLimit() async {
        let model = await loadedModel()
        let profiles = (0...ProfilePins.limit).compactMap { model.saveProfile(name: "P\($0)", keys: [Sample.entraKey]) }
        for p in profiles.prefix(ProfilePins.limit) { #expect(model.setPinned(id: p.id, true)) }
        #expect(!model.setPinned(id: profiles[ProfilePins.limit].id, true))
        #expect(model.pinnedProfiles.map(\.name) == ["P0", "P1", "P2", "P3"])
        #expect(model.setPinned(id: profiles[1].id, false))
        #expect(model.pinnedProfiles.map(\.name) == ["P0", "P2", "P3"])
        cleanup(model)
    }

    @Test func entriesAreAddedRemovedAndGivenDurationsInPlace() async {
        let model = await loadedModel()
        let p = model.saveProfile(name: "Ops", keys: [Sample.entraKey])!

        model.addProfileEntries(id: p.id, keys: [Sample.groupKey, Sample.entraKey])
        var entries = model.profile(id: p.id)?.entries ?? []
        // Same account and tenant: ordered by kind, and the existing key is not duplicated.
        #expect(entries.map(\.roleKey) == [Sample.entraKey, Sample.groupKey])

        model.setProfileEntryDuration(id: p.id, key: Sample.groupKey, duration: .seconds(7200))
        entries = model.profile(id: p.id)?.entries ?? []
        #expect(entries.first { $0.roleKey == Sample.groupKey }?.lastDuration == .seconds(7200))

        // Adding more keeps the duration already chosen. Kinds sort by their raw name here, so
        // Azure lands before Entra.
        model.addProfileEntries(id: p.id, keys: [Sample.azureKey])
        entries = model.profile(id: p.id)?.entries ?? []
        #expect(entries.map(\.roleKey) == [Sample.azureKey, Sample.entraKey, Sample.groupKey])
        #expect(entries.last?.lastDuration == .seconds(7200))

        model.removeProfileEntry(id: p.id, key: Sample.entraKey)
        #expect(model.profile(id: p.id)?.entries.map(\.roleKey) == [Sample.azureKey, Sample.groupKey])
        cleanup(model)
    }

    @Test func newProfileIsEmptyAndTheShortcutBindsToOneProfile() async {
        let model = await loadedModel()
        let fresh = model.newProfile()
        #expect(model.profile(id: fresh.id)?.entries.isEmpty == true)
        #expect(fresh.name == "New profile")

        model.setHotKeyProfile(fresh.id)
        #expect(model.settings.hotKeyProfileId == fresh.id)
        model.setHotKeyProfile(nil)
        #expect(model.settings.hotKeyProfileId == nil)

        model.setHotKeyProfile(fresh.id)
        model.deleteProfile(id: fresh.id)
        #expect(model.settings.hotKeyProfileId == nil)
        #expect(model.profiles.isEmpty)
        cleanup(model)
    }
}
