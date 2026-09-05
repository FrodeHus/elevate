import Foundation

public struct RolePolicy: Codable, Hashable, Sendable {
    public var defaultDuration: Duration
    public var maximumDuration: Duration
    public var requiresJustification: Bool
    public var requiresTicket: Bool
    public var requiresMFA: Bool
    public var requiresApproval: Bool
    /// Conditional Access authentication context id (e.g. "c1") the activation token must carry,
    /// from the `AuthenticationContext_EndUser_Assignment` rule. nil when the role has none.
    public var authenticationContext: String?

    public init(defaultDuration: Duration, maximumDuration: Duration, requiresJustification: Bool,
                requiresTicket: Bool, requiresMFA: Bool, requiresApproval: Bool, authenticationContext: String? = nil) {
        self.defaultDuration = defaultDuration
        self.maximumDuration = maximumDuration
        self.requiresJustification = requiresJustification
        self.requiresTicket = requiresTicket
        self.requiresMFA = requiresMFA
        self.requiresApproval = requiresApproval
        self.authenticationContext = authenticationContext
    }

    public static let manualDefault = RolePolicy(
        defaultDuration: .seconds(3600), maximumDuration: .seconds(8 * 3600),
        requiresJustification: true, requiresTicket: false, requiresMFA: false, requiresApproval: false)
}

public enum RoleSource: String, Codable, Hashable, Sendable { case discovered, manual }

public struct EligibleRole: Codable, Hashable, Sendable, Identifiable {
    public let key: RoleKey
    public var displayName: String
    /// Secondary caption, e.g. the Azure scope's display name and type. Nil for Entra roles.
    public var detail: String?
    public var source: RoleSource
    public var policy: RolePolicy
    public var id: RoleKey { key }

    public init(key: RoleKey, displayName: String, detail: String? = nil, source: RoleSource, policy: RolePolicy) {
        self.key = key
        self.displayName = displayName
        self.detail = detail
        self.source = source
        self.policy = policy
    }
}

public struct ActiveAssignment: Codable, Hashable, Sendable, Identifiable {
    public enum Status: Codable, Hashable, Sendable {
        case active, pendingApproval, pendingProvisioning, failed(String)
    }
    public let roleKey: RoleKey
    public var assignmentId: String?
    public var startDateTime: Date
    public var endDateTime: Date?
    public var status: Status
    public var id: RoleKey { roleKey }

    public init(roleKey: RoleKey, assignmentId: String?, startDateTime: Date, endDateTime: Date?, status: Status) {
        self.roleKey = roleKey
        self.assignmentId = assignmentId
        self.startDateTime = startDateTime
        self.endDateTime = endDateTime
        self.status = status
    }
}

public struct TicketInfo: Codable, Hashable, Sendable {
    public var number: String
    public var system: String
    public init(number: String, system: String) { self.number = number; self.system = system }
}

public struct ActivationRequest: Codable, Hashable, Sendable, Identifiable {
    public let roleKey: RoleKey
    public var duration: Duration
    public var justification: String
    public var ticket: TicketInfo?
    /// Authentication context the role's policy demands; the coordinator asks for a token carrying it.
    public var authenticationContext: String?
    public var id: RoleKey { roleKey }

    public init(roleKey: RoleKey, duration: Duration, justification: String, ticket: TicketInfo? = nil,
                authenticationContext: String? = nil) {
        self.roleKey = roleKey
        self.duration = duration
        self.justification = justification
        self.ticket = ticket
        self.authenticationContext = authenticationContext
    }
}
