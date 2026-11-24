import Foundation
import Testing
import ElevateCore
@testable import Elevate

@MainActor
struct AppModelProfileRunTests {
    private func loadedModel(notifier: any ExpiryNotifying = NoopNotifier()) async -> (AppModel, UUID) {
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]
        let model = await makeModel(state: state, notifier: notifier)
        model.roles[Sample.tenantKey] = [Sample.role(Sample.entraKey, name: "Global Reader")]
        let profile = model.saveProfile(name: "Ops", keys: [Sample.entraKey])!
        return (model, profile.id)
    }

    @Test func aSessionPlansTheProfileAndPrefillsTheReason() async {
        let (model, id) = await loadedModel()
        let session = model.runSession(for: id)
        #expect(session.items.count == 1)
        #expect(session.justification == (model.profile(id: id)?.lastJustification ?? ""))
        #expect(model.runSession(for: id) === session)
        cleanup(model)
    }

    @Test func uncheckingAnEntryRemovesItFromToActivate() async {
        let (model, id) = await loadedModel()
        let session = model.runSession(for: id)
        #expect(session.toActivate.count == 1)
        session.excluded.insert(Sample.entraKey)
        #expect(session.toActivate.isEmpty)
        cleanup(model)
    }

    @Test func leavingAnUnstartedDraftDropsIt() async {
        let (model, id) = await loadedModel()
        model.showProfile(id)
        #expect(model.runSessions[id] != nil)
        model.select(.allTenants)
        #expect(model.runSessions[id] == nil)
        cleanup(model)
    }

    @Test func progressReachesTheRowsOfARunningSessionOnly() async {
        let (model, id) = await loadedModel()
        let session = model.runSession(for: id)
        model.progress[Sample.entraKey] = .failed(.unexpected(status: 0, body: "x"))
        #expect(session.rowResults.isEmpty)
        session.running = true
        model.progress[Sample.entraKey] = .failed(.unexpected(status: 0, body: "x"))
        #expect(session.rowResults[Sample.entraKey] != nil)
        cleanup(model)
    }

    @Test func aDraftSurvivesThePanelClosing() async {
        let (model, id) = await loadedModel()
        model.showProfile(id)
        model.runSession(for: id).justification = "deploy"
        model.panelClosed()
        model.panelOpened()
        #expect(model.panelSelection == .profile(id))
        #expect(model.runSession(for: id).justification == "deploy")
        cleanup(model)
    }

    @Test func aRunningSessionSurvivesLeavingAndReopens() async {
        let (model, id) = await loadedModel()
        model.showProfile(id)
        model.runSession(for: id).running = true
        model.select(.allTenants)
        model.panelClosed()
        model.panelOpened()
        #expect(model.panelSelection == .profile(id))
        cleanup(model)
    }

    @Test func unseenResultsReopenOnTheProfile() async {
        let (model, id) = await loadedModel()
        let session = model.runSession(for: id)
        session.finished = true
        model.select(.allTenants)
        model.panelOpened()
        #expect(model.panelSelection == .profile(id))
        #expect(session.resultsSeen == false)
        cleanup(model)
    }

    @Test func seenResultsDoNotReopen() async {
        let (model, id) = await loadedModel()
        model.showProfile(id)
        model.runSession(for: id).finished = true
        model.markRunSeen(id)
        model.panelClosed()
        model.panelOpened()
        #expect(model.panelSelection == .allTenants)
        #expect(model.runSessions[id] == nil)
        cleanup(model)
    }

    // The activation itself fails (no stubbed service), which still ends the run and reports it.
    @Test func aRunThatEndsWithThePanelClosedNotifies() async {
        let notifier = RecordingNotifier()
        let (model, id) = await loadedModel(notifier: notifier)
        model.showProfile(id)
        model.runSession(for: id).justification = "because"
        model.panelClosed()
        await model.submitRun(id)
        #expect(model.runSessions[id]?.finished == true)
        let titles = await notifier.notifications.map(\.title)
        #expect(titles == ["Ops"])
        cleanup(model)
    }

    @Test func aRunThatEndsWithThePanelOpenDoesNotNotify() async {
        let notifier = RecordingNotifier()
        let (model, id) = await loadedModel(notifier: notifier)
        model.showProfile(id)
        model.runSession(for: id).justification = "because"
        model.panelOpened()
        await model.submitRun(id)
        #expect(model.runSessions[id]?.finished == true)
        #expect(await notifier.notifications.isEmpty)
        cleanup(model)
    }

    @Test func deletingTheShownProfileClearsSelectionAndSession() async {
        let (model, id) = await loadedModel()
        model.showProfile(id)
        model.runSession(for: id).running = true
        model.deleteProfile(id: id)
        #expect(model.selectedProfile == nil)
        #expect(model.runSessions[id] == nil)
        #expect(model.panelSelection == .allTenants)
        cleanup(model)
    }

    @Test func showingAFinishedProfileAgainStartsAFreshPlan() async {
        let (model, id) = await loadedModel()
        let session = model.runSession(for: id)
        session.finished = true
        session.excluded.insert(Sample.entraKey)
        model.showProfile(id)
        #expect(model.runSession(for: id).finished == false)
        #expect(model.runSession(for: id).excluded.isEmpty)
        cleanup(model)
    }

    private func managedModel() async -> AppModel {
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant(tenantId: AppModelManagedProfilesTests.tenantId)]
        let values: [String: Any] = ["ManagedProfiles": AppModelManagedProfilesTests.document()]
        let model = await makeModel(state: state, managed: ManagedConfiguration.load(from: DictionaryManagedSource(values)))
        model.roles[AppModelManagedProfilesTests.tenantKey] = [Sample.role(AppModelManagedProfilesTests.entraKey, name: "Global Reader")]
        return model
    }

    @Test func aManagedProfileWhoseRolesAreLoadingKeepsItsDraftAcrossReopen() async {
        let model = await managedModel()
        let id = AppModelManagedProfilesTests.managedId
        model.showProfile(id)
        model.runSession(for: id).justification = "deploy"
        // A refresh is under way: no eligible roles are known for now.
        model.roles = [:]
        model.panelClosed()
        model.panelOpened()
        #expect(model.selectedProfile == id)
        #expect(model.runSessions[id]?.justification == "deploy")
        cleanup(model)
    }

    @Test func aManagedProfileRemovedFromTheDocumentLosesSessionAndSelection() async {
        let model = await managedModel()
        let id = AppModelManagedProfilesTests.managedId
        model.showProfile(id)
        model.inlineProfileSet = .empty
        model.panelClosed()
        model.panelOpened()
        #expect(model.selectedProfile == nil)
        #expect(model.runSessions[id] == nil)
        cleanup(model)
    }
}
