import Foundation

/// What the menu bar label needs to know, derived from the assignments alone.
public struct PanelStatus: Equatable, Sendable {
    public let activeCount: Int
    public let expiringSoon: Bool
    public let pendingApproval: Bool

    public init(activeCount: Int, expiringSoon: Bool, pendingApproval: Bool) {
        self.activeCount = activeCount
        self.expiringSoon = expiringSoon
        self.pendingApproval = pendingApproval
    }

    /// `expiringSoon` is true when an active assignment ends within `soonWithin` seconds of `now` (and has not ended yet).
    public static func compute(_ assignments: [ActiveAssignment], now: Date, soonWithin: TimeInterval = 300) -> PanelStatus {
        var active = 0, soon = false, pending = false
        for a in assignments {
            switch a.status {
            case .active:
                active += 1
                if let end = a.endDateTime {
                    let left = end.timeIntervalSince(now)
                    if left > 0 && left <= soonWithin { soon = true }
                }
            case .pendingApproval: pending = true
            case .pendingProvisioning, .failed: break
            }
        }
        return PanelStatus(activeCount: active, expiringSoon: soon, pendingApproval: pending)
    }
}
