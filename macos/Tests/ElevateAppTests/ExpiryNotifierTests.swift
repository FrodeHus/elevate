import Foundation
import Testing
@testable import Elevate

/// `UNUserNotificationCenter` itself is not unit-testable in this environment (it requires a
/// running app with notification authorization), so only the pure helper that decides which
/// package-expiry notifications to cancel is covered here.
struct ExpiryNotifierTests {
    @Test func staleIdsIsEmptyWhenNothingDropped() {
        #expect(ExpiryNotifier.staleIds(previous: ["a", "b"], current: ["a", "b", "c"]) == [])
    }

    @Test func staleIdsReturnsIdsNoLongerPresent() {
        #expect(ExpiryNotifier.staleIds(previous: ["a", "b", "c"], current: ["a"]) == ["b", "c"])
    }

    @Test func staleIdsWithEmptyCurrentReturnsAllPrevious() {
        #expect(ExpiryNotifier.staleIds(previous: ["a", "b"], current: []) == ["a", "b"])
    }

    @Test func staleIdsWithEmptyPreviousReturnsEmpty() {
        #expect(ExpiryNotifier.staleIds(previous: [], current: ["a"]) == [])
    }
}
