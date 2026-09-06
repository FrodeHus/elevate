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
        #expect(model.adminConsentURL(identityId: Sample.identityId, tenantId: Sample.tenantId) == nil)
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
