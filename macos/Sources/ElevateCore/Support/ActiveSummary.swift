import Foundation

/// Order for the "Active now" section: what expires first on top, then what is waiting.
public enum ActiveSummary {
    public static func order(_ assignments: [ActiveAssignment]) -> [ActiveAssignment] {
        let active = assignments.filter { $0.status == .active }
            .sorted { ($0.endDateTime ?? .distantFuture, $0.assignmentId ?? "") < ($1.endDateTime ?? .distantFuture, $1.assignmentId ?? "") }
        let pending = assignments.filter { $0.status == .pendingApproval }
            .sorted { ($0.startDateTime, $0.assignmentId ?? "") < ($1.startDateTime, $1.assignmentId ?? "") }
        let provisioning = assignments.filter { $0.status == .pendingProvisioning }
            .sorted { ($0.startDateTime, $0.assignmentId ?? "") < ($1.startDateTime, $1.assignmentId ?? "") }
        return active + pending + provisioning
    }
}
