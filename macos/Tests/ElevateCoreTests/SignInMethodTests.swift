import Testing
import Foundation
@testable import ElevateCore

@Suite struct SignInMethodTests {
    @Test func firstPartyClientIds() {
        #expect(SignInMethod.ownApp.clientId == nil)
        #expect(SignInMethod.azureCLI.clientId == "04b07795-8ddb-461a-bbee-02f9e1bf7b46")
        #expect(SignInMethod.azurePowerShell.clientId == "1950a258-227b-4e31-a9cf-717495945fc2")
        #expect(SignInMethod.ownApp.usesMSAL && !SignInMethod.azureCLI.usesMSAL)
        #expect(SignInMethod.allCases.count == 3)
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
