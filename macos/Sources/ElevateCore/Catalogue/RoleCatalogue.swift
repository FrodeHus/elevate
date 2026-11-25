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

/// Which roles the panel marks with a crown: Entra roles Microsoft flags `isPrivileged`, and the four
/// Azure built-in privileged administrator roles. Groups and access packages are never privileged.
public enum PrivilegedRoles {
    static let azureIds: Set<String> = [
        "8e3af657-a8ff-443c-a75c-2fe8c4bcb635", // Owner
        "b24988ac-6180-42a0-ab88-20f7382dd24c", // Contributor
        "18d7d88d-d35e-4fb5-a5c3-7773c20a72d9", // User Access Administrator
        "f58310d9-a9f6-439a-9e8d-f62e7b41a168", // Role Based Access Control Administrator
    ]

    /// `roleDefinitionId` is the full ARM id or the bare GUID; only its last segment counts.
    public static func isAzure(roleDefinitionId: String) -> Bool {
        azureIds.contains((roleDefinitionId.split(separator: "/").last.map(String.init) ?? "").lowercased())
    }

    private static let entraCatalogue: [String: Bool] = {
        let roles = (try? RoleCatalogue.entraBuiltInRoles()) ?? []
        return Dictionary(roles.map { ($0.templateId.lowercased(), $0.isPrivileged) }, uniquingKeysWith: { a, _ in a })
    }()

    /// For a role the user added by hand: privilege from the bundled catalogue (Entra) or the Azure ids.
    public static func isKnownPrivileged(_ scope: RoleScope) -> Bool {
        switch scope {
        case .entraDirectory(let id, _): entraCatalogue[id.lowercased()] ?? false
        case .azureResource(_, let id): isAzure(roleDefinitionId: id)
        default: false
        }
    }
}
