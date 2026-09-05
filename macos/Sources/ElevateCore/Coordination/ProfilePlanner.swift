import Foundation

/// One line of a profile run: what will happen to an entry and with which duration.
public struct ProfilePlanItem: Hashable, Sendable, Identifiable {
    public enum Disposition: Hashable, Sendable { case activate, alreadyActive, pending, notEligible }
    public let roleKey: RoleKey
    public let role: EligibleRole?
    public var duration: Duration
    public let disposition: Disposition
    public var id: RoleKey { roleKey }
    public init(roleKey: RoleKey, role: EligibleRole?, duration: Duration, disposition: Disposition) {
        self.roleKey = roleKey; self.role = role; self.duration = duration; self.disposition = disposition
    }
}

public enum ProfilePlanner {
    /// Duration: the entry's last run, else the role's remembered duration, else the policy default; never above the maximum.
    public static func plan(_ profile: ActivationProfile, roles: [RoleKey: EligibleRole],
                            active: [RoleKey: ActiveAssignment], memory: [RoleKey: RoleMemory]) -> [ProfilePlanItem] {
        profile.entries.map { entry in
            let role = roles[entry.roleKey]
            let policy = role?.policy ?? .manualDefault
            let wanted = entry.lastDuration ?? memory[entry.roleKey]?.lastDuration ?? policy.defaultDuration
            let duration = min(wanted, policy.maximumDuration)
            let disposition: ProfilePlanItem.Disposition
            if role == nil { disposition = .notEligible }
            else if let a = active[entry.roleKey] {
                switch a.status {
                case .active: disposition = .alreadyActive
                case .pendingApproval, .pendingProvisioning: disposition = .pending
                case .failed: disposition = .activate
                }
            } else { disposition = .activate }
            return ProfilePlanItem(roleKey: entry.roleKey, role: role, duration: duration, disposition: disposition)
        }
    }
}
