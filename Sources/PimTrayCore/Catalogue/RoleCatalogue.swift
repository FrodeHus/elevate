import Foundation

public struct CatalogueRole: Codable, Hashable, Sendable, Identifiable {
    public let templateId: String
    public let displayName: String
    public let description: String
    public let isPrivileged: Bool
    public var id: String { templateId }
}

public enum RoleCatalogue {
    /// Built-in Entra directory roles bundled with the app. Regenerate with `Scripts/update-role-catalogue.pl`.
    public static func entraBuiltInRoles() throws -> [CatalogueRole] {
        guard let url = Bundle.module.url(forResource: "EntraBuiltInRoles", withExtension: "json") else {
            throw PIMError.unexpected(status: 0, body: "EntraBuiltInRoles.json missing from bundle")
        }
        let roles = try JSONDecoder().decode([CatalogueRole].self, from: Data(contentsOf: url))
        return roles.sorted { $0.displayName < $1.displayName }
    }
}
