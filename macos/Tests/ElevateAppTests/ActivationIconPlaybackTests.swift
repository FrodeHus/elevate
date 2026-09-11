import Testing
import Foundation
import ElevateCore
@testable import Elevate

struct ActivationIconPlaybackTests {
    @MainActor @Test func onlyConfirmedActivationShowsSuccessEvenWhileBatchIsRunning() {
        let assignment = ActiveAssignment(roleKey: Sample.entraKey, assignmentId: "a", startDateTime: .now,
                                          endDateTime: nil, status: .active)
        #expect(ActivationProgressLabel(result: .activated(assignment), running: true).phase == .success)
        for result: ActivationOutcome.Result in [.pendingApproval(assignment), .scheduled(assignment), .failed(.unexpected(status: 500, body: "Failed"))] {
            #expect(ActivationProgressLabel(result: result, running: true).phase == .hidden)
        }
        #expect(ActivationProgressLabel(result: nil, running: true).phase == .working)
        #expect(ActivationProgressLabel(result: nil, running: false).phase == .hidden)
    }

    @Test func tracedMotionIsBundledAndCircleCloses() throws {
        let motion = try #require(ElevationMotion.shared)
        #expect(motion.frames.count == 29)
        let final = try #require(motion.frames.last)
        // Upper ribbon's first and last outer points must meet at six o'clock.
        #expect(abs(final.upper[0][0] - final.upper[80][0]) < 0.01)
        #expect(abs(final.upper[0][1] - final.upper[80][1]) < 0.01)
        #expect(motion.frames.first?.upper != final.upper)
    }

    @Test func longRunningRequestNeverBecomesSuccessByTimeAlone() {
        var playback = ActivationIconPlayback()
        playback.update(.working, at: 0)
        let frame = playback.sample(at: 86_400, reduceMotion: false)
        #expect(frame.morphTime == nil)
        #expect(frame.animating)
    }

    @Test func confirmedSuccessMorphsOnceAndStops() {
        var playback = ActivationIconPlayback()
        playback.update(.working, at: 0)
        playback.update(.success, at: 10)
        #expect(abs((playback.sample(at: 10.7, reduceMotion: false).morphTime ?? 0) - 0.7) < 0.0001)
        playback.update(.success, at: 11) // An unrelated model update must not restart it.
        let frame = playback.sample(at: 12, reduceMotion: false)
        #expect(frame.morphTime == ActivationIconPlayback.morphDuration)
        #expect(!frame.animating)
    }

    @Test func retryResetsAndReducedMotionIsStatic() {
        var playback = ActivationIconPlayback()
        playback.update(.success, at: 0)
        #expect(!playback.sample(at: 0, reduceMotion: false).animating)
        playback.update(.working, at: 5)
        #expect(playback.sample(at: 5, reduceMotion: false).morphTime == nil)
        #expect(!playback.sample(at: 6, reduceMotion: true).animating)
        playback.update(.success, at: 7)
        #expect(playback.sample(at: 7, reduceMotion: true).morphTime == ActivationIconPlayback.morphDuration)
    }
}
