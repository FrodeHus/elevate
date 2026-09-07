import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// An account whose saved sign-in is gone stays in the list with its tenants and configured roles,
/// flagged for "Sign in again", instead of being removed at launch. Removal is the user's call.
@MainActor
struct AppModelSignInRetryTests {
    private static let clientId = "11111111-2222-3333-4444-555555555555"

    private static func stateWithAccount(_ identity: Identity) -> AppState {
        var state = AppState()
        state.identities = [identity]
        state.upsertTenant(Sample.tenant(identityId: identity.id))
        state.manualRoles = [ManualRole(tenantKey: TenantKey(identityId: identity.id, tenantId: Sample.tenantId),
                                        scope: Sample.azureKey.scope, displayName: "Owner")]
        return state
    }

    @Test func accountWithoutSavedSignInIsKeptAndFlagged() async {
        // `makeModel` has no refresh token in the loopback store for this first-party account.
        let identity = Sample.identity(method: .azureCLI)
        let model = await makeModel(state: Self.stateWithAccount(identity))
        #expect(model.identities.map(\.id) == [identity.id])
        #expect(model.tenants(for: identity.id).map(\.tenantId) == [Sample.tenantId])
        #expect(model.state.manualRoles.count == 1)
        #expect(model.needsSignIn(identity.id))
        #expect(model.notice?.contains("needs to sign in again") == true)
        cleanup(model)
    }

    @Test func flaggedAccountIsNotRefreshed() async {
        let identity = Sample.identity(method: .azureCLI)
        let tokens = FakeTokenProvider()
        let model = await makeModel(state: Self.stateWithAccount(identity), online: true, tokens: tokens)
        await model.refreshAll(userInitiated: true)
        #expect(await tokens.silentCalls.isEmpty)
        #expect(await tokens.interactiveCalls.isEmpty)
        #expect(model.tenantErrors.isEmpty)
        cleanup(model)
    }

    @Test func retryWithTheSameAccountClearsTheFlagAndRefreshes() async {
        // `FakeTokenProvider.signIn` always returns the identity id "new".
        let identity = Sample.identity("new", method: .azureCLI)
        let tokens = FakeTokenProvider()
        let model = await makeModel(state: Self.stateWithAccount(identity), online: true, tokens: tokens)
        #expect(model.needsSignIn("new"))
        let ok = await model.retrySignIn(identity)
        #expect(ok)
        #expect(!model.needsSignIn("new"))
        #expect(model.identities.map(\.id) == ["new"])
        #expect(model.state.manualRoles.count == 1)
        #expect(await tokens.signOutCalls.isEmpty)
        // The account's tenant was read again once it became usable.
        #expect(await !tokens.silentCalls.isEmpty)
        cleanup(model)
    }

    @Test func retryWithAnotherAccountKeepsTheFlagAndDiscardsThatSignIn() async {
        let identity = Sample.identity("id-1", method: .azureCLI)
        let tokens = FakeTokenProvider()
        let model = await makeModel(state: Self.stateWithAccount(identity), tokens: tokens)
        let ok = await model.retrySignIn(identity)
        #expect(!ok)
        #expect(model.needsSignIn("id-1"))
        #expect(model.identities.map(\.id) == ["id-1"])
        #expect(await tokens.signOutCalls == ["new"])
        #expect(model.notice?.contains("was expected") == true)
        cleanup(model)
    }

    @Test func signOutStillRemovesAFlaggedAccount() async {
        let identity = Sample.identity(method: .azureCLI)
        let tokens = FakeTokenProvider()
        let model = await makeModel(state: Self.stateWithAccount(identity), tokens: tokens)
        model.signOut(identity)
        // `signOut` runs in its own task; give it a turn.
        for _ in 0..<50 where !model.identities.isEmpty { await Task.yield() }
        #expect(model.identities.isEmpty)
        #expect(!model.needsSignIn(identity.id))
        #expect(model.state.manualRoles.isEmpty)
        cleanup(model)
    }
}
