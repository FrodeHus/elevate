import Testing
import Foundation
@testable import ElevateCore

@Suite struct ClaimsChallengeTests {
    @Test func extractsAndDecodesClaims() {
        let json = #"{"access_token":{"acrs":{"essential":true,"values":["c1"]}}}"#
        let b64 = Data(json.utf8).base64EncodedString().trimmingCharacters(in: CharacterSet(charactersIn: "="))
        let header = #"Bearer realm="", authorization_uri="https://login.microsoftonline.com/common/oauth2/authorize", error="insufficient_claims", claims="\#(b64)""#
        #expect(ClaimsChallenge.parse(wwwAuthenticate: header) == json)
    }

    @Test func returnsNilWithoutClaims() {
        #expect(ClaimsChallenge.parse(wwwAuthenticate: #"Bearer realm="""#) == nil)
    }

    @Test func interactionRetryRunsInteractiveThenRetries() async throws {
        let tokens = FakeTokenProvider()
        let identity = Identity(id: "i", upn: "u@x", displayName: "U", homeTenantId: "t")
        var calls = 0
        let result = try await InteractionRetry.run(tokens: tokens, identity: identity, tenantId: "t", scopes: GraphScopes.all) {
            calls += 1
            if calls == 1 { throw PIMError.claimsChallenge("{}") }
            return "ok"
        }
        #expect(result == "ok")
        #expect(calls == 2)
        #expect(await tokens.interactiveCalls.count == 1)
        #expect(await tokens.interactiveCalls.first?.claims == "{}")
    }
}
