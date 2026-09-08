import Foundation

/// Something worth telling the user about between two polls of a tenant's access packages.
public enum AccessPackageEvent: Hashable, Sendable {
    case approved(AccessPackageRequest)
    case denied(AccessPackageRequest)
    case deliveryFailed(AccessPackageRequest)
    case revoked(AccessPackageAssignment)
    case expired(AccessPackageAssignment)

    public var packageName: String {
        switch self {
        case .approved(let r), .denied(let r), .deliveryFailed(let r): r.packageName
        case .revoked(let a), .expired(let a): a.packageName
        }
    }
}

/// Pure comparison of two snapshots. A nil `previous` is the first sight of a tenant and never
/// notifies: everything present then is baseline, not news.
public enum AccessPackageDiff {
    public static func events(previous: AccessPackageSnapshot?, current: AccessPackageSnapshot, now: Date) -> [AccessPackageEvent] {
        guard let previous else { return [] }
        var out: [AccessPackageEvent] = []

        // Requests: only a request we already knew in an open state can transition into news.
        let previousStates = Dictionary(previous.requests.map { ($0.id, $0.state) }, uniquingKeysWith: { a, _ in a })
        for r in current.requests {
            guard let was = previousStates[r.id], was.isOpen, was != r.state else { continue }
            switch r.state {
            case .delivered: out.append(.approved(r))
            case .denied: out.append(.denied(r))
            case .deliveryFailed: out.append(.deliveryFailed(r))
            default: break
            }
        }

        // Assignments: a delivered one that is gone or no longer delivered is either revoked or
        // expired. Graph's own "expired" state wins; otherwise the end date decides, so an
        // assignment that vanished after its end date reads as expired, not revoked.
        let currentById = Dictionary(current.assignments.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        for a in previous.assignments where a.state == .delivered {
            let still = currentById[a.id]?.state
            guard still != .delivered else { continue }
            if still == .expired {
                out.append(.expired(a))
            } else if let end = a.expiresAt, end <= now {
                out.append(.expired(a))
            } else {
                out.append(.revoked(a))
            }
        }
        return out
    }
}
