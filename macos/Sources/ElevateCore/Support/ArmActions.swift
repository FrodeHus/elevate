import Foundation

/// One entry of an ARM permission set: what it grants and what it takes back.
public struct ArmPermission: Hashable, Sendable, Decodable {
    public let actions: [String]
    public let notActions: [String]

    public init(actions: [String], notActions: [String] = []) {
        self.actions = actions
        self.notActions = notActions
    }

    private enum CodingKeys: String, CodingKey { case actions, notActions }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        actions = try container.decodeIfPresent([String].self, forKey: .actions) ?? []
        notActions = try container.decodeIfPresent([String].self, forKey: .notActions) ?? []
    }
}

/// Matching for Azure RBAC action strings, which are slash-separated operation names where `*`
/// stands for any run of characters — `Microsoft.Compute/*`, `*/read`, or a bare `*`.
///
/// Used to decide whether the permissions ARM reports for the caller already include the ones the
/// activated role definition grants. Both sides are patterns, so a granted `*` covers the literal
/// `*` an Owner definition asks for.
public enum ArmActions {
    /// Whether `pattern` covers `action`. Comparison ignores case, as ARM's own evaluation does.
    public static func covers(pattern: String, action: String) -> Bool {
        let p = Array(pattern.lowercased()), a = Array(action.lowercased())
        var pi = 0, ai = 0, star = -1, resume = 0
        while ai < a.count {
            if pi < p.count, p[pi] == "*" {
                star = pi
                pi += 1
                resume = ai
            } else if pi < p.count, p[pi] == a[ai] {
                pi += 1
                ai += 1
            } else if star >= 0 {
                // Backtrack: let the last star absorb one more character.
                pi = star + 1
                resume += 1
                ai = resume
            } else {
                return false
            }
        }
        while pi < p.count, p[pi] == "*" { pi += 1 }
        return pi == p.count
    }

    /// Whether `action` is granted by `permissions`: some entry's `actions` covers it and none of
    /// that entry's `notActions` takes it back. ARM evaluates each role assignment on its own, so an
    /// exclusion in one entry does not remove what another entry grants.
    public static func grants(_ permissions: [ArmPermission], action: String) -> Bool {
        permissions.contains { permission in
            permission.actions.contains { covers(pattern: $0, action: action) }
                && !permission.notActions.contains { covers(pattern: $0, action: action) }
        }
    }

    /// Whether `permissions` covers everything `required` grants. An empty `required` is not
    /// evidence of anything, so it answers false.
    public static func covers(_ permissions: [ArmPermission], required: [ArmPermission]) -> Bool {
        let wanted = Set(required.flatMap(\.actions).map { $0.lowercased() })
        return !wanted.isEmpty && wanted.allSatisfy { grants(permissions, action: $0) }
    }
}
