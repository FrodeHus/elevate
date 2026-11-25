import Foundation

/// Which list the panel shows. Persisted so the panel reopens where it was left.
enum PanelTab: String, CaseIterable, Sendable {
    case all, roles, azure, groups
    var title: String {
        switch self { case .all: "All"; case .roles: "Entra"; case .azure: "Azure"; case .groups: "Groups" }
    }
}
