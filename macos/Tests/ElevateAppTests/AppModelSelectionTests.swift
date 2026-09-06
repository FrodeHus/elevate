import Foundation
import Testing
import ElevateCore
@testable import Elevate

@MainActor
struct AppModelSelectionTests {
    private func selectedModel() async -> AppModel {
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]
        let model = await makeModel(state: state)
        model.roles[Sample.tenantKey] = [
            Sample.role(Sample.entraKey, name: "Global Reader"),
            Sample.role(Sample.azureKey, name: "Owner"),
            Sample.role(Sample.groupKey, name: "Platform Admins"),
        ]
        model.selectMode = true
        model.toggleSelection(Sample.entraKey)
        model.toggleSelection(Sample.azureKey)
        model.toggleSelection(Sample.groupKey)
        return model
    }

    @Test func selectionSurvivesATabChangeAndClearsOnASearchChange() async {
        let model = await selectedModel()
        #expect(model.selectionCount == 3)

        model.panelTab = .azure
        #expect(model.selectionCount == 3)   // a profile may span tabs

        model.searchQuery = "owner"
        #expect(model.selection.isEmpty)
    }

    @Test func selectionBreakdownCountsEachKind() async {
        let model = await selectedModel()
        let breakdown = model.selectionBreakdown
        #expect(breakdown.entra == 1)
        #expect(breakdown.azure == 1)
        #expect(breakdown.groups == 1)
        #expect(model.selectionNoun == "item")

        model.toggleSelection(Sample.entraKey)
        model.toggleSelection(Sample.azureKey)
        #expect(model.selectionBreakdown == (0, 0, 1))
        #expect(model.selectionNoun == "group")
    }

    @Test func leavingSelectModeClearsTheSelection() async {
        let model = await selectedModel()
        model.selectMode = false
        #expect(model.selection.isEmpty)
    }
}
