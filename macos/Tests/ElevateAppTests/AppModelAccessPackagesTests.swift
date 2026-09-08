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
}
