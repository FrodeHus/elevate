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
}
