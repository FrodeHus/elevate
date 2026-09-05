import Testing
import Foundation
@testable import ElevateCore

@Suite struct ScheduledStartTests {
    let now = Date(timeIntervalSince1970: 1_000_000)

    @Test func isFutureOnlyBeyondTheHorizon() {
        #expect(ScheduledStart.horizon == 60)
        #expect(ScheduledStart.isFuture(now.addingTimeInterval(61), now: now))
        #expect(!ScheduledStart.isFuture(now.addingTimeInterval(60), now: now))
        #expect(!ScheduledStart.isFuture(now.addingTimeInterval(59), now: now))
        #expect(!ScheduledStart.isFuture(now.addingTimeInterval(-3600), now: now))
        #expect(!ScheduledStart.isFuture(nil, now: now))
    }

    @Test func settledOrPendingIgnoresCase() {
        #expect(ScheduledStart.isSettledOrPending("pendingapproval"))
        #expect(ScheduledStart.isSettledOrPending("PENDINGADMINDECISION"))
        #expect(ScheduledStart.isSettledOrPending("PendingApprovalProvisioning"))
    }

    @Test func settledOrPendingCoversDeniedFailedAndCancelled() {
        for status in ["Denied", "AdminDenied", "Failed", "FailedAsResourceIsLocked",
                       "Canceled", "Cancelled", "Revoked", "TimedOut", "Invalid"] {
            #expect(ScheduledStart.isSettledOrPending(status), "\(status) should not read as scheduled")
        }
    }

    @Test func settledOrPendingIsFalseForProvisionedAndNil() {
        #expect(!ScheduledStart.isSettledOrPending("Provisioned"))
        #expect(!ScheduledStart.isSettledOrPending("Granted"))
        #expect(!ScheduledStart.isSettledOrPending(nil))
    }

    @Test func effectivePrefersAFutureResponse() {
        let response = now.addingTimeInterval(7200)
        let requested = now.addingTimeInterval(3600)
        #expect(ScheduledStart.effective(response: response, requested: requested, now: now) == response)
    }

    @Test func effectiveFallsBackToTheRequestedStartWhenTheResponseIsNow() {
        let requested = now.addingTimeInterval(3600)
        #expect(ScheduledStart.effective(response: now, requested: requested, now: now) == requested)
    }

    @Test func effectiveUsesTheResponseWhenBothArePast() {
        let response = now.addingTimeInterval(-10)
        let requested = now.addingTimeInterval(-3600)
        #expect(ScheduledStart.effective(response: response, requested: requested, now: now) == response)
    }

    @Test func effectiveHandlesMissingDates() {
        let requested = now.addingTimeInterval(-3600)
        #expect(ScheduledStart.effective(response: nil, requested: requested, now: now) == requested)
        let response = now.addingTimeInterval(-30)
        #expect(ScheduledStart.effective(response: response, requested: nil, now: now) == response)
        #expect(ScheduledStart.effective(response: nil, requested: nil, now: now) == now)
    }
}
