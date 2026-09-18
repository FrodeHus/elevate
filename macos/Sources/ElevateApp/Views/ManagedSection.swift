import SwiftUI
import ElevateCore

/// The Settings section that tells the user which values their organization pushed, and where
/// they came from. Nothing at all when no managed configuration is in effect.
struct ManagedSection: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let managed = model.managed
        if !managed.isEmpty {
            Section {
                if let id = managed.clientId {
                    LabeledContent("Client ID") { Text(id).font(.caption.monospaced()).textSelection(.enabled) }
                }
                if managed.disableUpdateCheck {
                    LabeledContent("Update check") { Text("Disabled") }
                }
                ManagedListRows()
                BrandingRows()
                ForEach(managed.warnings, id: \.self) { Text($0).font(.caption).foregroundStyle(.orange) }
                if let origin = managed.origin {
                    Text("Source: \(origin)").font(.caption2).foregroundStyle(.secondary)
                }
            } header: {
                Label("Managed by your organization", systemImage: "building.2")
            }
        }
    }
}

/// The organization's co-branding, as Settings and Diagnostics report it: the name, how it is
/// phrased beside Elevate's own, and the help desk to contact. Nothing at all when unbranded —
/// `AppModel.branding` is nil unless `OrganizationName` was pushed and accepted.
struct BrandingRows: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if let branding = model.branding {
            LabeledContent("Organization") { Text(branding.organizationName).textSelection(.enabled) }
            LabeledContent("Title style") { Text(branding.titleStyle.rawValue).font(.caption.monospaced()) }
            if let url = branding.supportUrl {
                LabeledContent("Support URL") {
                    Text(url.absoluteString).font(.caption.monospaced()).textSelection(.enabled)
                }
            }
            if let email = branding.supportEmail {
                LabeledContent("Support email") {
                    Text(email).font(.caption.monospaced()).textSelection(.enabled)
                }
            }
            if let destination = branding.supportDestination {
                Link("Get help from \(branding.supportLabel)", destination: destination).font(.caption)
            }
        }
    }
}

/// The list-valued managed rows: which sign-in methods are permitted, which tenants are allowed
/// and which are pinned. Tenant entries are shown as the organization wrote them, with the tenant
/// id they resolved to appended when it differs, and anything that could not be resolved is called
/// out underneath.
struct ManagedListRows: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let managed = model.managed
        if let methods = managed.allowedSignInMethods, !methods.isEmpty {
            LabeledContent("Allowed sign-in methods") { Text(Self.methodNames(methods)) }
        }
        if let tenants = managed.allowedTenants, !tenants.isEmpty {
            LabeledContent("Allowed tenants") { tenantList(tenants) }
        }
        if !managed.pinnedTenants.isEmpty {
            LabeledContent("Pinned tenants") { tenantList(managed.pinnedTenants) }
        }
        if managed.managedProfilesDocument != nil {
            LabeledContent("Managed profiles") { Text("\(model.inlineProfileSet.profiles.count) (inline)") }
        }
        if let url = managed.managedProfilesUrl {
            LabeledContent("Managed profiles URL") {
                Text("\(url.absoluteString) · fetched \(Self.fetched(model.managedProfilesFetchedAt))")
                    .font(.caption.monospaced()).textSelection(.enabled)
            }
        }
        ForEach(model.managedTenantWarnings, id: \.self) { Text($0).font(.caption).foregroundStyle(.orange) }
        ForEach(model.managedProfileWarnings, id: \.self) { Text($0).font(.caption).foregroundStyle(.orange) }
    }

    /// "5 minutes ago", or "never" until the first fetch succeeds.
    private static func fetched(_ date: Date?) -> String {
        guard let date else { return "never" }
        return RelativeDateTimeFormatter().localizedString(for: date, relativeTo: .now)
    }

    private func tenantList(_ entries: [String]) -> some View {
        VStack(alignment: .leading, spacing: 1) {
            ForEach(entries, id: \.self) { entry in
                Text(Self.label(entry, resolved: model.managedTenantIds[entry]))
                    .font(.caption.monospaced()).textSelection(.enabled)
            }
        }
    }

    /// The entry as configured; a domain also shows the tenant id it resolved to.
    private static func label(_ entry: String, resolved: String?) -> String {
        guard let resolved, resolved.caseInsensitiveCompare(entry) != .orderedSame else { return entry }
        return "\(entry) → \(resolved)"
    }

    /// Display names in `SignInMethodKind.allCases` order, so the row reads the same every launch.
    private static func methodNames(_ kinds: Set<SignInMethodKind>) -> String {
        SignInMethodKind.allCases.filter { kinds.contains($0) }.map(Self.displayName).joined(separator: ", ")
    }

    private static func displayName(_ kind: SignInMethodKind) -> String {
        switch kind {
        case .ownApp: SignInMethod.ownApp.displayName
        case .azureCLI: SignInMethod.azureCLI.displayName
        case .azurePowerShell: SignInMethod.azurePowerShell.displayName
        case .custom: SignInMethod.custom(clientId: "").displayName
        }
    }
}
