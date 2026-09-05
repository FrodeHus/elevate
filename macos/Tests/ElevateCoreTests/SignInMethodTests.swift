import Testing
import Foundation
@testable import ElevateCore

@Suite struct SignInMethodTests {
    @Test func firstPartyClientIds() {
        #expect(SignInMethod.ownApp.clientId == nil)
        #expect(SignInMethod.azureCLI.clientId == "04b07795-8ddb-461a-bbee-02f9e1bf7b46")
        #expect(SignInMethod.azurePowerShell.clientId == "1950a258-227b-4e31-a9cf-717495945fc2")
        #expect(SignInMethod.ownApp.usesMSAL && !SignInMethod.azureCLI.usesMSAL)
        #expect(SignInMethod.builtIn.count == 3)
        #expect(SignInMethod.custom(clientId: "abc").clientId == "abc")
        #expect(SignInMethod.custom(clientId: "abc").isCustom && !SignInMethod.custom(clientId: "abc").usesMSAL)
    }

    @Test func customMethodRoundTripsAndKeepsLegacyKeys() throws {
        let custom = SignInMethod.custom(clientId: "11111111-2222-3333-4444-555555555555")
        let encoded = String(decoding: try JSONEncoder().encode(custom), as: UTF8.self)
        #expect(encoded == "\"custom:11111111-2222-3333-4444-555555555555\"")
        #expect(try JSONDecoder().decode(SignInMethod.self, from: Data(encoded.utf8)) == custom)
        #expect(try JSONDecoder().decode(SignInMethod.self, from: Data("\"azureCLI\"".utf8)) == .azureCLI)
        #expect(SignInMethod(storageKey: "custom:") == nil)
        #expect(throws: DecodingError.self) { try JSONDecoder().decode(SignInMethod.self, from: Data("\"bogus\"".utf8)) }
        // A custom app is assumed capable of Entra activation until its token proves otherwise.
        #expect(custom.isPreauthorisedForEntraActivation && custom.limitationSummary == nil)
    }

    @Test func identityDefaultsToOwnAppWhenFieldMissing() throws {
        let json = #"{"id":"oid.tid","upn":"u@x","displayName":"U","homeTenantId":"tid"}"#
        let i = try JSONDecoder().decode(Identity.self, from: Data(json.utf8))
        #expect(i.signInMethod == .ownApp)
        let round = try JSONDecoder().decode(Identity.self, from: JSONEncoder().encode(Identity(id: "a.b", upn: "u", displayName: "U", homeTenantId: "b", signInMethod: .azureCLI)))
        #expect(round.signInMethod == .azureCLI)
    }

    @Test func fakeProviderSignsInWithMethod() async throws {
        let tokens = FakeTokenProvider()
        let i = try await tokens.signIn(method: .azurePowerShell)
        #expect(i.signInMethod == .azurePowerShell)
    }
}
