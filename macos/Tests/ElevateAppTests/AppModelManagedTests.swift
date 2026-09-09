import Foundation
import Testing
import ElevateCore
@testable import Elevate

@Suite @MainActor
struct AppModelManagedTests {
    static let id = "11111111-2222-3333-4444-555555555555"

    @Test func managedClientIdConfiguresTheApp() async {
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": Self.id]))
        let model = await makeModel(managed: managed, ownAppViaLoopback: true)
        defer { cleanup(model) }

        #expect(model.settings.isClientIdManaged)
        #expect(model.settings.clientId == Self.id)
        #expect(model.isConfigured)

        // The user cannot write over a managed client id, in the settings object or through the model.
        model.settings.clientId = "22222222-2222-3333-4444-555555555555"
        #expect(model.settings.clientId == Self.id)
        #expect(throws: PIMError.self) { try model.applyClientId("22222222-2222-3333-4444-555555555555") }
    }

    @Test func userClientIdStillWorksWithoutManagement() async {
        let model = await makeModel(ownAppViaLoopback: true)
        defer { cleanup(model) }

        #expect(!model.settings.isClientIdManaged)
        model.settings.clientId = Self.id
        #expect(model.settings.storedClientId == Self.id)
        #expect(model.settings.clientId == Self.id)
        #expect(model.isConfigured)
    }

    @Test func disabledUpdateCheckNeverCallsGitHub() async {
        let http = StubHTTPClient()
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["DisableUpdateCheck": true]))
        let model = await makeModel(http: http, online: true, managed: managed)
        defer { cleanup(model) }

        await model.checkForUpdates(force: true)

        #expect(await http.requests(matching: "api.github.com").isEmpty)
        #expect(model.updateAvailable == nil)
        #expect(model.updateCheckMessage == nil)
    }

    @Test func diagnosticsListsManagedKeys() async {
        let managed = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": Self.id, "DisableUpdateCheck": true], origin: "unit"))
        let model = await makeModel(managed: managed)
        defer { cleanup(model) }

        let text = model.diagnosticsText()
        #expect(text.contains("Source: unit"))
        #expect(text.contains("Keys: ClientId, DisableUpdateCheck"))
        #expect(!text.contains(Self.id))
    }
}
