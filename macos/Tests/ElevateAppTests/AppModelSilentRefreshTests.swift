import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// Background refreshes (timer, wake, launch, panel open) never open a browser or an auth sheet.
/// A tenant whose silent token acquisition fails is flagged instead, and only a user-initiated
/// refresh may prompt for it.
@MainActor
struct AppModelSilentRefreshTests {
    private static func stateWithTenant(accessPackages: Bool = false) -> AppState {
        var state = AppState()
        state.identities = [Sample.identity()]
        var tenant = Sample.tenant()
        tenant.accessPackagesAvailable = accessPackages
        state.tenants = [tenant]
        return state
    }

    @Test func backgroundRefreshNeverPromptsWhenSilentAcquisitionFails() async {
        let tokens = FakeTokenProvider()
        await tokens.setSilentError(.interactionRequired)
        let model = await makeModel(state: Self.stateWithTenant(), online: true, tokens: tokens)
        await model.refreshAll()
        #expect(await tokens.interactiveCalls.isEmpty)
        #expect(await !tokens.silentCalls.isEmpty)
        #expect(model.tenantsAwaitingSignIn.contains(Sample.tenantKey))
        // Not a failure the user can do anything about from the error glyph; it is a pending sign-in.
        #expect(model.tenantErrors.isEmpty)
        cleanup(model)
    }

    @Test func backgroundRefreshTreatsAClaimsChallengeAsAPendingSignIn() async {
        let tokens = FakeTokenProvider()
        await tokens.setSilentError(.claimsChallenge("{\"access_token\":{}}"))
        let model = await makeModel(state: Self.stateWithTenant(), online: true, tokens: tokens)
        await model.refreshAll()
        #expect(await tokens.interactiveCalls.isEmpty)
        #expect(model.tenantsAwaitingSignIn.contains(Sample.tenantKey))
        #expect(model.tenantErrors.isEmpty)
        cleanup(model)
    }

    @Test func userRefreshPromptsAndClearsThePendingSignIn() async {
        let tokens = FakeTokenProvider()
        await tokens.setSilentError(.interactionRequired)
        let model = await makeModel(state: Self.stateWithTenant(), online: true, tokens: tokens)
        await model.refreshAll()
        #expect(model.tenantsAwaitingSignIn.contains(Sample.tenantKey))
        await model.refreshAll(userInitiated: true)
        #expect(await !tokens.interactiveCalls.isEmpty)
        #expect(!model.tenantsAwaitingSignIn.contains(Sample.tenantKey))
        cleanup(model)
    }

    @Test func backgroundRefreshThatSucceedsClearsThePendingSignIn() async {
        let tokens = FakeTokenProvider()
        await tokens.setSilentError(.interactionRequired)
        let model = await makeModel(state: Self.stateWithTenant(), online: true, tokens: tokens)
        await model.refreshAll()
        #expect(model.tenantsAwaitingSignIn.contains(Sample.tenantKey))
        // The session recovered on its own (e.g. the refresh token became usable again).
        await tokens.setSilentError(nil)
        await model.refreshAll()
        #expect(await tokens.interactiveCalls.isEmpty)
        #expect(!model.tenantsAwaitingSignIn.contains(Sample.tenantKey))
        cleanup(model)
    }

    @Test func backgroundAccessPackagePollNeverPrompts() async {
        let tokens = FakeTokenProvider()
        await tokens.setSilentError(.interactionRequired)
        let model = await makeModel(state: Self.stateWithTenant(accessPackages: true), online: true, tokens: tokens)
        await model.pollAccessPackagesIfDue(force: true)
        #expect(await tokens.interactiveCalls.isEmpty)
        #expect(await !tokens.silentCalls.isEmpty)
        cleanup(model)
    }

    @Test func openingAccessPackagesMayPrompt() async {
        let tokens = FakeTokenProvider()
        await tokens.setSilentError(.interactionRequired)
        let model = await makeModel(state: Self.stateWithTenant(accessPackages: true), online: true, tokens: tokens)
        await model.pollAccessPackages(Sample.tenantKey)
        #expect(await !tokens.interactiveCalls.isEmpty)
        cleanup(model)
    }
}
