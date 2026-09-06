import Foundation
import ElevateCore

/// Scriptable provider. `failures` is consumed one error per activate call, in order, before succeeding.
actor FakeProviderState {
    var failures: [PIMError] = []
    var activated: [ActivationRequest] = []
    var deactivated: [ActiveAssignment] = []
    var cancelled: [ActiveAssignment] = []
    var order: [String] = []
    func pushFailure(_ e: PIMError) { failures.append(e) }
    func nextFailure() -> PIMError? { failures.isEmpty ? nil : failures.removeFirst() }
    func recordActivate(_ r: ActivationRequest) { activated.append(r); order.append("\(r.roleKey.tenantId):\(r.justification)") }
    func recordDeactivate(_ a: ActiveAssignment) { deactivated.append(a) }
    func recordCancel(_ a: ActiveAssignment) { cancelled.append(a) }
}

struct FakeProvider: PIMProvider {
    let kind: RoleScopeKind
    let scopes = ["scope"]
    let state = FakeProviderState()

    func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole] { [] }
    func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment] { [] }
    func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy { .manualDefault }

    func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment {
        if let e = await state.nextFailure() { throw e }
        await state.recordActivate(request)
        if request.justification == "approve-me" {
            return ActiveAssignment(roleKey: request.roleKey, assignmentId: "p", startDateTime: .now, endDateTime: nil, status: .pendingApproval)
        }
        return ActiveAssignment(roleKey: request.roleKey, assignmentId: "a", startDateTime: .now,
                                endDateTime: Date().addingTimeInterval(TimeInterval(request.duration.components.seconds)), status: .active)
    }

    func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws {
        if let e = await state.nextFailure() { throw e }
        await state.recordDeactivate(assignment)
    }

    func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws {
        if let e = await state.nextFailure() { throw e }
        await state.recordCancel(assignment)
    }
}
