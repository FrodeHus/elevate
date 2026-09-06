import Testing
import Foundation
@testable import ElevateCore

@Suite struct AppVersionTests {
    @Test func parsesPlain() {
        let v = AppVersion("1.2.3")
        #expect(v?.major == 1 && v?.minor == 2 && v?.patch == 3)
    }

    @Test func parsesLeadingV() {
        let v = AppVersion("v1.2.3")
        #expect(v?.major == 1 && v?.minor == 2 && v?.patch == 3)
    }

    @Test func parsesBuildMetadata() {
        let v = AppVersion("1.2.3+45")
        #expect(v?.major == 1 && v?.minor == 2 && v?.patch == 3)
    }

    @Test func parsesMissingPatchAsZero() {
        let v = AppVersion("1.2")
        #expect(v?.major == 1 && v?.minor == 2 && v?.patch == 0)
    }

    @Test func rejectsGarbage() {
        #expect(AppVersion("") == nil)
        #expect(AppVersion("abc") == nil)
        #expect(AppVersion("1.x") == nil)
    }

    @Test func comparable() {
        #expect(AppVersion("1.2.3")! < AppVersion("1.2.4")!)
        #expect(AppVersion("1.2.3")! < AppVersion("1.3.0")!)
        #expect(AppVersion("1.2.3")! < AppVersion("2.0.0")!)
        #expect(AppVersion("1.2.3")! == AppVersion("1.2.3")!)
        #expect(!(AppVersion("1.2.3")! < AppVersion("1.2.3")!))
    }

    @Test func isNewerTrueWhenLatestGreater() {
        #expect(AppVersion.isNewer(latestTag: "v1.1.0", current: "1.0.0"))
        #expect(AppVersion.isNewer(latestTag: "1.0.1", current: "1.0.0"))
    }

    @Test func isNewerFalseWhenEqual() {
        #expect(!AppVersion.isNewer(latestTag: "v1.0.0", current: "1.0.0"))
    }

    @Test func isNewerFalseWhenOlder() {
        #expect(!AppVersion.isNewer(latestTag: "v0.9.0", current: "1.0.0"))
    }

    @Test func isNewerFalseWhenEitherFailsToParse() {
        #expect(!AppVersion.isNewer(latestTag: "garbage", current: "1.0.0"))
        #expect(!AppVersion.isNewer(latestTag: "v1.0.0", current: "garbage"))
        #expect(!AppVersion.isNewer(latestTag: "garbage", current: "also-garbage"))
    }
}
