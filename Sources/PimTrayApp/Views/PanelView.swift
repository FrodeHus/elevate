import SwiftUI
import PimTrayCore

struct PanelView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    @State private var contentHeight: CGFloat = 0

    var body: some View {
        @Bindable var model = model
        VStack(spacing: 0) {
            header
            Divider()
            if let notice = model.notice {
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "exclamationmark.circle").foregroundStyle(.orange)
                    Text(notice).font(.caption).textSelection(.enabled)
                    Spacer()
                    Button { model.notice = nil } label: { Image(systemName: "xmark") }
                        .buttonStyle(.borderless).help("Dismiss")
                }
                .padding(.horizontal, 12).padding(.vertical, 6)
                .background(.orange.opacity(0.12))
                Divider()
            }
            if let fatal = model.startupError {
                ContentUnavailableView("PimTray cannot start", systemImage: "exclamationmark.triangle", description: Text(fatal))
                    .frame(height: 200)
            } else if model.identities.isEmpty {
                ContentUnavailableView("No accounts", systemImage: "person.crop.circle.badge.plus",
                                       description: Text("Add an account to see your PIM roles."))
                    .frame(height: 160)
            } else {
                // A ScrollView in a MenuBarExtra window reports no ideal height, so the panel would collapse
                // to zero; size it from the measured content instead, capped so long lists scroll.
                ScrollView {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(model.identities) { identity in
                            IdentitySection(identity: identity)
                        }
                    }
                    .padding(10)
                    .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { contentHeight = $0 }
                }
                .frame(height: min(max(contentHeight, 44), 520))
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
