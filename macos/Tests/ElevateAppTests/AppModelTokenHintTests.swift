import Foundation
import Testing
import ElevateCore
@testable import Elevate

@MainActor
struct AppModelTokenHintTests {
    private func activated(_ key: RoleKey) -> ActivationOutcome {
        let now = Date()
        return ActivationOutcome(roleKey: key, result: .activated(ActiveAssignment(roleKey: key, assignmentId: "a", startDateTime: now,
                                                                                    endDateTime: now.addingTimeInterval(3600), status: .active)))
    }

    @Test func anAzureOrGroupActivationRaisesTheHintOnceUntilDismissedForTheAccount() async {
        // Signed in as the Azure CLI app: Elevate shares the CLI's token cache, so the hint is certain.
        var state = AppState()
        state.identities = [Sample.identity(method: .azureCLI)]
        state.tenants = [Sample.tenant()]
        let model = await makeModel(state: state)

        model.noteTokenHint([activated(Sample.entraKey)])
        #expect(model.tokenHint == nil, "an Entra role does not touch the Azure CLI's token")

        model.noteTokenHint([activated(Sample.groupKey), activated(Sample.azureKey)])
        #expect(model.tokenHint?.account == Sample.identity().upn)

        model.dismissTokenHint()
        #expect(model.tokenHint == nil)
        #expect(model.settings.dismissedTokenHintAccounts == [Sample.identity().id])

        model.noteTokenHint([activated(Sample.azureKey)])
        #expect(model.tokenHint == nil, "the account dismissed it")
        cleanup(model)
    }

    @Test func anAppRegistrationAccountNeverGetsTheHint() async {
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]
        let model = await makeModel(state: state)

        model.noteTokenHint([activated(Sample.groupKey), activated(Sample.azureKey)])
        #expect(model.tokenHint == nil, "an app registration has its own token cache; Elevate cannot know whether the tools were ever used as this account")
        cleanup(model)
    }
}
