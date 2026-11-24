import Foundation
import Testing
import ElevateCore
@testable import Elevate

@MainActor
struct AppModelHomeTests {
    private static let otherTenant = TenantKey(identityId: Sample.identityId, tenantId: "tenant-2")
    private static var otherKey: RoleKey { Sample.key(.entraDirectory(roleDefinitionId: "r2", directoryScopeId: "/"), tenantId: "tenant-2") }

    private func makeHomeModel() async -> AppModel {
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant(), Sample.tenant(tenantId: "tenant-2", name: "Fabrikam")]
        let model = await makeModel(state: state)
        model.roles[Sample.tenantKey] = [
            Sample.role(Sample.entraKey, name: "Global Reader"),
            Sample.role(Sample.azureKey, name: "Owner"),
            Sample.role(Sample.groupKey, name: "Platform Admins"),
        ]
        return model
    }

    /// Started long enough ago that Entra's five minute lock is over.
    private func running(_ key: RoleKey, endsIn seconds: TimeInterval, status: ActiveAssignment.Status = .active) -> ActiveAssignment {
        ActiveAssignment(roleKey: key, assignmentId: "a", startDateTime: Date().addingTimeInterval(-3600),
                         endDateTime: Date().addingTimeInterval(seconds), status: status)
    }

    private func approval(_ id: String, tenant: TenantKey) -> ApprovalRequest {
        ApprovalRequest(id: id, tenantKey: tenant, kind: .entraDirectory, action: .activate, targetName: id, requesterName: "Sam")
    }

    @Test func activeNowListsEveryKindWhateverTheTab() async {
        let model = await makeHomeModel()
        model.active = [Sample.entraKey: Sample.assignment(Sample.entraKey), Sample.azureKey: Sample.assignment(Sample.azureKey),
                        Sample.groupKey: Sample.assignment(Sample.groupKey)]
        model.panelTab = .azure
        #expect(model.activeAssignmentsOrdered.count == 3)
        cleanup(model)
    }

    @Test func activeAssignmentsCanBeNarrowedToOneTenant() async {
        let model = await makeHomeModel()
        model.active = [Sample.entraKey: Sample.assignment(Sample.entraKey), Self.otherKey: Sample.assignment(Self.otherKey)]
        #expect(model.activeAssignments(in: Self.otherTenant).map(\.roleKey) == [Self.otherKey])
        #expect(model.activeAssignments(in: nil).count == 2)
        #expect(model.totalActiveCount == 2)
        cleanup(model)
    }

    @Test func approvalsCanBeNarrowedToOneTenant() async {
        let model = await makeHomeModel()
        model.approvals = [Sample.tenantKey: [.entraDirectory: [approval("a", tenant: Sample.tenantKey)]],
                           Self.otherTenant: [.entraDirectory: [approval("b", tenant: Self.otherTenant)]]]
        #expect(model.approvals(in: Self.otherTenant).map(\.id) == ["b"])
        #expect(model.approvals(in: nil).count == 2)
        cleanup(model)
    }

    @Test func heroIsTheSoonestExpiryInsideTheExtendWindow() async {
        let model = await makeHomeModel()
        model.active = [Sample.entraKey: running(Sample.entraKey, endsIn: 600)]
        #expect(model.heroAssignment?.roleKey == Sample.entraKey)
        cleanup(model)
    }

    @Test func noHeroOutsideTheExtendWindow() async {
        let model = await makeHomeModel()
        model.active = [Sample.entraKey: running(Sample.entraKey, endsIn: 3600)]
        #expect(model.heroAssignment == nil)
        cleanup(model)
    }

    @Test func noHeroWhenTheSoonestRowIsPending() async {
        let model = await makeHomeModel()
        // ActiveSummary puts active rows first, so a pending row leads only when nothing is active.
        model.active = [Sample.entraKey: running(Sample.entraKey, endsIn: 60, status: .pendingApproval)]
        #expect(model.heroAssignment == nil)
        cleanup(model)
    }

    @Test func homeIsEmptyWithNothingActiveAndNoApprovals() async {
        let model = await makeHomeModel()
        #expect(model.homeIsEmpty)
        #expect(model.panelContent == .roleList)
        cleanup(model)
    }

    @Test func sidebarMarkPrefersActiveCountOverError() async {
        let model = await makeHomeModel()
        #expect(model.sidebarMark(for: Sample.tenantKey) == .none)
        model.tenantErrors[Sample.tenantKey] = "boom"
        #expect(model.sidebarMark(for: Sample.tenantKey) == .error)
        model.active = [Sample.entraKey: Sample.assignment(Sample.entraKey), Sample.azureKey: Sample.assignment(Sample.azureKey)]
        #expect(model.sidebarMark(for: Sample.tenantKey) == .active(2))
        cleanup(model)
    }
}
