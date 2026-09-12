import Foundation

/// A Microsoft admin portal that can be opened straight into one tenant, for the tenant menu's
/// "Open…" submenu. The Ibiza shells (Azure, Entra, Intune) take the tenant id as the first path
/// segment; the Microsoft 365 security shells (Defender, Purview) take it as `?tid=`. The
/// Microsoft 365 admin center has no tenant-in-URL form and is deliberately absent. None of
/// these carry the signed-in account: the browser session decides, and Microsoft's sign-in page
/// prompts with the tenant already fixed when it has none.
public enum AdminPortal: String, CaseIterable, Sendable {
    case azure, entra, intune, defender, purview

    /// Menu title, in the order `allCases` lists them.
    public var title: String {
        switch self {
        case .azure: "Azure Portal"
        case .entra: "Entra admin center"
        case .intune: "Intune admin center"
        case .defender: "Defender Portal"
        case .purview: "Purview Portal"
        }
    }

    /// The portal opened in `tenantId`.
    public func url(tenantId: String) -> URL {
        let tenant = Self.escape(tenantId)
        switch self {
        case .azure, .entra, .intune: return URL(string: "https://\(host)/\(tenant)")!
        case .defender, .purview: return URL(string: "https://\(host)/?tid=\(tenant)")!
        }
    }

    private var host: String {
        switch self {
        case .azure: "portal.azure.com"
        case .entra: "entra.microsoft.com"
        case .intune: "intune.microsoft.com"
        case .defender: "security.microsoft.com"
        case .purview: "purview.microsoft.com"
        }
    }

    /// Percent-encodes everything but RFC 3986 unreserved characters, so the id is safe as a
    /// path segment and as a query value alike. Tenant ids are GUIDs, so this is belt and braces.
    private static func escape(_ s: String) -> String {
        let unreserved = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-._~"))
        return s.addingPercentEncoding(withAllowedCharacters: unreserved) ?? s
    }
}
