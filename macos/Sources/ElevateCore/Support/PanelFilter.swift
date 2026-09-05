import Foundation

/// The panel's search box: a substring match over everything a row shows, ignoring case and accents.
public enum PanelFilter {
    public static func isActive(_ query: String) -> Bool {
        !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    public static func matches(query: String, role: EligibleRole, tenantName: String, upn: String) -> Bool {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty else { return true }
        let fields = [role.displayName, role.detail ?? "", role.viaGroup ?? "", tenantName, upn]
        return fields.contains { $0.range(of: q, options: [.caseInsensitive, .diacriticInsensitive]) != nil }
    }
}
