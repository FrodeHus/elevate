import Foundation
import ElevateCore

enum TenantMark: Equatable { case active(Int), error, none }

@MainActor
extension AppModel {
    /// Whether the row's Extend button is offered: past Entra's five minute lock, inside the extend
    /// window, and the role does not need approval.
    func canExtendNow(_ key: RoleKey, now: Date = .now) -> Bool {
        guard let a = assignment(for: key), 300 - now.timeIntervalSince(a.startDateTime) <= 0 else { return false }
        return ExtendWindow.canExtend(a, policy: role(for: key)?.policy ?? .manualDefault, now: now)
    }

    /// The soonest-expiring active row, when it is the one Extend applies to.
    var heroAssignment: ActiveAssignment? {
        activeAssignments(in: nil).first.flatMap { canExtendNow($0.roleKey) ? $0 : nil }
    }

    var homeIsEmpty: Bool { approvals(in: nil).isEmpty && activeRows(in: nil).isEmpty }

    var totalActiveCount: Int { activeAssignments(in: nil).count }

    func sidebarMark(for key: TenantKey) -> TenantMark {
        let n = active.values.count { $0.roleKey.tenantKey == key }
        return n > 0 ? .active(n) : tenantErrors[key] != nil ? .error : .none
    }
}
