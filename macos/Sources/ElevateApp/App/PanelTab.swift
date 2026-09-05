import Foundation

/// Which list the panel shows. Persisted so the panel reopens where it was left.
enum PanelTab: String, CaseIterable, Sendable {
    case roles, groups
    var title: String { self == .roles ? "Roles" : "Groups" }
}
