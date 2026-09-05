import Testing
import Foundation
@testable import ElevateCore

@Suite struct CountdownTests {
    let now = Date(timeIntervalSince1970: 1_000_000)

    @Test func remainingIsNilWhenExpired() {
        #expect(Countdown.remaining(until: now.addingTimeInterval(-1), now: now) == nil)
    }

    @Test func labelFormatsHoursMinutes() {
        #expect(Countdown.label(.seconds(2 * 3600 + 41 * 60 + 10)) == "02:41")
        #expect(Countdown.label(.seconds(59)) == "00:00")
        #expect(Countdown.label(.seconds(5 * 60)) == "00:05")
    }

    @Test func remainingRoundsDownToSeconds() {
        let r = Countdown.remaining(until: now.addingTimeInterval(125.9), now: now)
        #expect(r == .seconds(125))
    }
}
