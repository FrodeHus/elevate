import Foundation

/// The organization's co-branding, resolved from `ManagedConfiguration` into the exact strings each
/// surface renders. Resolving to `nil` is the unbranded case: every caller renders exactly what it
/// rendered before this type existed, so an unbranded install is unaffected by co-branding entirely.
///
/// Elevate's own name is never replaced — the organization's name is added beside it. See
/// `docs/superpowers/specs/2026-09-18-enterprise-cobranding-design.md` §1.
public struct Branding: Hashable, Sendable {
    public let organizationName: String
    public let titleStyle: OrganizationTitleStyle
    public let supportUrl: URL?
    public let supportEmail: String?

    /// The panel header's second line, under "Elevate". `nil` for the `none` style.
    public var headerCaption: String? {
        switch titleStyle {
        case .by: "by \(organizationName)"
        case .managedBy: "Managed by \(organizationName)"
        case .none: nil
        }
    }

    public var firstRunLine: String { "Provided by \(organizationName)." }

    public var supportLabel: String { "\(organizationName) IT" }

    public var hasSupport: Bool { supportUrl != nil || supportEmail != nil }

    /// The single link a "Get help" control opens: the help desk URL, or the address as `mailto:`.
    /// One place on purpose, so no view builds a `mailto:` of its own.
    public var supportDestination: URL? {
        if let supportUrl { return supportUrl }
        guard let supportEmail else { return nil }
        return URL(string: "mailto:\(supportEmail)")
    }

    /// The one-line contact appended to CLI failures. Prefers the URL: a help desk portal lists the
    /// address, but an address does not list the portal.
    public var supportLine: String? {
        guard let target = supportUrl?.absoluteString ?? supportEmail else { return nil }
        return "Need help? \(supportLabel) — \(target)"
    }

    /// One line for the diagnostics report: the name, how it is phrased, and the help desk.
    public var diagnosticsLine: String {
        var parts = ["\(organizationName) (\(titleStyle.rawValue))"]
        if let supportUrl { parts.append(supportUrl.absoluteString) }
        if let supportEmail { parts.append(supportEmail) }
        return parts.joined(separator: " · ")
    }

    public static func resolve(from config: ManagedConfiguration) -> Branding? {
        guard let name = config.organizationName else { return nil }
        return Branding(
            organizationName: name,
            titleStyle: config.organizationTitleStyle ?? .by,
            supportUrl: config.organizationSupportUrl,
            supportEmail: config.organizationSupportEmail
        )
    }
}
