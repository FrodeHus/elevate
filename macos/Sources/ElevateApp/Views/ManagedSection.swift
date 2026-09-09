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

/// The list-valued managed rows (sign-in methods, tenants, profiles). Empty until the tasks that
/// introduce those keys in the app layer fill it in.
struct ManagedListRows: View {
    @Environment(AppModel.self) private var model

    var body: some View { EmptyView() }
}
