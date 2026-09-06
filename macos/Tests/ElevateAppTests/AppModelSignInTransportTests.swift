import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// How the own-app registration is transported: MSAL on a signed build, the loopback PKCE flow
/// with the Settings client id on an unsigned one. `BuildInfo.signingState` describes the test
/// host, so the model's `ownAppViaLoopbackOverride` stands in for it here.
@MainActor
struct AppModelSignInTransportTests {
    private static let clientId = "11111111-2222-3333-4444-555555555555"

    @Test func ownAppIsAvailableThroughLoopbackOnUnsignedBuilds() async {
        let settings = makeSettings()
        settings.clientId = Self.clientId
        // No MSAL provider — `makeModel` never builds one, exactly as `live()` does on ad-hoc.
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        #expect(model.ownAppViaLoopback)
        #expect(model.isConfigured)
        #expect(model.isAvailable(.ownApp))
        // Tokens are keyed by the client id; the identity is still recorded as `.ownApp`.
        #expect(model.ownAppLoopbackProvider?.method == .custom(clientId: Self.clientId))
        #expect(model.ownAppLoopbackProvider?.reportedMethod == .ownApp)
        // Admin consent is about the registration, not the transport, so it works here too.
        // The identity is set after `bootstrap()`, which would otherwise drop an own-app account
        // with no refresh token in the loopback store.
        model.state.identities = [Sample.identity(method: .ownApp)]
        let consent = model.adminConsentURL(identityId: Sample.identityId, tenantId: Sample.tenantId)
        #expect(consent != nil)
        #expect(consent?.absoluteString.contains(Self.clientId) == true)
        #expect(consent?.absoluteString.contains(Sample.tenantId) == true)
        cleanup(model)
    }

    @Test func ownAppStaysUnavailableWithoutMSALOnSignedBuilds() async {
        let settings = makeSettings()
        settings.clientId = Self.clientId
        let model = await makeModel(settings: settings, ownAppViaLoopback: false)
        #expect(!model.isConfigured)
        #expect(!model.isAvailable(.ownApp))
        #expect(model.ownAppLoopbackProvider == nil)
        cleanup(model)
    }

    /// The `.ownApp` stand-in and a `.custom` account over the same Settings client id share one
    /// keychain item ("<clientId>|<identityId>"), so refusing the duplicate must not sign the new
    /// identity out — that would delete the existing account's refresh token.
    @Test func duplicateAccountOnTheSameClientIdKeepsTheSharedRefreshToken() async {
        let settings = makeSettings()
        settings.clientId = Self.clientId
        let tokens = FakeTokenProvider()
        let model = await makeModel(settings: settings, tokens: tokens, ownAppViaLoopback: true)
        // `FakeTokenProvider.signIn` always returns the identity id "new".
        model.state.identities = [Sample.identity("new", method: .custom(clientId: Self.clientId))]
        let added = await model.addAccount(method: .ownApp)
        #expect(!added)
        #expect(model.notice?.contains("already added") == true)
        #expect(await tokens.signOutCalls.isEmpty)
        cleanup(model)
    }

    /// A duplicate under a method with a *different* client id owns its own keychain item, so the
    /// sign-in that was just made is still discarded.
    @Test func duplicateAccountOnAnotherClientIdIsSignedOut() async {
        let settings = makeSettings()
        settings.clientId = Self.clientId
        let tokens = FakeTokenProvider()
        let model = await makeModel(settings: settings, tokens: tokens, ownAppViaLoopback: true)
        model.state.identities = [Sample.identity("new", method: .azureCLI)]
        let added = await model.addAccount(method: .ownApp)
        #expect(!added)
        #expect(await tokens.signOutCalls == ["new"])
        cleanup(model)
    }

    @Test func loopbackTransportNeedsAClientId() async {
        let settings = makeSettings()
        // Explicitly blank: `AppSettings` migrates a client id from the legacy defaults suite when
        // it finds one, so a fresh test suite is not necessarily unconfigured.
        settings.clientId = ""
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        #expect(!model.isConfigured)
        #expect(!model.isAvailable(.ownApp))
        #expect(model.ownAppLoopbackProvider == nil)
        // The first-party methods never depended on the client id, either way.
        #expect(model.isAvailable(.azureCLI))
        cleanup(model)
    }
}
