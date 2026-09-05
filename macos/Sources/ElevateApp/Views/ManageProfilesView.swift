import SwiftUI
import ElevateCore

struct ManageProfilesView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openWindow) private var openWindow
    @State private var names: [UUID: String] = [:]
    /// Name of the profile the last "Edit" loaded. The work happens in the menu bar panel, which is
    /// closed while this window is up, so say so instead of leaving the button looking inert.
    @State private var editingHint: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Profiles").font(.title3.weight(.semibold))
            if model.profiles.isEmpty {
                VStack {
                    Text("No profiles yet. Select roles in the panel and choose \"Save as profile…\".")
                        .font(.caption).foregroundStyle(.secondary)
                        .multilineTextAlignment(.center).fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxWidth: .infinity, minHeight: 160)
            } else {
                List {
                    ForEach(model.profiles) { p in
                        HStack(spacing: 8) {
                            Image(systemName: "line.3.horizontal").foregroundStyle(.tertiary)
                            TextField("Name", text: Binding(get: { names[p.id] ?? p.name }, set: { names[p.id] = $0 }))
                                .textFieldStyle(.plain)
                                .onSubmit { commit(p.id) }
                            Text(ProfileSummary.caption(entries: p.entries)).font(.caption).foregroundStyle(.secondary)
                            Spacer()
                            Button("Run") { commitAll(); model.requestRun(p.id); open(.runProfile(p.id)) }.controlSize(.small)
                            Button("Edit") {
                                commitAll()
                                model.beginEditing(profileId: p.id)
                                editingHint = names[p.id] ?? p.name
                            }
                            .controlSize(.small)
                            .help("Reopens the selection in the panel; use \"Update profile\" when done")
                            Button(role: .destructive) { model.deleteProfile(id: p.id) } label: { Image(systemName: "trash") }
                                .controlSize(.small).accessibilityLabel("Delete \(p.name)")
                        }
                    }
                    .onMove { from, to in model.moveProfile(fromOffsets: from, toOffset: to) }
                }
                .frame(minHeight: 160)
            }
            if let editingHint {
                Text("\"\(editingHint)\" is loaded into the panel's selection. Open the Elevate menu, adjust the ticks across the Entra, Azure and Groups tabs, then press Update profile.")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack { Spacer(); Button("Done") { commitAll(); dismiss() }.keyboardShortcut(.defaultAction) }
        }
        .padding(16).frame(width: 520)
    }

    private func commit(_ id: UUID) { if let n = names[id] { model.renameProfile(id: id, name: n) } }
    private func commitAll() { for id in names.keys { commit(id) } }
    private func open(_ route: PanelRoute) { openWindow(value: route); NSApp.activate(ignoringOtherApps: true) }
}
