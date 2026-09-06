import Foundation
import Testing
import ElevateCore
@testable import Elevate

@MainActor
struct AppModelUpdatesTests {
    @Test func automaticCheckIsThrottledToOnceADay() async {
        let http = StubHTTPClient()
        await http.on("GET", "api.github.com", status: 404)
        // Online, so only the 24 h throttle can hold the check back. The timestamp is set before
        // bootstrap because bootstrap fires an automatic check of its own.
        let settings = makeSettings()
        settings.lastUpdateCheck = .now
        let model = await makeModel(http: http, online: true, settings: settings)

        await model.checkForUpdates()

        #expect(await http.requests(matching: "api.github.com").isEmpty)
        #expect(model.updateCheckMessage == nil)
    }

    @Test func forcedCheckAsksGitHubEvenWhenThrottledAndReportsNoReleases() async {
        let http = StubHTTPClient()
        await http.on("GET", "api.github.com", status: 404)
        let model = await makeModel(http: http)
        model.settings.lastUpdateCheck = .now

        await model.checkForUpdates(force: true)

        let requests = await http.requests(matching: "api.github.com")
        #expect(requests.count == 1)
        #expect(requests.first?.url == UpdateChecker.latestReleaseURL)
        #expect(requests.first?.headers["Accept"] == "application/vnd.github+json")
        #expect(requests.first?.headers["User-Agent"] == "Elevate")
        // 404 is "no release published yet", not a failure.
        #expect(model.updateCheckMessage == "No releases yet")
        #expect(model.updateAvailable == nil)
    }

    @Test func aNewerReleaseRaisesTheBannerUntilItIsDismissed() async {
        let http = StubHTTPClient()
        let body = Data(#"{"tag_name":"v999.0.0","html_url":"https://example.com/release"}"#.utf8)
        await http.on("GET", "api.github.com", status: 200, body: body)
        let model = await makeModel(http: http)

        await model.checkForUpdates(force: true)

        #expect(model.updateCheckMessage == "Elevate 999.0.0 is available")
        #expect(model.updateAvailable?.version == "999.0.0")

        model.dismissUpdate()
        #expect(model.updateAvailable == nil)
        #expect(model.settings.dismissedUpdateVersion == "999.0.0")
    }
}
