import Foundation

/// What one step of an ARM scope path names.
public enum ArmScopeKind: Int, Hashable, Sendable, CaseIterable, Comparable {
    /// The tenant root, `/`, or a path this parser did not recognise.
    case unknown
    case managementGroup
    case subscription
    case resourceGroup
    case resource

    public static func < (lhs: ArmScopeKind, rhs: ArmScopeKind) -> Bool { lhs.rawValue < rhs.rawValue }
}

/// One step of an ARM scope path: what it names, the name itself, and the scope string that
/// reaches it. `scope` is a prefix of the scope the segment came from, so it is a usable scope in
/// its own right and can key a node of the tree.
public struct ArmScopeSegment: Hashable, Sendable {
    public let kind: ArmScopeKind
    public let name: String
    public let scope: String

    public init(kind: ArmScopeKind, name: String, scope: String) {
        self.kind = kind
        self.name = name
        self.scope = scope
    }
}

/// Reading Azure Resource Manager scope strings as the hierarchy they already are:
/// `/subscriptions/{id}/resourceGroups/{name}/providers/{ns}/{type}/{name}`. The panel builds its
/// tree from this, so no extra calls are needed to learn the shape.
///
/// One thing the string cannot tell us: which management group a subscription sits under. ARM
/// writes a management group scope as a flat `/providers/Microsoft.Management/managementGroups/{name}`
/// and never repeats it in a subscription's scope, so management groups are roots beside the
/// subscriptions rather than above them.
public enum ArmScope {
    private static let managementGroupPrefix = "/providers/Microsoft.Management/managementGroups/"

    /// The steps of `scope`, outermost first; empty for a nil or root scope.
    public static func segments(_ scope: String?) -> [ArmScopeSegment] {
        let trimmed = (scope ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed != "/" else { return [] }

        let parts = trimmed.split(separator: "/").map(String.init)
        var segments: [ArmScopeSegment] = []
        var path = ""
        var i = 0

        // "/providers/Microsoft.Management/managementGroups/{name}" is a whole scope, not a resource
        // under something: it only ever appears on its own, so it is recognised before the loop.
        if trimmed.lowercased().hasPrefix(managementGroupPrefix.lowercased()), parts.count >= 4 {
            path = managementGroupPrefix + parts[3]
            segments.append(ArmScopeSegment(kind: .managementGroup, name: parts[3], scope: path))
            i = 4
        }

        while i < parts.count {
            let token = parts[i].lowercased()
            if i + 1 < parts.count, token == "subscriptions" {
                path += "/subscriptions/" + parts[i + 1]
                segments.append(ArmScopeSegment(kind: .subscription, name: parts[i + 1], scope: path))
                i += 2
            } else if i + 1 < parts.count, token == "resourcegroups" {
                path += "/resourceGroups/" + parts[i + 1]
                segments.append(ArmScopeSegment(kind: .resourceGroup, name: parts[i + 1], scope: path))
                i += 2
            } else if token == "providers", i + 3 < parts.count {
                // providers/{namespace}/{type}/{name}, then (type, name) pairs for child resources.
                path += "/providers/" + parts[i + 1] + "/" + parts[i + 2] + "/" + parts[i + 3]
                segments.append(ArmScopeSegment(kind: .resource, name: parts[i + 3], scope: path))
                i += 4
                while i + 1 < parts.count {
                    path += "/" + parts[i] + "/" + parts[i + 1]
                    segments.append(ArmScopeSegment(kind: .resource, name: parts[i + 1], scope: path))
                    i += 2
                }
            } else {
                // Something this parser does not know. Keep the rest as one step rather than
                // guessing, so an unfamiliar scope still appears in the tree under a readable name.
                path += "/" + parts[i...].joined(separator: "/")
                segments.append(ArmScopeSegment(kind: .unknown, name: parts[parts.count - 1], scope: path))
                break
            }
        }

        return segments
    }

    /// Whether `scope` is `ancestor` or sits below it. Compared step by step, so
    /// `/subscriptions/abc` does not contain `/subscriptions/abcdef`.
    public static func isAtOrUnder(_ scope: String?, ancestor: String?) -> Bool {
        let above = segments(ancestor)
        // The root contains everything.
        guard !above.isEmpty else { return true }

        let below = segments(scope)
        guard below.count >= above.count else { return false }
        return zip(below, above).allSatisfy { $0.scope.lowercased() == $1.scope.lowercased() }
    }

    /// Whether any step of `scope` is named `name` — the plain form of "under this subscription" or
    /// "under this resource group", where the user names it rather than giving the whole path. A
    /// step's own scope string is accepted too.
    public static func hasSegmentNamed(_ scope: String?, name: String) -> Bool {
        var term = name.trimmingCharacters(in: .whitespacesAndNewlines)
        while term.hasSuffix("/") { term.removeLast() }
        guard !term.isEmpty else { return false }

        return segments(scope).contains {
            $0.name.lowercased() == term.lowercased() || $0.scope.lowercased() == term.lowercased()
        }
    }

    /// Whether `text` matches the glob `pattern`, where `*` absorbs any run of characters including
    /// slashes — so `/subscriptions/*` reaches everything in every subscription, not only the
    /// subscriptions themselves. The whole string must match, and case is ignored, as everywhere
    /// else in ARM.
    public static func matches(pattern: String, text: String?) -> Bool {
        // Same glob ARM uses for action strings; ArmActions already implements it without recursion.
        ArmActions.covers(pattern: pattern, action: text ?? "")
    }

    /// Whether `term` is meant as a glob rather than as plain text.
    public static func looksLikePattern(_ term: String?) -> Bool { (term ?? "").contains("*") }

    /// The display name an Azure role's `detail` caption carries — "Pay-As-You-Go · subscription"
    /// is written by `AzureResourceProvider.caption`, and only the name part names the scope.
    public static func displayName(detail: String?) -> String? {
        let text = (detail ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }

        let name = text.range(of: " · ", options: .backwards).map {
            String(text[text.startIndex..<$0.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
        } ?? text
        return name.isEmpty ? nil : name
    }

    /// What to call a step of the path in the panel, matching the provider's captions.
    public static func label(_ kind: ArmScopeKind) -> String {
        switch kind {
        case .managementGroup: "management group"
        case .subscription: "subscription"
        case .resourceGroup: "resource group"
        case .resource: "resource"
        case .unknown: "scope"
        }
    }

    /// The scope shortened for a narrow row: the last `steps` steps, with a leading ellipsis for
    /// what was dropped. Truncating from the left keeps the leaf, which is the part that tells two
    /// long paths apart.
    public static func tail(_ scope: String?, steps: Int = 2) -> String {
        let segments = segments(scope)
        guard !segments.isEmpty else { return "/" }

        let take = max(1, steps)
        guard segments.count > take else {
            return scope?.trimmingCharacters(in: .whitespacesAndNewlines) ?? "/"
        }

        return "…/" + segments.suffix(take).map(\.name).joined(separator: "/")
    }
}
