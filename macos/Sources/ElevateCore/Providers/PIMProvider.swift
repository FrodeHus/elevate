import Foundation

public protocol PIMProvider: Sendable {
    var kind: RoleScopeKind { get }
    /// Token scopes this provider needs; all against one resource.
    var scopes: [String] { get }
    func eligibleRoles(identity: Identity, tenant: TenantContext) async throws -> [EligibleRole]
    func activeAssignments(identity: Identity, tenant: TenantContext) async throws -> [ActiveAssignment]
    func policy(for role: EligibleRole, identity: Identity) async throws -> RolePolicy
    func activate(_ request: ActivationRequest, identity: Identity) async throws -> ActiveAssignment
    func deactivate(_ assignment: ActiveAssignment, identity: Identity) async throws
    /// Withdraws a request that is still waiting for an approver.
    func cancelPendingRequest(_ assignment: ActiveAssignment, identity: Identity) async throws
}

/// Start times for activations booked ahead of time.
///
/// A start counts as scheduled only when it is more than a minute out: PIM echoes back a start of
/// "now" that is a few seconds off our clock, and that must still read as an immediate activation.
enum ScheduledStart {
    /// How far ahead a start must be before it counts as scheduled rather than immediate.
    static let horizon: TimeInterval = 60

    /// Request statuses that must never read as a booked-ahead activation: one still waiting on an
    /// approver is `.pendingApproval`, and a refused or withdrawn one is nothing at all.
    static let notScheduled: Set<String> = [
        "PendingApproval", "PendingAdminDecision", "PendingApprovalProvisioning",
        "Denied", "AdminDenied", "Failed", "FailedAsResourceIsLocked", "Canceled", "Cancelled", "Revoked",
        "TimedOut", "Invalid",
    ]

    /// True when `status` is one of the above, comparing case-insensitively as the services differ.
    static func isSettledOrPending(_ status: String?) -> Bool {
        guard let status else { return false }
        return notScheduled.contains { $0.caseInsensitiveCompare(status) == .orderedSame }
    }

    static func isFuture(_ date: Date?, now: Date = .now) -> Bool {
        guard let date else { return false }
        return date.timeIntervalSince(now) > horizon
    }

    /// The start to report. A future start the service echoed wins; failing that the future start we
    /// asked for, because the service often answers with the moment it received the request.
    static func effective(response: Date?, requested: Date?, now: Date = .now) -> Date {
        if let response, isFuture(response, now: now) { return response }
        if let requested, isFuture(requested, now: now) { return requested }
        return response ?? requested ?? now
    }

    /// The end time to report: an explicit end the service gave wins; failing that, the parsed ISO 8601
    /// duration added to `start`; failing that, the caller-supplied fallback duration added to `start`.
    static func end(explicit: Date?, duration: String?, start: Date, fallback: Duration? = nil) -> Date? {
        if let explicit { return explicit }
        if let duration, let parsed = ISO8601Duration.parse(duration) {
            return start.addingTimeInterval(TimeInterval(parsed.components.seconds))
        }
        if let fallback {
            return start.addingTimeInterval(TimeInterval(fallback.components.seconds))
        }
        return nil
    }
}
