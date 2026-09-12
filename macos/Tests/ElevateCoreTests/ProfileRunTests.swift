import Foundation
import Testing
@testable import ElevateCore

@Suite struct ProfileRunTests {
    let key = RoleKey(identityId: "i", tenantId: "t", scope: .entraDirectory(roleDefinitionId: "r", directoryScopeId: "/"))
    let now = Date(timeIntervalSince1970: 1_800_000_000)
    func assignment() -> ActiveAssignment {
        ActiveAssignment(roleKey: key, assignmentId: "instance", startDateTime: now.addingTimeInterval(-600),
                         endDateTime: now.addingTimeInterval(3600), status: .active)
    }
    @Test func exactAssignmentOnlyAndCompletedNeverRetries() {
        let a = assignment()
        var entry = ProfileRun.Entry(assignment: a, displayName: "Reader")
        #expect(entry.disposition(current: a, now: now) == .ready)
        var replacement = a
        replacement.startDateTime = now
        #expect(entry.disposition(current: replacement, now: now) == .skipped("Assignment replaced"))
        replacement = a; replacement.assignmentId = "later"
        #expect(entry.disposition(current: replacement, now: now) == .skipped("Assignment replaced"))
        entry.completed = true
        #expect(entry.disposition(current: a, now: now) == .completed)
    }
    @Test func expiredMissingAndMinimumPeriodArePerEntry() {
        var a = assignment()
        let entry = ProfileRun.Entry(assignment: a, displayName: "Reader")
        #expect(entry.disposition(current: nil, now: now) == .skipped("Already inactive or expired"))
        #expect(entry.disposition(current: a, now: now.addingTimeInterval(3601)) == .skipped("Already inactive or expired"))
        a.startDateTime = now.addingTimeInterval(-100)
        #expect(ProfileRun.Entry(assignment: a, displayName: "Reader").disposition(current: a, now: now) == .blocked("Can be deactivated in 200 s (minimum activation period)"))
        a.assignmentId = nil
        #expect(ProfileRun.Entry(assignment: a, displayName: "Reader").disposition(current: a, now: now) == .blocked("Assignment identity could not be verified"))
    }
    @Test func pendingAssignmentAbsenceStaysRetryableAndRequestCorrelationSurvivesApproval() {
        var pending = assignment()
        pending.status = .pendingApproval; pending.endDateTime = nil
        pending.assignmentId = "request"
        let entry = ProfileRun.Entry(assignment: pending, displayName: "Reader")
        #expect(entry.disposition(current: nil, now: now) == .blocked("Awaiting active assignment confirmation"))
        var approved = assignment()
        approved.scheduleId = "schedule"
        approved.activationRequestId = "request"
        #expect(entry.disposition(current: approved, now: now) == .blocked("Original activation interval could not be verified; deactivate this role individually"))
        pending.endDateTime = approved.endDateTime
        let knownInterval = ProfileRun.Entry(assignment: pending, displayName: "Reader")
        #expect(knownInterval.disposition(current: approved, now: now) == .ready)
        approved.endDateTime = approved.endDateTime?.addingTimeInterval(3600)
        #expect(!knownInterval.matches(approved))
        approved.endDateTime = pending.endDateTime
        approved.activationRequestId = "different-request"
        #expect(knownInterval.disposition(current: approved, now: now) == .skipped("Assignment replaced"))
    }

    @Test func scheduleCorrelationSurvivesDifferentRequestAndInstanceIds() {
        var requested = assignment(); requested.assignmentId = "request"; requested.scheduleId = "schedule"
        var refreshed = requested; refreshed.assignmentId = "instance"
        let entry = ProfileRun.Entry(assignment: requested, displayName: "Reader")
        #expect(entry.disposition(current: refreshed, now: now) == .ready)
        refreshed.endDateTime = refreshed.endDateTime?.addingTimeInterval(3600)
        #expect(entry.disposition(current: refreshed, now: now) == .skipped("Assignment replaced"))
        refreshed.endDateTime = requested.endDateTime
        refreshed.scheduleId = "replacement"
        #expect(entry.disposition(current: refreshed, now: now) == .skipped("Assignment replaced"))
    }

    @Test func removingAnAccountRemovesItsRunAssignments() {
        var state = AppState()
        state.profileRuns = [ProfileRun(profileId: UUID(), profileName: "Ops", entries: [.init(assignment: assignment(), displayName: "Reader")])]
        state.removeIdentity(key.identityId)
        #expect(state.profileRuns.isEmpty)
    }

    @Test func runSnapshotsSurviveRestartProfileEditsAndRepeatedRuns() async throws {
        var state = AppState()
        let p = ActivationProfile(name: "Original", entries: [.init(roleKey: key)])
        state.upsertProfile(p)
        let first = ProfileRun(profileId: p.id, profileName: p.name, startedAt: now, entries: [.init(assignment: assignment(), displayName: "Reader")])
        state.profileRuns = [first, ProfileRun(profileId: p.id, profileName: p.name, startedAt: now.addingTimeInterval(1), entries: [])]
        state.profiles[0].entries = []; state.profiles[0].name = "Edited"
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        try await AppStateStore(directory: directory).save(state)
        let loaded = try await AppStateStore(directory: directory).load()
        #expect(loaded.profileRuns == state.profileRuns)
        #expect(loaded.profileRuns[0].profileName == "Original")
        #expect(loaded.profileRuns[0].entries[0].assignment == assignment())
        #expect(try GraphJSON.decoder.decode(AppState.self, from: Data("{}".utf8)).profileRuns.isEmpty)
    }
}
