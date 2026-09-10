import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// The project-provided shared Elevate app registration: `AppSettings.usesSharedClientId`,
/// `AppModel.usesSharedApp`/`sharedAppAdminConsentURL()`, and the diagnostics line that names it
/// without ever printing the id itself.
@MainActor
struct AppModelSharedAppTests {
    @Test func usesSharedClientIdMatchesAnyCaseWithWhitespace() {
        let settings = makeSettings()
        settings.clientId = "  \(AppSettings.sharedClientId.uppercased())  "
        #expect(settings.usesSharedClientId)
    }

    @Test func usesSharedClientIdFalseForAnotherGUID() {
        let settings = makeSettings()
        settings.clientId = "11111111-2222-3333-4444-555555555555"
        #expect(!settings.usesSharedClientId)
    }

    @Test func usesSharedClientIdFalseForEmpty() {
        let settings = makeSettings()
        settings.clientId = ""
        #expect(!settings.usesSharedClientId)
    }

    @Test func sharedAppAdminConsentURLIsNilWhenUnconfigured() async {
        let settings = makeSettings()
        // Explicitly blank: `AppSettings` migrates a client id from the legacy defaults suite when
        // it finds one, so a fresh test suite is not necessarily unconfigured.
        settings.clientId = ""
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        #expect(!model.isConfigured)
        #expect(model.sharedAppAdminConsentURL() == nil)
        cleanup(model)
    }

    @Test func sharedAppAdminConsentURLWhenConfiguredWithSharedId() async {
        let settings = makeSettings()
        settings.clientId = AppSettings.sharedClientId
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        #expect(model.isConfigured)
        #expect(model.usesSharedApp)
        let url = model.sharedAppAdminConsentURL()
        #expect(url != nil)
        let components = URLComponents(url: url!, resolvingAgainstBaseURL: false)
        #expect(components?.host == "login.microsoftonline.com")
        #expect(components?.path == "/organizations/v2.0/adminconsent")
        let items = components?.queryItems ?? []
        #expect(items.first { $0.name == "client_id" }?.value == AppSettings.sharedClientId)
        #expect(items.first { $0.name == "redirect_uri" }?.value == AppSettings.sharedConsentRedirectURI)
        let scope = items.first { $0.name == "scope" }?.value ?? ""
        #expect(scope.contains("RoleAssignmentSchedule.ReadWrite.Directory"))
        cleanup(model)
    }

    @Test func sharedAppAdminConsentURLIsNilWhenConfiguredWithAnotherGUID() async {
        let settings = makeSettings()
        settings.clientId = "11111111-2222-3333-4444-555555555555"
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        #expect(model.isConfigured)
        #expect(!model.usesSharedApp)
        #expect(model.sharedAppAdminConsentURL() == nil)
        cleanup(model)
    }

    @Test func adminConsentURLStillUsesTenantIdInPath() async {
        let settings = makeSettings()
        settings.clientId = AppSettings.sharedClientId
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        model.state.identities = [Sample.identity(method: .ownApp)]
        let url = model.adminConsentURL(identityId: Sample.identityId, tenantId: Sample.tenantId)
        #expect(url != nil)
        #expect(URLComponents(url: url!, resolvingAgainstBaseURL: false)?.path == "/\(Sample.tenantId)/v2.0/adminconsent")
        cleanup(model)
    }

    @Test func sharedAppAdminConsentURLUsesConsentPageRedirect() async {
        let settings = makeSettings()
        settings.clientId = AppSettings.sharedClientId
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        let url = model.sharedAppAdminConsentURL()
        #expect(url != nil)
        let items = URLComponents(url: url!, resolvingAgainstBaseURL: false)?.queryItems ?? []
        #expect(items.first { $0.name == "redirect_uri" }?.value == AppSettings.sharedConsentRedirectURI)
        cleanup(model)
    }

    @Test func adminConsentURLUsesConsentPageRedirectForSharedApp() async {
        let settings = makeSettings()
        settings.clientId = AppSettings.sharedClientId
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        model.state.identities = [Sample.identity(method: .ownApp)]
        let url = model.adminConsentURL(identityId: Sample.identityId, tenantId: Sample.tenantId)
        #expect(url != nil)
        let items = URLComponents(url: url!, resolvingAgainstBaseURL: false)?.queryItems ?? []
        #expect(items.first { $0.name == "redirect_uri" }?.value == AppSettings.sharedConsentRedirectURI)
        cleanup(model)
    }

    @Test func adminConsentURLKeepsNativeClientRedirectForOwnRegistration() async {
        let settings = makeSettings()
        settings.clientId = "11111111-2222-3333-4444-555555555555"
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        model.state.identities = [Sample.identity(method: .ownApp)]
        #expect(!model.usesSharedApp)
        let url = model.adminConsentURL(identityId: Sample.identityId, tenantId: Sample.tenantId)
        #expect(url != nil)
        let items = URLComponents(url: url!, resolvingAgainstBaseURL: false)?.queryItems ?? []
        #expect(items.first { $0.name == "redirect_uri" }?.value == "https://login.microsoftonline.com/common/oauth2/nativeclient")
        cleanup(model)
    }

    @Test func diagnosticsTextNamesSharedAppWithoutTheIdItself() async {
        let settings = makeSettings()
        settings.clientId = AppSettings.sharedClientId
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        let text = model.diagnosticsText()
        #expect(text.contains("Client id: shared Elevate app"))
        #expect(!text.contains(AppSettings.sharedClientId))
        cleanup(model)
    }

    @Test func diagnosticsTextShowsOwnRegistrationForOtherClientId() async {
        let settings = makeSettings()
        settings.clientId = "11111111-2222-3333-4444-555555555555"
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        let text = model.diagnosticsText()
        #expect(!text.contains("Client id: shared Elevate app"))
        #expect(text.contains("Client id: own registration"))
        #expect(!text.contains("Client id: not set"))
        cleanup(model)
    }

    @Test func diagnosticsTextShowsNotSetWhenUnconfigured() async {
        let settings = makeSettings()
        settings.clientId = ""
        let model = await makeModel(settings: settings, ownAppViaLoopback: true)
        #expect(!model.isConfigured)
        let text = model.diagnosticsText()
        #expect(text.contains("Client id: not set"))
        #expect(!text.contains("Client id: shared Elevate app"))
        #expect(!text.contains("Client id: own registration"))
        cleanup(model)
    }
}
