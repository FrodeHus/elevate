import Foundation

/// An entitlement management access package the signed-in user may request.
public struct AccessPackage: Codable, Hashable, Sendable, Identifiable {
    public let id: String
    public var displayName: String
    public var description: String?
    public var isHidden: Bool

    public init(id: String, displayName: String, description: String? = nil, isHidden: Bool = false) {
        self.id = id
        self.displayName = displayName
        self.description = description
        self.isHidden = isHidden
    }
}

/// Graph's `accessPackageAssignmentRequestState`, plus `unknown` for values this build has not seen.
public enum AccessPackageRequestState: String, Codable, Hashable, Sendable, CaseIterable {
    case submitted, pendingApproval, delivering, delivered, deliveryFailed, denied, scheduled, canceled, partiallyDelivered, unknown

    /// Case-insensitive; anything unrecognised is `unknown` so one new value never fails a page.
    public static func parse(_ raw: String?) -> AccessPackageRequestState {
        guard let raw else { return .unknown }
        return allCases.first { $0.rawValue.caseInsensitiveCompare(raw) == .orderedSame } ?? .unknown
    }

    /// Requested tab: the request has not reached a final state.
    public var isOpen: Bool {
        switch self {
        case .submitted, .pendingApproval, .delivering, .scheduled, .partiallyDelivered: true
        default: false
        }
    }

    /// Declined tab: the request ended without access.
    public var isDeclined: Bool {
        switch self {
        case .denied, .deliveryFailed, .canceled: true
        default: false
        }
    }

    /// Graph accepts a cancel only before delivery starts.
    public var isCancellable: Bool { self == .submitted || self == .pendingApproval }
}

/// One of the signed-in user's own access package requests.
public struct AccessPackageRequest: Codable, Hashable, Sendable, Identifiable {
    public let id: String
    public var packageId: String
    public var packageName: String
    public var requestType: String
    public var state: AccessPackageRequestState
    /// Graph's free-text status, shown for failed and canceled requests.
    public var status: String?
    public var justification: String?
    public var createdAt: Date?
    public var completedAt: Date?
    public var policyId: String?

    public init(id: String, packageId: String, packageName: String, requestType: String, state: AccessPackageRequestState,
                status: String? = nil, justification: String? = nil, createdAt: Date? = nil, completedAt: Date? = nil, policyId: String? = nil) {
        self.id = id
        self.packageId = packageId
        self.packageName = packageName
        self.requestType = requestType
        self.state = state
        self.status = status
        self.justification = justification
        self.createdAt = createdAt
        self.completedAt = completedAt
        self.policyId = policyId
    }
}

public enum AccessPackageAssignmentState: String, Codable, Hashable, Sendable, CaseIterable {
    case delivering, delivered, expired, unknown

    public static func parse(_ raw: String?) -> AccessPackageAssignmentState {
        guard let raw else { return .unknown }
        return allCases.first { $0.rawValue.caseInsensitiveCompare(raw) == .orderedSame } ?? .unknown
    }
}

/// An access package currently (or formerly) assigned to the signed-in user.
public struct AccessPackageAssignment: Codable, Hashable, Sendable, Identifiable {
    public let id: String
    public var packageId: String
    public var packageName: String
    public var state: AccessPackageAssignmentState
    public var policyName: String?
    public var expiresAt: Date?

    public init(id: String, packageId: String, packageName: String, state: AccessPackageAssignmentState,
                policyName: String? = nil, expiresAt: Date? = nil) {
        self.id = id
        self.packageId = packageId
        self.packageName = packageName
        self.state = state
        self.policyName = policyName
        self.expiresAt = expiresAt
    }
}

/// One policy the signed-in user may request a package under, from `getApplicablePolicyRequirements`.
public struct PolicyRequirement: Codable, Hashable, Sendable, Identifiable {
    /// The policy id.
    public let id: String
    public var displayName: String
    public var description: String?
    public var isApprovalRequired: Bool
    /// True when the policy asks questions; Elevate hands such requests to the My Access portal.
    public var requiresAnswers: Bool

    public init(id: String, displayName: String, description: String? = nil, isApprovalRequired: Bool, requiresAnswers: Bool) {
        self.id = id
        self.displayName = displayName
        self.description = description
        self.isApprovalRequired = isApprovalRequired
        self.requiresAnswers = requiresAnswers
    }
}

/// What one poll of a tenant returned. Persisted so the next poll can diff against it.
public struct AccessPackageSnapshot: Codable, Hashable, Sendable {
    public var requests: [AccessPackageRequest]
    public var assignments: [AccessPackageAssignment]

    public init(requests: [AccessPackageRequest] = [], assignments: [AccessPackageAssignment] = []) {
        self.requests = requests
        self.assignments = assignments
    }
}
