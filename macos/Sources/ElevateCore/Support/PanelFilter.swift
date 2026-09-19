import Foundation

/// The panel's search box: a substring match over everything a row shows, ignoring case and accents.
public enum PanelFilter {
    public static func isActive(_ query: String) -> Bool {
        !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// One field against the query: an empty query matches everything, otherwise a case- and
    /// diacritic-insensitive substring test.
    public static func matches(query: String, text: String) -> Bool {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty else { return true }
        return text.range(of: q, options: [.caseInsensitive, .diacriticInsensitive]) != nil
    }

    public static func matches(query: String, role: EligibleRole, tenantName: String, upn: String) -> Bool {
        guard isActive(query) else { return true }
        // The whole ARM path too: a role row only shows the scope's caption, but "prod" should find
        // an eligibility whose subscription is named only in the path.
        let path: String
        if case .azureResource(let scope, _) = role.key.scope { path = scope } else { path = "" }
        let fields = [role.displayName, role.detail ?? "", role.viaGroup ?? "", tenantName, upn, path]
        return fields.contains { matches(query: query, text: $0) }
    }
}
