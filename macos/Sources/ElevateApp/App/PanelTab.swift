import Foundation

/// Which list the panel shows. Persisted so the panel reopens where it was left.
enum PanelTab: String, CaseIterable, Sendable {
    case roles, azure, groups
    var title: String {
        switch self { case .roles: "Entra"; case .azure: "Azure"; case .groups: "Groups" }
    }
    /// Singular noun for the bulk button: "Activate 2 roles" / "groups".
    var noun: String { self == .groups ? "group" : "role" }
}
