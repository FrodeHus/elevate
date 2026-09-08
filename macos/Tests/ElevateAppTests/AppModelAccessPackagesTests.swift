import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// Records what the model asked the notifier to do.
actor RecordingNotifier: ExpiryNotifying {
    private(set) var notifications: [(title: String, body: String)] = []
    private(set) var packageExpiries: [PackageExpiry] = []
    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String], tenantNames: [TenantKey: String]) async {}
    func notify(title: String, body: String) async { notifications.append((title, body)) }
    func setPackageExpiries(_ expiries: [PackageExpiry]) async { packageExpiries = expiries }
}

@MainActor
struct AppModelAccessPackagesTests {
    private func modelWithTenant(http: StubHTTPClient, notifier: RecordingNotifier) async -> AppModel {
        var state = AppState()
        state.identities = [Sample.identity()]
        var tenant = Sample.tenant()
        tenant.accessPackagesAvailable = true
        state.tenants = [tenant]
        return await makeModel(state: state, http: http, online: true, notifier: notifier)
    }

    @Test func firstPollBaselinesWithoutNotifying() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        await http.on("GET", "assignmentRequests/filterByCurrentUser", body: Data(#"{"value":[{"id":"r1","state":"delivered","accessPackage":{"id":"p","displayName":"Pkg"}}]}"#.utf8))
        await http.on("GET", "assignments/filterByCurrentUser", body: Data(#"{"value":[]}"#.utf8))
        let model = await modelWithTenant(http: http, notifier: notifier)
        await model.pollAccessPackages(Sample.tenantKey)
        #expect(model.accessPackageSnapshot(Sample.tenantKey)?.requests.map(\.id) == ["r1"])
        #expect(await notifier.notifications.isEmpty)
        cleanup(model)
    }

    @Test func secondPollNotifiesApprovalAndSchedulesExpiry() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        var state = AppState()
        state.identities = [Sample.identity()]
        var tenant = Sample.tenant()
        tenant.accessPackagesAvailable = true
        state.tenants = [tenant]
        let pending = AccessPackageRequest(id: "r1", packageId: "p", packageName: "Pkg", requestType: "userAdd", state: .pendingApproval)
        state.setAccessPackages(Sample.tenantKey, snapshot: AccessPackageSnapshot(requests: [pending]), polledAt: .distantPast)
        await http.on("GET", "assignmentRequests/filterByCurrentUser", body: Data(#"{"value":[{"id":"r1","state":"delivered","accessPackage":{"id":"p","displayName":"Pkg"}}]}"#.utf8))
        await http.on("GET", "assignments/filterByCurrentUser", body: Data(#"{"value":[{"id":"a1","state":"delivered","schedule":{"expiration":{"endDateTime":"2030-01-01T00:00:00Z"}},"accessPackage":{"id":"p","displayName":"Pkg"}}]}"#.utf8))
        let model = await makeModel(state: state, http: http, online: true, notifier: notifier)
        await model.pollAccessPackages(Sample.tenantKey)
        let notes = await notifier.notifications
        #expect(notes.count == 1)
        #expect(notes[0].title == "Access package approved")
        #expect(notes[0].body == "Pkg in Contoso")
        let expiries = await notifier.packageExpiries
        #expect(expiries.map(\.id) == ["a1"])
        #expect(expiries[0].packageName == "Pkg" && expiries[0].tenantName == "Contoso")
        cleanup(model)
    }

    @Test func pollIfDueHonoursTheThrottleUnlessForced() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        await http.on("GET", "assignmentRequests/filterByCurrentUser", body: Data(#"{"value":[]}"#.utf8))
        await http.on("GET", "assignments/filterByCurrentUser", body: Data(#"{"value":[]}"#.utf8))
        let model = await modelWithTenant(http: http, notifier: notifier)
        await model.pollAccessPackagesIfDue()
        #expect(await http.requests(matching: "assignmentRequests").count == 1)
        await model.pollAccessPackagesIfDue()
        #expect(await http.requests(matching: "assignmentRequests").count == 1)
        await model.pollAccessPackagesIfDue(force: true)
        #expect(await http.requests(matching: "assignmentRequests").count == 2)
        cleanup(model)
    }

    @Test func tenantsWithoutTheScopeAreNeverPolled() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]   // accessPackagesAvailable nil
        let model = await makeModel(state: state, http: http, online: true, notifier: notifier)
        await model.pollAccessPackagesIfDue(force: true)
        // Bootstrap's own online refresh reads roles for the tenant unconditionally and records
        // those requests too; what this test verifies is that access packages specifically were
        // never polled, so it filters to the entitlement management path rather than asserting
        // `http.requests.isEmpty`.
        #expect(await http.requests(matching: "entitlementManagement").isEmpty)
        cleanup(model)
    }

    @Test func consentFailureIsRecordedPerTenantAndKeepsTheOldSnapshot() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        await http.on("GET", "assignmentRequests/filterByCurrentUser", status: 403, body: Data(#"{"error":{"code":"Authorization_RequestDenied","message":"no"}}"#.utf8))
        let model = await modelWithTenant(http: http, notifier: notifier)
        await model.pollAccessPackages(Sample.tenantKey)
        #expect(model.accessPackageErrors[Sample.tenantKey] == PIMError.consentRequired.userMessage)
        #expect(model.accessPackageSnapshot(Sample.tenantKey) == nil)
        cleanup(model)
    }

    @Test func requestingAPackageRefreshesTheSnapshot() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        await http.on("POST", "assignmentRequests", status: 201, body: Data(#"{"id":"new","state":"submitted","assignment":{"accessPackageId":"p"}}"#.utf8))
        await http.on("GET", "assignmentRequests/filterByCurrentUser", body: Data(#"{"value":[{"id":"new","state":"submitted","accessPackage":{"id":"p","displayName":"Pkg"}}]}"#.utf8))
        await http.on("GET", "assignments/filterByCurrentUser", body: Data(#"{"value":[]}"#.utf8))
        let model = await modelWithTenant(http: http, notifier: notifier)
        try await model.requestPackage(Sample.tenantKey, packageId: "p", policyId: nil, justification: "Because")
        #expect(model.accessPackageSnapshot(Sample.tenantKey)?.requests.map(\.id) == ["new"])
        cleanup(model)
    }

    /// `requestPackage` calls `pollAccessPackages` (via `waitForPollThenPoll`), which returns
    /// immediately when a poll for the tenant is already in flight. Without waiting for that poll
    /// to clear, the fresh request would never land in the snapshot. Deterministic per the brief's
    /// fallback: rather than racing a real network poll, the in-flight flag is set and cleared on a
    /// timer so the test does not depend on scheduling order.
    @Test func requestPackageWaitsForAnInFlightPollBeforeRePolling() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        await http.on("POST", "assignmentRequests", status: 201, body: Data(#"{"id":"new","state":"submitted","assignment":{"accessPackageId":"p"}}"#.utf8))
        await http.on("GET", "assignmentRequests/filterByCurrentUser", body: Data(#"{"value":[{"id":"new","state":"submitted","accessPackage":{"id":"p","displayName":"Pkg"}}]}"#.utf8))
        await http.on("GET", "assignments/filterByCurrentUser", body: Data(#"{"value":[]}"#.utf8))
        let model = await modelWithTenant(http: http, notifier: notifier)
        model.accessPackagesPolling = [Sample.tenantKey]
        Task {
            try? await Task.sleep(for: .milliseconds(200))
            model.accessPackagesPolling.remove(Sample.tenantKey)
        }
        try await model.requestPackage(Sample.tenantKey, packageId: "p", policyId: nil, justification: "Because")
        #expect(model.accessPackageSnapshot(Sample.tenantKey)?.requests.map(\.id) == ["new"])
        cleanup(model)
    }

    @Test func newRolesAreMarkedNotifiedAndClearedOnSecondOpen() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]
        let model = await makeModel(state: state, http: http, notifier: notifier)
        let reader = Sample.role(Sample.entraKey, name: "Global Reader")
        let exchange = Sample.role(Sample.key(.entraDirectory(roleDefinitionId: "exchange", directoryScopeId: "/")), name: "Exchange Administrator")

        await model.observeDiscoveredRoles(Sample.tenantKey, discovered: [reader])
        #expect(await notifier.notifications.isEmpty)
        #expect(!model.isNewRole(reader.key))

        await model.observeDiscoveredRoles(Sample.tenantKey, discovered: [reader, exchange])
        let notes = await notifier.notifications
        #expect(notes.count == 1)
        #expect(notes[0].title == "New roles available in Contoso")
        #expect(notes[0].body == "Exchange Administrator")
        #expect(model.isNewRole(exchange.key))

        model.panelOpened()
        #expect(model.isNewRole(exchange.key))
        model.panelOpened()
        #expect(!model.isNewRole(exchange.key))
        cleanup(model)
    }

    @Test func emptyDiscoveryDoesNotBaselineOrNotify() async throws {
        let http = StubHTTPClient(), notifier = RecordingNotifier()
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]
        let model = await makeModel(state: state, http: http, notifier: notifier)
        await model.observeDiscoveredRoles(Sample.tenantKey, discovered: [])
        await model.observeDiscoveredRoles(Sample.tenantKey, discovered: [Sample.role(Sample.entraKey, name: "Global Reader")])
        #expect(await notifier.notifications.isEmpty)
        cleanup(model)
    }

    @Test func windowSearchFiltersByNameAndDescription() {
        let rows = [
            AccessPackagesView.Row(id: "1", name: "Finance Reporting Tools", detail: "Power BI workspace"),
            AccessPackagesView.Row(id: "2", name: "Exchange Operations", detail: "PIM roles for on-call staff"),
        ]
        #expect(AccessPackagesView.filtered(rows, query: "").map(\.id) == ["1", "2"])
        #expect(AccessPackagesView.filtered(rows, query: "power").map(\.id) == ["1"])
        #expect(AccessPackagesView.filtered(rows, query: "ON-CALL").map(\.id) == ["2"])
        #expect(AccessPackagesView.filtered(rows, query: "nothing").isEmpty)
    }

    @Test func emptyCaptionDistinguishesNoRowsFromNoMatches() {
        #expect(AccessPackagesView.emptyCaption(total: 3, filtered: 2, query: "power", emptyText: "No packages.") == nil)
        #expect(AccessPackagesView.emptyCaption(total: 0, filtered: 0, query: "", emptyText: "No packages.") == "No packages.")
        #expect(AccessPackagesView.emptyCaption(total: 3, filtered: 0, query: "nothing", emptyText: "No packages.")
            == "No matches for \u{201C}nothing\u{201D}.")
    }
}
