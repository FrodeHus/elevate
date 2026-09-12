import Foundation

/// Immutable ownership snapshots, independent of subsequent profile edits and overlapping profiles.
public struct ProfileRun: Codable, Hashable, Sendable, Identifiable {
    public struct Entry: Codable, Hashable, Sendable {
        public var assignment: ActiveAssignment
        public var displayName: String
        public var completed: Bool
        public init(assignment: ActiveAssignment, displayName: String, completed: Bool = false) {
            self.assignment = assignment; self.displayName = displayName; self.completed = completed
        }

        public func disposition(current: ActiveAssignment?, now: Date) -> Disposition {
            if completed { return .completed }
            if current == nil, assignment.status != .active,
               assignment.endDateTime.map({ $0 > now }) ?? true {
                return .blocked("Awaiting active assignment confirmation")
            }
            guard let current, current.endDateTime.map({ $0 > now }) ?? true else {
                return .skipped("Already inactive or expired")
            }
            guard assignment.assignmentId?.isEmpty == false || assignment.scheduleId?.isEmpty == false else {
                return .blocked("Assignment identity could not be verified")
            }
            if assignment.endDateTime == nil, assignment.status != .active, current.status == .active {
                return .blocked("Original activation interval could not be verified; deactivate this role individually")
            }
            guard matches(current) else { return .skipped("Assignment replaced") }
            guard current.status == .active else { return .blocked("Awaiting active assignment confirmation") }
            let remaining = 300 - now.timeIntervalSince(current.startDateTime)
            if remaining > 0 { return .blocked("Can be deactivated in \(Int(remaining.rounded(.up))) s (minimum activation period)") }
            return .ready
        }

        public func matches(_ current: ActiveAssignment) -> Bool {
            guard current.roleKey == assignment.roleKey else { return false }
            // A later extension can retain the schedule id. Compare the original active interval,
            // allowing only the precision lost in provider JSON timestamps.
            guard floor(current.startDateTime.timeIntervalSince1970) == floor(assignment.startDateTime.timeIntervalSince1970),
                  current.endDateTime.map({ floor($0.timeIntervalSince1970) }) == assignment.endDateTime.map({ floor($0.timeIntervalSince1970) }) else { return false }
            if let schedule = assignment.scheduleId, !schedule.isEmpty {
                return current.scheduleId == schedule
            }
            if let requestId = current.activationRequestId, !requestId.isEmpty {
                return requestId == (assignment.activationRequestId ?? assignment.assignmentId)
            }
            // Legacy/fake assignments without provider correlation must match all immutable fields.
            return assignment.assignmentId?.isEmpty == false && current.assignmentId == assignment.assignmentId
                && current.startDateTime == assignment.startDateTime && current.endDateTime == assignment.endDateTime
        }
    }
    public enum Disposition: Equatable, Sendable {
        case ready, completed, blocked(String), skipped(String)
    }
    public var id: UUID
    public var profileId: UUID
    public var profileName: String
    public var startedAt: Date
    public var entries: [Entry]
    public init(id: UUID = UUID(), profileId: UUID, profileName: String, startedAt: Date = .now, entries: [Entry] = []) {
        self.id = id; self.profileId = profileId; self.profileName = profileName
        self.startedAt = startedAt; self.entries = entries
    }
}
