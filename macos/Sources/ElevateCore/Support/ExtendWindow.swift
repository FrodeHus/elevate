import Foundation

/// Extend is offered only near the end of an activation; earlier it would just shorten the remaining time.
public enum ExtendWindow {
    public static func canExtend(_ assignment: ActiveAssignment, now: Date, within: TimeInterval = 900) -> Bool {
        guard assignment.status == .active, let end = assignment.endDateTime else { return false }
        let left = end.timeIntervalSince(now)
        return left > 0 && left <= within
    }
}
