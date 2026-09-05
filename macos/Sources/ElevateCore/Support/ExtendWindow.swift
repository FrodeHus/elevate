import Foundation

/// Extend is offered only near the end of an activation; earlier it would just shorten the remaining time.
public enum ExtendWindow {
    public static func canExtend(_ assignment: ActiveAssignment, policy: RolePolicy, now: Date, within: TimeInterval = 900) -> Bool {
        // An Extend deactivates first; with approval required the re-activation would only be pending,
        // leaving the user without access in the meantime.
        guard !policy.requiresApproval else { return false }
        guard assignment.status == .active, let end = assignment.endDateTime else { return false }
        let left = end.timeIntervalSince(now)
        return left > 0 && left <= within
    }
}
