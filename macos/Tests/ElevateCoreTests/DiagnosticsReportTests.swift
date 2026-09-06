import Testing
import Foundation
@testable import ElevateCore

@Suite struct DiagnosticsReportTests {
    private func makeInput(
        errors: [DiagnosticsError] = [],
        accounts: [DiagnosticsAccount] = [],
        tenants: [DiagnosticsTenant] = [],
        profiles: [String] = [],
        hotKey: String? = nil
    ) -> DiagnosticsInput {
        DiagnosticsInput(
            appVersion: "1.2.3",
            build: "45",
            signing: "Developer ID",
            os: "macOS 15.1",
            accounts: accounts,
            tenants: tenants,
            profiles: profiles,
            hotKey: hotKey,
            errors: errors
        )
    }

    @Test func headerLinesPresent() {
        let text = DiagnosticsReport.render(makeInput())
        #expect(text.contains("1.2.3"))
        #expect(text.contains("45"))
        #expect(text.contains("Developer ID"))
        #expect(text.contains("macOS 15.1"))
    }

    @Test func accountsSectionRendered() {
        let input = makeInput(accounts: [
            DiagnosticsAccount(upn: "alice@contoso.com", method: "MSAL", tenantCount: 2)
        ])
        let text = DiagnosticsReport.render(input)
        #expect(text.contains("alice@contoso.com"))
        #expect(text.contains("MSAL"))
        #expect(text.contains("2"))
    }

    @Test func tenantsSectionRendered() {
        let input = makeInput(tenants: [
            DiagnosticsTenant(name: "Contoso", id: "tenant-id-1", mode: "auto", flags: ["azure", "groups"])
        ])
        let text = DiagnosticsReport.render(input)
        #expect(text.contains("Contoso"))
        #expect(text.contains("tenant-id-1"))
        #expect(text.contains("auto"))
        #expect(text.contains("azure"))
        #expect(text.contains("groups"))
    }

    @Test func profilesAndHotKeyRendered() {
        let input = makeInput(profiles: ["Default", "Work"], hotKey: "cmd+shift+e")
        let text = DiagnosticsReport.render(input)
        #expect(text.contains("Default"))
        #expect(text.contains("Work"))
        #expect(text.contains("cmd+shift+e"))
    }

    @Test func noHotKeyRendersPlaceholder() {
        let input = makeInput(hotKey: nil)
        let text = DiagnosticsReport.render(input)
        #expect(text.contains("None") || text.contains("none") || text.contains("—"))
    }

    @Test func errorsRenderedWithISO8601UTCTimestamps() {
        let date = Date(timeIntervalSince1970: 1_700_000_000)
        let input = makeInput(errors: [DiagnosticsError(date: date, message: "boom")])
        let text = DiagnosticsReport.render(input)
        #expect(text.contains("boom"))
        // ISO-8601 UTC representation of the fixed timestamp above.
        let formatter = ISO8601DateFormatter()
        formatter.timeZone = TimeZone(identifier: "UTC")
        let expected = formatter.string(from: date)
        #expect(text.contains(expected))
    }

    @Test func errorMessageWithSecretIsRenderedVerbatim() {
        let input = makeInput(errors: [
            DiagnosticsError(date: .now, message: "auth failed token=abc123")
        ])
        let text = DiagnosticsReport.render(input)
        #expect(text.contains("token=abc123"))
    }

    @Test func rendererNeverContainsUnpassedClientId() {
        // The input type has no field for a client id / secret at all, so a
        // client-id-looking string that was never supplied cannot appear.
        let clientIdLooking = "9d3a7e2c-4b1f-4a6e-9c2d-5f8b1c0a7e3d"
        let text = DiagnosticsReport.render(makeInput())
        #expect(!text.contains(clientIdLooking))
    }

    @Test func multipleErrorsRenderedInOrder() {
        let d1 = Date(timeIntervalSince1970: 100)
        let d2 = Date(timeIntervalSince1970: 200)
        let input = makeInput(errors: [
            DiagnosticsError(date: d1, message: "first error"),
            DiagnosticsError(date: d2, message: "second error")
        ])
        let text = DiagnosticsReport.render(input)
        let firstRange = text.range(of: "first error")
        let secondRange = text.range(of: "second error")
        #expect(firstRange != nil && secondRange != nil)
        if let f = firstRange, let s = secondRange {
            #expect(f.lowerBound < s.lowerBound)
        }
    }
}
