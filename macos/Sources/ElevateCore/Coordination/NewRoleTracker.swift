import Foundation

/// Remembers which eligible roles a tenant has shown before, so the panel can mark additions as
/// new and the app can notify about them. Pure state: the model calls `observe` after each
/// discovery and `panelOpened` each time the panel opens.
public struct NewRoleTracker: Codable, Hashable, Sendable {
    /// Every role key seen in the most recent discovery. Empty means "never baselined".
    public var seen: Set<RoleKey> = []
    /// Roles added since the baseline that the panel still marks.
    public var new: Set<RoleKey> = []
    /// Panel opens since `new` became non-empty; the marker clears at two.
    public var shownOpens: Int = 0

    public init() {}

    /// Records a discovery. Returns the additions, in no particular order. An empty discovery is
    /// ignored (a failed or consent-blocked read must not baseline away real roles), and the
    /// first non-empty discovery only baselines.
    @discardableResult
    public mutating func observe(discovered: Set<RoleKey>) -> [RoleKey] {
        guard !discovered.isEmpty else { return [] }
        defer { seen = discovered }
        guard !seen.isEmpty else { return [] }
        let added = discovered.subtracting(seen)
        new.formUnion(added)
        // A role that disappeared stops being "new"; it becomes new again if it returns.
        new.formIntersection(discovered)
        return Array(added)
    }

    /// Counts a panel open while something is marked; the second one clears the marker.
    public mutating func panelOpened() {
        guard !new.isEmpty else { return }
        shownOpens += 1
        if shownOpens >= 2 {
            new.removeAll()
            shownOpens = 0
        }
    }

    public func isNew(_ key: RoleKey) -> Bool { new.contains(key) }
}
