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
}
