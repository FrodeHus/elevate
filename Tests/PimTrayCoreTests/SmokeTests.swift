import Testing
import Foundation
@testable import PimTrayCore

@Test func packageBuilds() {
    #expect(PimTrayCore.version == "0.1.0")
}

@Suite struct GraphJSONDateTests {
    @Test func parsesFractionalSecondVariants() {
        let plain = GraphJSON.parseDate("2026-09-04T08:00:00Z")
        #expect(plain != nil)
        #expect(GraphJSON.parseDate("2026-09-04T08:00:00.5Z") == plain?.addingTimeInterval(0.5))
        #expect(GraphJSON.parseDate("2026-09-04T08:00:00.50Z") == plain?.addingTimeInterval(0.5))
        #expect(GraphJSON.parseDate("2026-09-04T08:00:00.500Z") == plain?.addingTimeInterval(0.5))
        #expect(GraphJSON.parseDate("2026-09-04T08:00:00.5000000Z") == plain?.addingTimeInterval(0.5))
    }
}

@Suite struct GraphTransportErrorTests {
    @Test func earlyDeactivationMapsToClearPolicyMessage() {
        let body = Data(#"{"error":{"code":"ActiveDurationTooShort","message":"The role assignment cannot be deactivated within 5 minutes of activation."}}"#.utf8)
        let r = HTTPResponse(status: 400, headers: [:], body: body)
        #expect(GraphTransport.mapError(r) == .policyViolation("Entra requires a role to stay active for 5 minutes before it can be deactivated"))
    }

    @Test func throttlingMapsToNetworkErrorWithRetryAfter() {
        let r = HTTPResponse(status: 429, headers: ["Retry-After": "12"], body: Data())
        #expect(GraphTransport.mapError(r) == .network("Throttled by Microsoft Graph; retry in 12s"))
        let noHeader = HTTPResponse(status: 429, headers: [:], body: Data())
        #expect(GraphTransport.mapError(noHeader) == .network("Throttled by Microsoft Graph; retry in a few seconds"))
    }

    @Test func armForbiddenIsAPermissionFailureNotConsent() {
        let r = HTTPResponse(status: 403, headers: [:], body: Data(#"{"error":{"code":"AuthorizationFailed","message":"no"}}"#.utf8))
        #expect(GraphTransport.mapArmError(r) == .policyViolation("Not permitted at this scope"))
    }

    @Test func armSharesClaimsAndThrottlingMapping() {
        let claims = #"{"access_token":{"acrs":{"essential":true,"values":["c1"]}}}"#
        let b64 = Data(claims.utf8).base64EncodedString()
        let r = HTTPResponse(status: 401, headers: ["WWW-Authenticate": #"Bearer error="insufficient_claims", claims="\#(b64)""#], body: Data())
        #expect(GraphTransport.mapArmError(r) == .claimsChallenge(claims))
        #expect(GraphTransport.mapArmError(HTTPResponse(status: 429, headers: ["Retry-After": "3"], body: Data())) == .network("Throttled by Microsoft Graph; retry in 3s"))
        let early = HTTPResponse(status: 400, headers: [:], body: Data(#"{"error":{"code":"ActiveDurationTooShort","message":"x"}}"#.utf8))
        #expect(GraphTransport.mapArmError(early) == .policyViolation("Entra requires a role to stay active for 5 minutes before it can be deactivated"))
    }

    @Test func transportUsesInjectedMapperAndSupportsPut() async throws {
        let http = StubHTTPClient()
        await http.on("PUT", "example.test", status: 403, body: Data("{}".utf8))
        let t = GraphTransport(http: http, tokens: FakeTokenProvider(), mapper: GraphTransport.mapArmError)
        let identity = Identity(id: "i", upn: "u", displayName: "U", homeTenantId: "t")
        await #expect(throws: PIMError.policyViolation("Not permitted at this scope")) {
            _ = try await t.put(identity: identity, tenantId: "t", url: URL(string: "https://example.test/x")!, scopes: ArmScopes.all, body: Data("{}".utf8))
        }
        let req = await http.requests.first!
        #expect(req.method == "PUT")
        #expect(req.headers["Content-Type"] == "application/json")
        #expect(req.headers["Authorization"] == "Bearer token-t")
    }
}
