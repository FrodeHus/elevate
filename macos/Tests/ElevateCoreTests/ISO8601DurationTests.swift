import Testing
import Foundation
@testable import ElevateCore

@Suite struct ISO8601DurationTests {
    @Test func parsesHoursMinutes() {
        #expect(ISO8601Duration.parse("PT8H") == .seconds(8 * 3600))
        #expect(ISO8601Duration.parse("PT30M") == .seconds(1800))
        #expect(ISO8601Duration.parse("PT1H30M") == .seconds(5400))
        #expect(ISO8601Duration.parse("P1D") == .seconds(86400))
        #expect(ISO8601Duration.parse("garbage") == nil)
    }

    @Test func formatsAsHoursAndMinutes() {
        #expect(ISO8601Duration.format(.seconds(8 * 3600)) == "PT8H")
        #expect(ISO8601Duration.format(.seconds(1800)) == "PT30M")
        #expect(ISO8601Duration.format(.seconds(5400)) == "PT1H30M")
    }

    @Test func decoderAcceptsFractionalAndPlainDates() throws {
        struct Box: Decodable { let d: Date }
        let plain = try GraphJSON.decoder.decode(Box.self, from: Data(#"{"d":"2026-09-04T08:00:00Z"}"#.utf8))
        let frac = try GraphJSON.decoder.decode(Box.self, from: Data(#"{"d":"2026-09-04T08:00:00.1234567Z"}"#.utf8))
        #expect(abs(plain.d.timeIntervalSince(frac.d)) < 1)
    }
}
