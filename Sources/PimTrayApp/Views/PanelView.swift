import SwiftUI
import PimTrayCore

struct PanelView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        @Bindable var model = model
        VStack(spacing: 0) {
            header
            Divider()
            if let fatal = model.fatalError {
                ContentUnavailableView("PimTray cannot start", systemImage: "exclamationmark.triangle", description: Text(fatal))
                    .frame(height: 200)
            } else if model.identities.isEmpty {
                ContentUnavailableView("No accounts", systemImage: "person.crop.circle.badge.plus",
                                       description: Text("Add an account to see your PIM roles."))
                    .frame(height: 160)
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 8) {
                        ForEach(model.identities) { identity in
                            IdentitySection(identity: identity)
                        }
                    }
                    .padding(10)
                }
                .frame(maxHeight: 520)
            }
            if model.selectMode {
                Divider()
                Button {
                    openWindow(value: PanelRoute.activate(Array(model.selection).sorted { "\($0)" < "\($1)" }))
                    NSApp.activate(ignoringOtherApps: true)
                } label: {
                    Text("Activate \(model.selection.count) role\(model.selection.count == 1 ? "" : "s")")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .disabled(model.selection.isEmpty)
                .padding(10)
            }
            Divider()
            footer
        }
        .frame(width: 380)
    }

    private var header: some View {
        @Bindable var model = model
        return HStack {
            Text("PimTray").font(.headline)
            Spacer()
            Toggle(isOn: $model.selectMode) { Image(systemName: "checklist") }
                .toggleStyle(.button)
                .help("Select several roles to activate together")
            Button { Task { await model.refreshAll() } } label: { Image(systemName: "arrow.clockwise") }
                .help("Refresh")
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private var footer: some View {
        HStack {
            Button("Add account…") { Task { await model.addAccount() } }
            Spacer()
            Button("Quit") { NSApp.terminate(nil) }
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }
}
