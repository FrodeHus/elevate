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
        ForEach(model.managedTenantWarnings, id: \.self) { Text($0).font(.caption).foregroundStyle(.orange) }
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
